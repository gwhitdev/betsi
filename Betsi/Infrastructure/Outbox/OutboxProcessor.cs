namespace Betsi.Infrastructure.Outbox;

using Betsi.Infrastructure.Observability;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Drains one tenant's outbox, publishing each queued event and marking it delivered.
/// </summary>
public interface IOutboxProcessor
{
    Task<OutboxDrainResult> DrainAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>What one drain pass did.</summary>
/// <param name="Published">Messages delivered and marked processed.</param>
/// <param name="Failed">Messages whose delivery threw and which remain queued.</param>
/// <param name="DeadLettered">Messages abandoned after exceeding the attempt limit.</param>
public readonly record struct OutboxDrainResult(int Published, int Failed, int DeadLettered)
{
    public int Total => Published + Failed + DeadLettered;
}

/// <inheritdoc cref="IOutboxProcessor"/>
public sealed class OutboxProcessor : IOutboxProcessor
{
    private readonly BetsiDbContext _context;
    private readonly IOutboxPublisher _publisher;
    private readonly OutboxOptions _options;
    private readonly BetsiMetrics _metrics;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        BetsiDbContext context,
        IOutboxPublisher publisher,
        OutboxOptions options,
        BetsiMetrics metrics,
        ILogger<OutboxProcessor> logger)
    {
        _context = context;
        _publisher = publisher;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<OutboxDrainResult> DrainAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Ordered by Id so events are delivered in the order they were written. Batched so a
        // large backlog does not hold a transaction or a connection open indefinitely.
        var pending = await _context.OutboxMessages
            .Where(m => m.TenantId == tenantId && m.ProcessedAt == null)
            .OrderBy(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return new OutboxDrainResult(0, 0, 0);

        var published = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var message in pending)
        {
            try
            {
                await _publisher.PublishAsync(message, cancellationToken);

                message.ProcessedAt = DateTime.UtcNow;
                message.ProcessedBy = Environment.MachineName;
                published++;

                _metrics.OutboxProcessed(tenantId, "published");
                _metrics.OutboxLag(tenantId, message.ProcessedAt.Value - message.CreatedAt);
            }
            catch (Exception exception)
            {
                message.Attempts++;
                message.LastError = Truncate(exception.Message, 500);

                if (message.Attempts >= _options.MaxAttempts)
                {
                    // A message that can never be delivered must not block the ones behind
                    // it. It is marked processed so the queue drains, and logged at error so
                    // the loss is visible rather than silent.
                    message.ProcessedAt = DateTime.UtcNow;
                    message.ProcessedBy = "dead-letter";
                    deadLettered++;
                    _metrics.OutboxProcessed(tenantId, "dead-lettered");

                    _logger.LogError(
                        exception,
                        "Outbox message {EventId} ({EventType}) for tenant {TenantId} dead-lettered " +
                        "after {Attempts} attempts. The event is recorded in the event log but was " +
                        "never delivered.",
                        message.EventId, message.EventType, tenantId, message.Attempts);
                }
                else
                {
                    failed++;
                    _metrics.OutboxProcessed(tenantId, "failed");

                    _logger.LogWarning(
                        exception,
                        "Outbox message {EventId} ({EventType}) for tenant {TenantId} failed on " +
                        "attempt {Attempts}; it will be retried.",
                        message.EventId, message.EventType, tenantId, message.Attempts);
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new OutboxDrainResult(published, failed, deadLettered);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// Delivers one outbox message to whatever is downstream.
/// </summary>
/// <remarks>
/// The MVP has no external broker (ADR-004), so the default implementation only records the
/// delivery. The seam exists so that adding a broker in v1.1, or the webhook delivery of
/// MVP-067, is a new implementation rather than a change to the drain loop.
/// </remarks>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The MVP publisher: logs the event and treats it as delivered.
/// </summary>
public sealed class LoggingOutboxPublisher : IOutboxPublisher
{
    private readonly ILogger<LoggingOutboxPublisher> _logger;

    public LoggingOutboxPublisher(ILogger<LoggingOutboxPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // The payload is not logged: domain events carry patient data.
        _logger.LogInformation(
            "Published {EventType} {EventId} for {AggregateType} {AggregateId}",
            message.EventType, message.EventId, message.AggregateType, message.AggregateId);

        return Task.CompletedTask;
    }
}

/// <summary>Outbox drain settings.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How many messages one pass will attempt.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How often the background service drains each tenant.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delivery attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;
}
