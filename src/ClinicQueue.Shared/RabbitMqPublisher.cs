using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ClinicQueue.Shared;

public static class RabbitTopology
{
    public const string AppointmentRequestsQueue = "clinic.appointment.requests";
    public const string NotificationQueue = "clinic.notifications.signalr";
    public const string EventsExchange = "clinic.events";
    public const string QueueChangedRoutingKey = "queue.changed";
}

public sealed class RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger) : IDisposable
{
    private readonly object _sync = new();
    private IConnection? _connection;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public void PublishAppointmentRequested(AppointmentRequestedEvent message)
    {
        Publish(
            RabbitTopology.AppointmentRequestsQueue,
            "",
            JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions));
    }

    public void PublishQueueChanged(QueueChangedEvent message)
    {
        Publish(
            RabbitTopology.EventsExchange,
            RabbitTopology.QueueChangedRoutingKey,
            JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions));
    }

    private void Publish(string destination, string routingKey, byte[] body)
    {
        lock (_sync)
        {
            if (_connection is not { IsOpen: true })
            {
                _connection?.Dispose();
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
                _connection = factory.CreateConnection("clinic-outbox-publisher");
                logger.LogInformation("Connected to RabbitMQ at {HostName}", factory.HostName);
            }

            using var channel = _connection.CreateModel();
            if (destination == RabbitTopology.EventsExchange)
            {
                channel.ExchangeDeclare(destination, ExchangeType.Topic, durable: true);
                channel.QueueDeclare(RabbitTopology.NotificationQueue, durable: true, exclusive: false, autoDelete: false);
                channel.QueueBind(
                    RabbitTopology.NotificationQueue,
                    RabbitTopology.EventsExchange,
                    RabbitTopology.QueueChangedRoutingKey);
            }
            else
            {
                channel.QueueDeclare(destination, durable: true, exclusive: false, autoDelete: false);
            }

            channel.ConfirmSelect();
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.MessageId = Guid.NewGuid().ToString();
            channel.BasicPublish(destination, routingKey, mandatory: true, properties, body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
