using System.Data;
using System.Text.Json;
using ClinicQueue.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ClinicQueue.Queue.Worker;

public sealed class Worker(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<Worker> logger) : BackgroundService
{
    private const string RetryQueue = "clinic.appointment.retry";
    private const string DeadLetterQueue = "clinic.appointment.dead";
    private const int MaximumRetries = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = CreateConnection();
                using var channel = connection.CreateModel();
                DeclareQueues(channel);
                channel.BasicQos(0, 1, false);

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += (_, delivery) => HandleDelivery(channel, delivery, stoppingToken);
                channel.BasicConsume(RabbitTopology.AppointmentRequestsQueue, autoAck: false, consumer);
                logger.LogInformation("Queue worker is consuming {Queue}", RabbitTopology.AppointmentRequestsQueue);

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
                logger.LogError(exception, "Queue worker connection failed; reconnecting in 3 seconds");
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
        return factory.CreateConnection("clinic-queue-worker");
    }

    private static void DeclareQueues(IModel channel)
    {
        channel.QueueDeclare(RabbitTopology.AppointmentRequestsQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueDeclare(
            RetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            new Dictionary<string, object>
            {
                ["x-message-ttl"] = 5000,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = RabbitTopology.AppointmentRequestsQueue
            });
        channel.QueueDeclare(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
    }

    private void HandleDelivery(IModel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<AppointmentRequestedEvent>(delivery.Body.Span, JsonOptions)
                ?? throw new JsonException("Appointment request payload was empty.");
            ProcessAppointmentAsync(message, stoppingToken).GetAwaiter().GetResult();
            lock (channel)
            {
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
            }
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
            logger.LogError(exception, "Failed to process appointment message {DeliveryTag}", delivery.DeliveryTag);
            try
            {
                SendToRetryOrDeadLetter(channel, delivery);
            }
            catch (Exception publishException)
            {
                logger.LogError(publishException, "Could not route failed message {DeliveryTag}; requeuing it", delivery.DeliveryTag);
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

    private void SendToRetryOrDeadLetter(IModel channel, BasicDeliverEventArgs delivery)
    {
        var retryCount = delivery.BasicProperties.Headers is not null &&
                         delivery.BasicProperties.Headers.TryGetValue("x-retry-count", out var header)
            ? ReadRetryCount(header)
            : 0;
        var destination = retryCount >= MaximumRetries ? DeadLetterQueue : RetryQueue;
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.Headers = new Dictionary<string, object>
        {
            ["x-retry-count"] = retryCount + 1
        };

        lock (channel)
        {
            channel.ConfirmSelect();
            channel.BasicPublish("", destination, mandatory: true, properties, delivery.Body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            channel.BasicAck(delivery.DeliveryTag, multiple: false);
        }

        logger.LogWarning(
            "Routed failed appointment message {DeliveryTag} to {Queue} (attempt {Attempt})",
            delivery.DeliveryTag,
            destination,
            retryCount + 1);
    }

    private async Task ProcessAppointmentAsync(AppointmentRequestedEvent message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedAppointments = await db.Appointments
            .FromSqlInterpolated(
                $"SELECT * FROM \"Appointments\" WHERE \"Id\" = {message.AppointmentId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var appointment = lockedAppointments.SingleOrDefault();

        if (appointment is null || appointment.Status != AppointmentStatus.Pending)
        {
            logger.LogInformation("Ignoring duplicate or stale appointment event {AppointmentId}", message.AppointmentId);
            return;
        }

        var queueDate = DateOnly.FromDateTime(appointment.ScheduledAt);
        var queueNumber = await IncrementQueueCounterAsync(
            db,
            appointment.DoctorId,
            queueDate,
            cancellationToken);

        appointment.QueueNumber = queueNumber;
        appointment.Status = AppointmentStatus.Confirmed;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.QueueChanged,
            Payload = JsonSerializer.Serialize(
                new QueueChangedEvent(
                    appointment.Id,
                    appointment.DoctorId,
                    appointment.RoomId,
                    queueDate,
                    queueNumber,
                    appointment.PatientName,
                    appointment.Status.ToString(),
                    message.CorrelationId),
                JsonOptions),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Assigned queue number {QueueNumber} to appointment {AppointmentId}",
            queueNumber,
            appointment.Id);
    }

    private static async Task<int> IncrementQueueCounterAsync(
        ClinicDbContext db,
        Guid doctorId,
        DateOnly queueDate,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "QueueCounters" ("DoctorId", "QueueDate", "LastNumber")
            VALUES (@doctorId, @queueDate, 1)
            ON CONFLICT ("DoctorId", "QueueDate")
            DO UPDATE SET "LastNumber" = "QueueCounters"."LastNumber" + 1
            RETURNING "LastNumber"
            """;

        var doctorParameter = command.CreateParameter();
        doctorParameter.ParameterName = "doctorId";
        doctorParameter.Value = doctorId;
        command.Parameters.Add(doctorParameter);

        var dateParameter = command.CreateParameter();
        dateParameter.ParameterName = "queueDate";
        dateParameter.Value = queueDate;
        command.Parameters.Add(dateParameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static int ReadRetryCount(object header) => header switch
    {
        int value => value,
        long value => checked((int)value),
        byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var value) => value,
        _ => 0
    };
}
