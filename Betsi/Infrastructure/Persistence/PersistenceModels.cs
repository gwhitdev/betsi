namespace Betsi.Infrastructure.Persistence;

/// <summary>
/// Represents a persisted domain event from the event store.
/// Each row is immutable - events are append-only.
/// </summary>
public class DomainEventRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid AggregateId { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Serialized event data as JSON.
    /// </summary>
    public string EventData { get; set; } = string.Empty;

    /// <summary>
    /// Version of the aggregate when this event was generated.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// User or system that triggered this event.
    /// </summary>
    public Guid ActorId { get; set; }
    public string ActorRole { get; set; } = string.Empty;

    /// <summary>
    /// When the event occurred (domain time).
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// When the event was persisted (system time).
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Represents an outbox message for reliable integration delivery.
/// Messages are processed asynchronously and marked as processed when delivered.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid AggregateId { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Serialized event data as JSON for external systems.
    /// </summary>
    public string EventData { get; set; } = string.Empty;

    /// <summary>
    /// When this message was created (added to outbox).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this message was successfully processed/delivered.
    /// NULL if not yet processed.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Name of the consumer that processed this message (e.g., "ProjectionWorker", "NotificationService").
    /// </summary>
    public string? ProcessedBy { get; set; }

    /// <summary>
    /// How many delivery attempts have been made. Used to dead-letter a message that can
    /// never be delivered rather than retrying it forever and blocking the queue behind it.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>Why the most recent delivery attempt failed, if it did.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Audit log record for compliance and forensic analysis.
/// Records all command executions, their outcomes, and who performed them.
/// </summary>
public class AuditLogRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>
    /// Operation performed (e.g., "RegisterPatient", "AcknowledgeEscalation").
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// User or system that performed this action.
    /// </summary>
    public Guid ActorId { get; set; }
    public string ActorRole { get; set; } = string.Empty;

    /// <summary>
    /// Aggregate that was affected by this action.
    /// </summary>
    public Guid AffectedAggregateId { get; set; }
    public string AffectedAggregateType { get; set; } = string.Empty;

    /// <summary>
    /// Outcome of the action: "Success" or "Failure".
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// If outcome is Failure, what was the error?
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional context as JSON (request parameters, etc).
    /// PHI-sensitive fields should be redacted.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// When this action occurred.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
