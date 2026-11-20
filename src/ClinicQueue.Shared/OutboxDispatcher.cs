using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinicQueue.Shared;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    RabbitMqPublisher publisher,
    ILogger<OutboxDispatcher> logger,
    string eventType) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox dispatch failed for {EventType}; it will be retried", eventType);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var messages = await db.OutboxMessages
            .Where(message => message.EventType == eventType && message.PublishedAt == null)
            .OrderBy(message => message.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            switch (message.EventType)
            {
                case EventTypes.AppointmentRequested:
                    publisher.PublishAppointmentRequested(
                        JsonSerializer.Deserialize<AppointmentRequestedEvent>(message.Payload, JsonOptions)
                        ?? throw new InvalidOperationException($"Invalid outbox payload {message.Id}"));
                    break;
                case EventTypes.QueueChanged:
                    publisher.PublishQueueChanged(
                        JsonSerializer.Deserialize<QueueChangedEvent>(message.Payload, JsonOptions)
                        ?? throw new InvalidOperationException($"Invalid outbox payload {message.Id}"));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported outbox event type: {message.EventType}");
            }

            message.PublishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Published outbox message {MessageId} ({EventType})", message.Id, message.EventType);
        }
    }
}

public static class EventTypes
{
    public const string AppointmentRequested = "appointment.requested";
    public const string QueueChanged = "queue.changed";
}
