namespace Betsi.Domain.Aggregates;

/// <summary>
/// An audited record that an escalation was not acknowledged by its deadline, awaiting
/// supervisory review (MVP-022, spec §4.3).
/// </summary>
/// <remarks>
/// Separate from the escalation because it has its own owner, its own lifecycle and its own
/// closure authority: the escalation can be acknowledged late and resolved while the question
/// "why did nobody pick this up for 40 minutes?" is still open.
///
/// One exception per missed deadline. An escalation that is reassigned and then misses its new
/// deadline raises a second exception — that is a second failure, not a duplicate.
/// </remarks>
public class FollowUpException : AggregateRoot
{
    public enum FollowUpState
    {
        Open = 1,
        Closed = 2
    }

    public enum ReviewOutcome
    {
        /// <summary>The escalation was picked up late; no harm identified.</summary>
        AcknowledgedLate = 1,
        /// <summary>The escalation was reassigned to someone able to act.</summary>
        Reassigned = 2,
        /// <summary>Reviewed; the escalation no longer required action.</summary>
        NoFurtherActionRequired = 3,
        /// <summary>Reviewed and reported through the incident process (e.g. Datix).</summary>
        IncidentReported = 4,
        Other = 5
    }

    public Guid EscalationId { get; private set; }
    public Guid PatientEpisodeId { get; private set; }

    /// <summary>The role that should have acknowledged the escalation.</summary>
    public string EscalationResponsibleRole { get; private set; } = string.Empty;

    /// <summary>The deadline that was missed. With <see cref="EscalationId"/>, identifies the exception.</summary>
    public DateTime MissedDeadline { get; private set; }

    public DateTime RaisedAt { get; private set; }

    /// <summary>Who is expected to review it.</summary>
    public string OwnerRole { get; private set; } = string.Empty;

    public FollowUpState State { get; private set; }
    public ReviewOutcome? Outcome { get; private set; }
    public string? ReviewNotes { get; private set; }
    public Guid? ClosedByActorId { get; private set; }
    public string? ClosedByRole { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    protected FollowUpException() { }

    public static FollowUpException Raise(Guid tenantId, Escalation escalation, string ownerRole, DateTime now)
    {
        if (!escalation.HasExceededAcknowledgementDeadline(now))
        {
            throw new DomainRuleViolationException(
                $"Escalation {escalation.Id} is {escalation.State} with deadline {escalation.AcknowledgementDueAt:O}; " +
                "a follow-up exception is only raised for a missed acknowledgement deadline.");
        }

        if (string.IsNullOrWhiteSpace(ownerRole))
            throw new DomainRuleViolationException("A follow-up exception must have an owner.");

        var exception = new FollowUpException
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EscalationId = escalation.Id,
            PatientEpisodeId = escalation.PatientEpisodeId,
            EscalationResponsibleRole = escalation.ResponsibleRole,
            MissedDeadline = escalation.AcknowledgementDueAt!.Value,
            RaisedAt = now,
            OwnerRole = ownerRole,
            State = FollowUpState.Open
        };

        exception.RaiseDomainEvent(new FollowUpExceptionRaised
        {
            EscalationId = escalation.Id,
            PatientEpisodeId = escalation.PatientEpisodeId,
            EscalationResponsibleRole = escalation.ResponsibleRole,
            MissedDeadline = exception.MissedDeadline,
            OwnerRole = ownerRole,
            ActorId = Guid.Empty,
            ActorRole = "System",
            OccurredAt = now
        });

        return exception;
    }

    /// <summary>Records the supervisory review and closes the exception.</summary>
    public void Close(ReviewOutcome outcome, string reviewNotes, Guid actorId, string actorRole, DateTime now)
    {
        if (State == FollowUpState.Closed)
            throw new DomainRuleViolationException("This follow-up exception has already been closed.");

        if (actorId == Guid.Empty)
            throw new DomainRuleViolationException("An identified user is required to close a follow-up exception.");

        if (string.IsNullOrWhiteSpace(reviewNotes))
            throw new DomainRuleViolationException("Closing a follow-up exception must record what the review found.");

        // Closure authority (spec §4.3): the named owner, or a supervisory role. Not the role
        // whose missed acknowledgement is being reviewed, unless it is also one of those.
        if (!string.Equals(actorRole, OwnerRole, StringComparison.OrdinalIgnoreCase) &&
            !EscalationAuthority.IsSupervisory(actorRole))
        {
            throw new DomainRuleViolationException(
                $"Only {OwnerRole} or a supervisory role may close this follow-up exception.");
        }

        State = FollowUpState.Closed;
        Outcome = outcome;
        ReviewNotes = reviewNotes.Trim();
        ClosedByActorId = actorId;
        ClosedByRole = actorRole;
        ClosedAt = now;

        RaiseDomainEvent(new FollowUpExceptionClosed
        {
            EscalationId = EscalationId,
            Outcome = outcome.ToString(),
            ReviewNotes = ReviewNotes,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }
}

public class FollowUpExceptionRaised : DomainEvent
{
    public Guid EscalationId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public string EscalationResponsibleRole { get; set; } = string.Empty;
    public DateTime MissedDeadline { get; set; }
    public string OwnerRole { get; set; } = string.Empty;
}

public class FollowUpExceptionClosed : DomainEvent
{
    public Guid EscalationId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string ReviewNotes { get; set; } = string.Empty;
}
