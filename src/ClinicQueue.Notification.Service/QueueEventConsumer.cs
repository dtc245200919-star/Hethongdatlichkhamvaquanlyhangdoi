using System.Text.Json;
using ClinicQueue.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ClinicQueue.Notification.Service;

public sealed class QueueEventConsumer(
    IConfiguration configuration,
    IHubContext<NotificationHub> hub,
    ILogger<QueueEventConsumer> logger) : BackgroundService
{
    private const string QueueName = RabbitTopology.NotificationQueue;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = CreateConnection();
                using var channel = connection.CreateModel();
                channel.ExchangeDeclare(RabbitTopology.EventsExchange, ExchangeType.Topic, durable: true);
                channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
                channel.QueueBind(QueueName, RabbitTopology.EventsExchange, RabbitTopology.QueueChangedRoutingKey);
                channel.BasicQos(0, 1, false);

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += (_, delivery) => ForwardEvent(channel, delivery, stoppingToken);
                channel.BasicConsume(QueueName, autoAck: false, consumer);
                logger.LogInformation("Notification service is consuming {Queue}", QueueName);

                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification RabbitMQ connection failed; reconnecting in 3 seconds");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private IConnection CreateConnection()
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:HostName"] ?? "localhost",
            Port = int.TryParse(configuration["RabbitMq:Port"], out var port) ? port : 5672,
            UserName = configuration["RabbitMq:UserName"] ?? "guest",
            Password = configuration["RabbitMq:Password"] ?? "guest",
            VirtualHost = configuration["RabbitMq:VirtualHost"] ?? "/",
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };
        return factory.CreateConnection("clinic-notification-service");
    }

    private void ForwardEvent(IModel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<QueueChangedEvent>(delivery.Body.Span, JsonOptions)
                ?? throw new JsonException("Queue change payload was empty.");
            hub.Clients.Group($"doctor-{message.DoctorId:N}")
                .SendAsync("QueueChanged", message, stoppingToken)
                .GetAwaiter()
                .GetResult();
            lock (channel)
            {
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
            }
            logger.LogInformation(
                "Sent queue update for doctor {DoctorId}, appointment {AppointmentId}",
                message.DoctorId,
                message.AppointmentId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            lock (channel)
            {
                if (channel.IsOpen)
                {
                    channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not forward queue update {DeliveryTag}; requeuing it", delivery.DeliveryTag);
            lock (channel)
            {
                if (channel.IsOpen)
                {
                    channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                }
            }
        }
    }
}
