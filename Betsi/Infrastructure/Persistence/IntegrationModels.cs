namespace Betsi.Infrastructure.Persistence;

/// <summary>
/// The outcome of a command submitted with an idempotency key (MVP-061), so a retried request
/// returns the original result rather than acting twice.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>A key is bound to the actor that first used it; nobody else can replay its result.</summary>
    public Guid ActorId { get; set; }

    public string CommandType { get; set; } = string.Empty;

    /// <summary>SHA-256 of the command type and payload. A reused key with a different request is refused.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary><c>Pending</c> while the command runs, then <c>Completed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public Guid CommandId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>An external endpoint that receives signed event notifications (MVP-064).</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Url { get; set; } = string.Empty;
    public List<string> EventTypes { get; set; } = [];
    public string? Description { get; set; }

    /// <summary>The signing secret, encrypted with ASP.NET Data Protection. Never returned after creation.</summary>
    public string ProtectedSecret { get; set; } = string.Empty;

    public bool Active { get; set; }
    public Guid CreatedByActorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SecretRotatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}

/// <summary>One event to be delivered to one subscription, with its retry state.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;

    /// <summary>The exact body sent and signed, fixed when the delivery is created so every retry is identical.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary><c>Pending</c>, <c>Delivered</c> or <c>DeadLettered</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An external system allowed to send signed HL7 v2 or FHIR messages (MVP-063, MVP-066).</summary>
public class InboundSource
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary><c>Hl7v2</c> or <c>FhirR4</c>.</summary>
    public string Format { get; set; } = string.Empty;

    public string ProtectedSecret { get; set; } = string.Empty;
    public bool Active { get; set; }
    public Guid CreatedByActorId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Every inbound message received: for de-duplication, and so a message that could not be
/// processed is quarantined for review rather than silently discarded (spec §6).
/// </summary>
public class InboundMessage
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SourceId { get; set; }

    /// <summary>The sender's message id (<c>Betsi-Message-Id</c>). Unique per source.</summary>
    public string MessageId { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    /// <summary><c>Accepted</c> or <c>Quarantined</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public string? MessageType { get; set; }
    public string? Error { get; set; }
    public Guid? EpisodeId { get; set; }

    /// <summary>Kept only for quarantined messages, which a person must review. Accepted messages are not retained.</summary>
    public string? Body { get; set; }

    public DateTime ReceivedAt { get; set; }
}

/// <summary>Maps a source system's visit identifier to the episode it created.</summary>
public class ExternalEpisodeLink
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SourceId { get; set; }
    public string ExternalVisitId { get; set; } = string.Empty;
    public Guid EpisodeId { get; set; }
    public DateTime CreatedAt { get; set; }
}
