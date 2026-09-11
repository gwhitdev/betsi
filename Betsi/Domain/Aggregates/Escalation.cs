namespace Betsi.Domain.Aggregates;

/// <summary>
/// Escalation aggregate root.
/// Represents an escalation event triggered by waiting-time thresholds or manual intervention.
/// Implements the manual-follow-up exception workflow for missed acknowledgements.
/// </summary>
public class Escalation : AggregateRoot
{
    public enum EscalationState
    {
        Created = 1,          // Initial state
        Acknowledged = 2,     // Acknowledged by responsible person
        Resolved = 3,         // Issue resolved
        Escalated = 4,        // Escalated to higher authority
        ManualFollowUp = 5,   // Awaiting manual follow-up (not acknowledged by deadline)
        Closed = 6            // Escalation closed
    }

    public enum EscalationTrigger
    {
        WaitingTimeThreshold = 1, // Automatic: waiting time exceeded
        ManualEscalation = 2,      // Manual: user triggered
        SystemAlert = 3            // System: safety alert
    }

    // Properties
    public Guid PatientEpisodeId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid? QueueId { get; private set; }
    public EscalationState State { get; private set; }
    public EscalationTrigger Trigger { get; private set; }

    /// <summary>
    /// Role responsible for acknowledging this escalation (e.g., "Senior Clinician", "Resuscitation Lead").
    /// </summary>
    public string ResponsibleRole { get; private set; } = string.Empty;

    /// <summary>
    /// Person who acknowledged this escalation (if applicable).
    /// </summary>
    public Guid? AcknowledgedByActorId { get; private set; }

    /// <summary>
    /// Timestamp when this escalation was created.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Timestamp by which this escalation must be acknowledged.
    /// Used to trigger manual-follow-up workflow.
    /// </summary>
    public DateTime? AcknowledgementDueAt { get; private set; }

    /// <summary>
    /// Timestamp when this escalation was acknowledged (if applicable).
    /// </summary>
    public DateTime? AcknowledgedAt { get; private set; }

    /// <summary>
    /// Timestamp when this escalation was resolved (if applicable).
    /// </summary>
    public DateTime? ResolvedAt { get; private set; }

    /// <summary>
    /// Free-text notes about the escalation (e.g., action taken).
    /// </summary>
    public string? Notes { get; private set; }

    // ============= Constructors =============

    protected Escalation() { }

    private Escalation(
        Guid id,
        Guid tenantId,
        Guid patientEpisodeId,
        Guid locationId,
        string responsibleRole,
        EscalationTrigger trigger,
        Guid? queueId = null)
    {
        Id = id;
        TenantId = tenantId;
        PatientEpisodeId = patientEpisodeId;
        LocationId = locationId;
        QueueId = queueId;
        ResponsibleRole = responsibleRole;
        Trigger = trigger;
        State = EscalationState.Created;
        CreatedAt = DateTime.UtcNow;

        // Set acknowledgement deadline to 15 minutes from now
        AcknowledgementDueAt = DateTime.UtcNow.AddMinutes(15);
        Version = 0;
    }

    // ============= Factory Methods =============

    public static Escalation CreateFromWaitingTime(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid locationId,
        string responsibleRole,
        Guid? queueId = null,
        Guid? actorId = null,
        string actorRole = "System")
    {
        var escalation = new Escalation(
            Guid.NewGuid(),
            tenantId,
            patientEpisodeId,
            locationId,
            responsibleRole,
            EscalationTrigger.WaitingTimeThreshold,
            queueId);

        escalation.RaiseDomainEvent(new EscalationCreated
        {
            PatientEpisodeId = patientEpisodeId,
            LocationId = locationId,
            ResponsibleRole = responsibleRole,
            Trigger = EscalationTrigger.WaitingTimeThreshold.ToString(),
            ActorId = actorId ?? Guid.Empty,
            ActorRole = actorRole
        });

        return escalation;
    }

    public static Escalation CreateManual(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid locationId,
        string responsibleRole,
        string notes,
        Guid actorId,
        string actorRole)
    {
        var escalation = new Escalation(
            Guid.NewGuid(),
            tenantId,
            patientEpisodeId,
            locationId,
            responsibleRole,
            EscalationTrigger.ManualEscalation);

        escalation.Notes = notes;

        escalation.RaiseDomainEvent(new EscalationCreated
        {
            PatientEpisodeId = patientEpisodeId,
            LocationId = locationId,
            ResponsibleRole = responsibleRole,
            Trigger = EscalationTrigger.ManualEscalation.ToString(),
            ActorId = actorId,
            ActorRole = actorRole
        });

        return escalation;
    }

    // ============= Command Handlers =============

    /// <summary>
    /// Acknowledges the escalation (person with responsible role confirms receipt).
    /// </summary>
    public void Acknowledge(Guid actorId, string? notes = null)
    {
        if (State != EscalationState.Created && State != EscalationState.ManualFollowUp)
            throw new InvalidOperationException($"Cannot acknowledge escalation in state {State}");

        State = EscalationState.Acknowledged;
        AcknowledgedByActorId = actorId;
        AcknowledgedAt = DateTime.UtcNow;
        if (notes != null)
            Notes = notes;

        RaiseDomainEvent(new EscalationAcknowledged
        {
            AcknowledgedByActorId = actorId,
            Notes = notes,
            ActorId = actorId,
            ActorRole = "Staff" // Actual role injected at command handler level
        });
    }

    /// <summary>
    /// Resolves the escalation (underlying issue addressed).
    /// </summary>
    public void Resolve(Guid actorId, string? notes = null)
    {
        if (State != EscalationState.Acknowledged && State != EscalationState.ManualFollowUp)
            throw new InvalidOperationException($"Cannot resolve escalation in state {State}");

        State = EscalationState.Resolved;
        ResolvedAt = DateTime.UtcNow;
        if (notes != null)
            Notes = (Notes ?? string.Empty) + $" [RESOLVED] {notes}";

        RaiseDomainEvent(new EscalationResolved
        {
            ResolvedAt = ResolvedAt.Value,
            Notes = notes,
            ActorId = actorId,
            ActorRole = "Staff"
        });
    }

    /// <summary>
    /// Escalates to a higher authority (manual escalation workflow).
    /// </summary>
    public void EscalateToHigherAuthority(string higherRole, Guid actorId, string? notes = null)
    {
        if (State != EscalationState.Acknowledged && State != EscalationState.ManualFollowUp)
            throw new InvalidOperationException($"Cannot escalate further in state {State}");

        State = EscalationState.Escalated;
        ResponsibleRole = higherRole;
        if (notes != null)
            Notes = (Notes ?? string.Empty) + $" [ESCALATED to {higherRole}] {notes}";

        RaiseDomainEvent(new EscalationEscalatedFurther
        {
            NewResponsibleRole = higherRole,
            Notes = notes,
            ActorId = actorId,
            ActorRole = "Staff"
        });
    }

    /// <summary>
    /// Marks escalation as requiring manual follow-up (not acknowledged by deadline).
    /// Triggers manual-follow-up workflow.
    /// </summary>
    public void MarkForManualFollowUp(Guid actorId, string? notes = null)
    {
        if (State != EscalationState.Created && State != EscalationState.Escalated)
            throw new InvalidOperationException($"Cannot mark for manual follow-up in state {State}");

        State = EscalationState.ManualFollowUp;
        if (notes != null)
            Notes = (Notes ?? string.Empty) + $" [MANUAL FOLLOW-UP] {notes}";

        RaiseDomainEvent(new EscalationMarkedForManualFollowUp
        {
            Notes = notes,
            ActorId = actorId,
            ActorRole = "System"
        });
    }

    /// <summary>
    /// Closes the escalation.
    /// </summary>
    public void Close(Guid actorId, string? notes = null)
    {
        if (State == EscalationState.Closed)
            throw new InvalidOperationException("Escalation already closed");

        State = EscalationState.Closed;
        if (notes != null)
            Notes = (Notes ?? string.Empty) + $" [CLOSED] {notes}";

        RaiseDomainEvent(new EscalationClosed
        {
            Notes = notes,
            ActorId = actorId,
            ActorRole = "Staff"
        });
    }

    /// <summary>
    /// Checks if this escalation has exceeded its acknowledgement deadline.
    /// </summary>
    public bool HasExceededAcknowledgementDeadline()
    {
        return AcknowledgementDueAt.HasValue && DateTime.UtcNow > AcknowledgementDueAt.Value;
    }
}

// ============= Domain Events =============

public class EscalationCreated : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public Guid LocationId { get; set; }
    public string ResponsibleRole { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
}

public class EscalationAcknowledged : DomainEvent
{
    public Guid AcknowledgedByActorId { get; set; }
    public string? Notes { get; set; }
}

public class EscalationResolved : DomainEvent
{
    public DateTime ResolvedAt { get; set; }
    public string? Notes { get; set; }
}

public class EscalationEscalatedFurther : DomainEvent
{
    public string NewResponsibleRole { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class EscalationMarkedForManualFollowUp : DomainEvent
{
    public string? Notes { get; set; }
}

public class EscalationClosed : DomainEvent
{
    public string? Notes { get; set; }
}
