namespace Betsi.Domain.Aggregates;

/// <summary>
/// Escalation aggregate root.
/// Represents an escalation triggered by a waiting-time threshold or raised by staff, and the
/// record of who acknowledged, reassigned and resolved it.
/// </summary>
/// <remarks>
/// Every transition takes the time it happened at and the role of the person responsible, so
/// the event log can answer "who decided what, in which capacity, and when" without relying on
/// the server clock at the moment the event happened to be written.
/// </remarks>
public class Escalation : AggregateRoot
{
    public enum EscalationState
    {
        Created = 1,          // Awaiting acknowledgement
        Acknowledged = 2,     // Acknowledged by responsible person
        Resolved = 3,         // Issue resolved
        Escalated = 4,        // Escalated to higher authority
        ManualFollowUp = 5,   // Not acknowledged by deadline; a follow-up exception exists
        Closed = 6            // Escalation closed
    }

    public enum EscalationTrigger
    {
        WaitingTimeThreshold = 1, // Waiting time exceeded (policy or staff-raised)
        ManualEscalation = 2,     // Staff concern
        SystemAlert = 3           // System: safety alert
    }

    /// <summary>Deadline for escalations raised without a policy tier to take one from.</summary>
    public const int DefaultAcknowledgementDeadlineMinutes = 15;

    public Guid PatientEpisodeId { get; private set; }
    public Guid? LocationId { get; private set; }
    public Guid? QueueId { get; private set; }
    public EscalationState State { get; private set; }
    public EscalationTrigger Trigger { get; private set; }

    /// <summary>Role responsible for acting on this escalation, e.g. "Waiting-room Coordinator".</summary>
    public string ResponsibleRole { get; private set; } = string.Empty;

    // ---- Provenance for escalations raised by the waiting-time policy ----

    /// <summary>Policy revision whose tier raised this escalation. Null if raised by staff.</summary>
    public int? PolicyRevision { get; private set; }

    /// <summary>Tier of the policy (1 = first threshold). At most one escalation per patient per tier.</summary>
    public int? TierLevel { get; private set; }

    public int? ThresholdMinutes { get; private set; }

    /// <summary>How long the patient had waited when the escalation was raised.</summary>
    public int? WaitedMinutes { get; private set; }

    public string? RecommendedAction { get; private set; }

    public int AcknowledgementDeadlineMinutes { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>When the current responsible role must acknowledge by. Reset on reassignment.</summary>
    public DateTime? AcknowledgementDueAt { get; private set; }

    public Guid? AcknowledgedByActorId { get; private set; }
    public string? AcknowledgedByRole { get; private set; }
    public DateTime? AcknowledgedAt { get; private set; }

    public Guid? ResolvedByActorId { get; private set; }
    public string? ResolvedByRole { get; private set; }
    public DateTime? ResolvedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    /// <summary>Accumulated notes. Every note is also on the event that added it.</summary>
    public string? Notes { get; private set; }

    public bool IsOpen => State is not (EscalationState.Resolved or EscalationState.Closed);

    // ============= Constructors =============

    protected Escalation() { }

    private Escalation(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid? locationId,
        Guid? queueId,
        string responsibleRole,
        EscalationTrigger trigger,
        int acknowledgementDeadlineMinutes,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(responsibleRole))
            throw new DomainRuleViolationException("An escalation must name the role responsible for it.");

        if (acknowledgementDeadlineMinutes <= 0)
            throw new DomainRuleViolationException("An acknowledgement deadline must be in the future.");

        Id = Guid.NewGuid();
        TenantId = tenantId;
        PatientEpisodeId = patientEpisodeId;
        LocationId = locationId;
        QueueId = queueId;
        ResponsibleRole = responsibleRole;
        Trigger = trigger;
        State = EscalationState.Created;
        CreatedAt = now;
        AcknowledgementDeadlineMinutes = acknowledgementDeadlineMinutes;
        AcknowledgementDueAt = now.AddMinutes(acknowledgementDeadlineMinutes);
        Version = 0;
    }

    // ============= Factory Methods =============

    /// <summary>A waiting-time escalation raised by a member of staff.</summary>
    public static Escalation CreateFromWaitingTime(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid? locationId,
        string responsibleRole,
        DateTime now,
        Guid? queueId = null,
        Guid? actorId = null,
        string actorRole = "System")
    {
        var escalation = new Escalation(
            tenantId, patientEpisodeId, locationId, queueId, responsibleRole,
            EscalationTrigger.WaitingTimeThreshold, DefaultAcknowledgementDeadlineMinutes, now);

        escalation.RaiseCreated(actorId ?? Guid.Empty, actorRole, now);
        return escalation;
    }

    /// <summary>A waiting-time escalation raised automatically by an approved policy tier (MVP-021).</summary>
    public static Escalation CreateFromWaitingTimePolicy(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid? locationId,
        int policyRevision,
        WaitingTimeTier tier,
        int waitedMinutes,
        DateTime now)
    {
        if (waitedMinutes < tier.ThresholdMinutes)
        {
            throw new DomainRuleViolationException(
                $"Tier {tier.Level} applies after {tier.ThresholdMinutes} minutes; the patient has waited {waitedMinutes}.");
        }

        var escalation = new Escalation(
            tenantId, patientEpisodeId, locationId, queueId: null, tier.ResponsibleRole,
            EscalationTrigger.WaitingTimeThreshold, tier.AcknowledgementDeadlineMinutes, now)
        {
            PolicyRevision = policyRevision,
            TierLevel = tier.Level,
            ThresholdMinutes = tier.ThresholdMinutes,
            WaitedMinutes = waitedMinutes,
            RecommendedAction = tier.RecommendedAction
        };

        escalation.RaiseCreated(Guid.Empty, "System", now);
        return escalation;
    }

    public static Escalation CreateManual(
        Guid tenantId,
        Guid patientEpisodeId,
        Guid? locationId,
        string responsibleRole,
        string notes,
        Guid actorId,
        string actorRole,
        DateTime now)
    {
        var escalation = new Escalation(
            tenantId, patientEpisodeId, locationId, queueId: null, responsibleRole,
            EscalationTrigger.ManualEscalation, DefaultAcknowledgementDeadlineMinutes, now)
        {
            Notes = notes
        };

        escalation.RaiseCreated(actorId, actorRole, now);
        return escalation;
    }

    private void RaiseCreated(Guid actorId, string actorRole, DateTime now) =>
        RaiseDomainEvent(new EscalationCreated
        {
            PatientEpisodeId = PatientEpisodeId,
            LocationId = LocationId,
            ResponsibleRole = ResponsibleRole,
            Trigger = Trigger.ToString(),
            PolicyRevision = PolicyRevision,
            TierLevel = TierLevel,
            ThresholdMinutes = ThresholdMinutes,
            WaitedMinutes = WaitedMinutes,
            RecommendedAction = RecommendedAction,
            AcknowledgementDueAt = AcknowledgementDueAt!.Value,
            Notes = Notes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });

    // ============= Command Handlers =============

    /// <summary>
    /// The responsible person confirms they have taken the escalation on.
    /// </summary>
    /// <returns>False if it was already acknowledged: acknowledging twice is not an error.</returns>
    public bool Acknowledge(Guid actorId, string actorRole, string? notes, DateTime now)
    {
        // Idempotent so a double-tap on a ward tablet, or a retry after a dropped response,
        // does not show the second person an error for doing the right thing.
        if (State == EscalationState.Acknowledged)
            return false;

        if (State != EscalationState.Created && State != EscalationState.ManualFollowUp)
            throw new DomainRuleViolationException($"Cannot acknowledge escalation in state {State}");

        State = EscalationState.Acknowledged;
        AcknowledgedByActorId = actorId;
        AcknowledgedByRole = actorRole;
        AcknowledgedAt = now;
        AppendNote(null, notes);

        RaiseDomainEvent(new EscalationAcknowledged
        {
            AcknowledgedByActorId = actorId,
            Notes = notes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });

        return true;
    }

    /// <summary>The underlying issue has been dealt with.</summary>
    /// <returns>False if it was already resolved.</returns>
    public bool Resolve(Guid actorId, string actorRole, string? notes, DateTime now)
    {
        if (State == EscalationState.Resolved)
            return false;

        if (State != EscalationState.Acknowledged && State != EscalationState.ManualFollowUp &&
            State != EscalationState.Escalated)
        {
            throw new DomainRuleViolationException($"Cannot resolve escalation in state {State}");
        }

        State = EscalationState.Resolved;
        ResolvedAt = now;
        ResolvedByActorId = actorId;
        ResolvedByRole = actorRole;
        AppendNote("RESOLVED", notes);

        RaiseDomainEvent(new EscalationResolved
        {
            ResolvedAt = now,
            Notes = notes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });

        return true;
    }

    /// <summary>
    /// Hands the escalation to a different role (MVP-025). The new role must acknowledge it
    /// afresh, against a new deadline.
    /// </summary>
    public void Reassign(string newResponsibleRole, string reason, Guid actorId, string actorRole, DateTime now)
    {
        if (!IsOpen)
            throw new DomainRuleViolationException($"Cannot reassign escalation in state {State}");

        if (string.IsNullOrWhiteSpace(newResponsibleRole))
            throw new DomainRuleViolationException("A reassignment must name the new responsible role.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleViolationException("A reassignment must record why.");

        if (string.Equals(newResponsibleRole, ResponsibleRole, StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleViolationException($"The escalation is already assigned to {ResponsibleRole}.");

        var previousRole = ResponsibleRole;

        // Reset to awaiting acknowledgement: an acknowledgement by the previous owner says
        // nothing about whether the new owner has seen it.
        ResponsibleRole = newResponsibleRole;
        State = EscalationState.Created;
        AcknowledgedByActorId = null;
        AcknowledgedByRole = null;
        AcknowledgedAt = null;
        AcknowledgementDueAt = now.AddMinutes(AcknowledgementDeadlineMinutes);
        AppendNote($"REASSIGNED from {previousRole} to {newResponsibleRole}", reason);

        RaiseDomainEvent(new EscalationReassigned
        {
            PreviousResponsibleRole = previousRole,
            NewResponsibleRole = newResponsibleRole,
            Reason = reason,
            AcknowledgementDueAt = AcknowledgementDueAt.Value,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    /// <summary>Escalates to a higher authority after acknowledgement.</summary>
    public void EscalateToHigherAuthority(string higherRole, Guid actorId, string actorRole, string? notes, DateTime now)
    {
        if (State != EscalationState.Acknowledged && State != EscalationState.ManualFollowUp)
            throw new DomainRuleViolationException($"Cannot escalate further in state {State}");

        State = EscalationState.Escalated;
        ResponsibleRole = higherRole;
        AppendNote($"ESCALATED to {higherRole}", notes);

        RaiseDomainEvent(new EscalationEscalatedFurther
        {
            NewResponsibleRole = higherRole,
            Notes = notes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    /// <summary>
    /// Records that the acknowledgement deadline was missed and a follow-up exception raised (MVP-022).
    /// </summary>
    public void MarkForManualFollowUp(Guid followUpExceptionId, Guid actorId, string actorRole, DateTime now)
    {
        if (State != EscalationState.Created && State != EscalationState.Escalated)
            throw new DomainRuleViolationException($"Cannot mark for manual follow-up in state {State}");

        State = EscalationState.ManualFollowUp;
        AppendNote("MANUAL FOLLOW-UP", $"Not acknowledged by {AcknowledgementDueAt:yyyy-MM-dd HH:mm} UTC");

        RaiseDomainEvent(new EscalationMarkedForManualFollowUp
        {
            FollowUpExceptionId = followUpExceptionId,
            MissedDeadline = AcknowledgementDueAt,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    public void Close(Guid actorId, string actorRole, string? notes, DateTime now)
    {
        if (State == EscalationState.Closed)
            throw new DomainRuleViolationException("Escalation already closed");

        State = EscalationState.Closed;
        ClosedAt = now;
        AppendNote("CLOSED", notes);

        RaiseDomainEvent(new EscalationClosed
        {
            Notes = notes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    /// <summary>Whether the escalation is awaiting acknowledgement and its deadline has passed.</summary>
    public bool HasExceededAcknowledgementDeadline(DateTime now) =>
        State == EscalationState.Created && AcknowledgementDueAt is { } due && now > due;

    private void AppendNote(string? marker, string? note)
    {
        if (string.IsNullOrWhiteSpace(note) && marker is null)
            return;

        var entry = marker is null ? note : $"[{marker}] {note}".TrimEnd();
        Notes = string.IsNullOrEmpty(Notes) ? entry : $"{Notes} {entry}";

        if (Notes is { Length: > 2000 })
            Notes = "…" + Notes[^1999..];
    }
}

// ============= Domain Events =============

public class EscalationCreated : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public Guid? LocationId { get; set; }
    public string ResponsibleRole { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public int? PolicyRevision { get; set; }
    public int? TierLevel { get; set; }
    public int? ThresholdMinutes { get; set; }
    public int? WaitedMinutes { get; set; }
    public string? RecommendedAction { get; set; }
    public DateTime AcknowledgementDueAt { get; set; }
    public string? Notes { get; set; }
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

public class EscalationReassigned : DomainEvent
{
    public string PreviousResponsibleRole { get; set; } = string.Empty;
    public string NewResponsibleRole { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime AcknowledgementDueAt { get; set; }
}

public class EscalationEscalatedFurther : DomainEvent
{
    public string NewResponsibleRole { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class EscalationMarkedForManualFollowUp : DomainEvent
{
    public Guid FollowUpExceptionId { get; set; }
    public DateTime? MissedDeadline { get; set; }
}

public class EscalationClosed : DomainEvent
{
    public string? Notes { get; set; }
}
