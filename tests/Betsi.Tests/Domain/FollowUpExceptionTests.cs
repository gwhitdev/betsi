namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

/// <summary>
/// The follow-up exception is the record that an escalation went unacknowledged (MVP-022).
/// It must only exist for a real missed deadline, and only a named owner or supervisor may
/// close it, with a recorded finding.
/// </summary>
public class FollowUpExceptionTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    private static Escalation AnEscalationDueAt15Minutes() =>
        Escalation.CreateFromWaitingTime(TenantId, Guid.NewGuid(), null, "Waiting-room Coordinator", Now);

    private static FollowUpException AnOpenException() =>
        FollowUpException.Raise(TenantId, AnEscalationDueAt15Minutes(), "Operations Manager", Now.AddMinutes(16));

    [Fact]
    public void It_records_the_escalation_the_missed_deadline_and_the_owner()
    {
        var escalation = AnEscalationDueAt15Minutes();

        var exception = FollowUpException.Raise(TenantId, escalation, "Operations Manager", Now.AddMinutes(16));

        exception.State.ShouldBe(FollowUpException.FollowUpState.Open);
        exception.EscalationId.ShouldBe(escalation.Id);
        exception.PatientEpisodeId.ShouldBe(escalation.PatientEpisodeId);
        exception.MissedDeadline.ShouldBe(Now.AddMinutes(15));
        exception.EscalationResponsibleRole.ShouldBe("Waiting-room Coordinator");
        exception.OwnerRole.ShouldBe("Operations Manager");
        exception.GetUncommittedEvents().Single().ShouldBeOfType<FollowUpExceptionRaised>().ActorRole.ShouldBe("System");
    }

    [Fact]
    public void It_cannot_be_raised_before_the_deadline_has_passed()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            FollowUpException.Raise(TenantId, AnEscalationDueAt15Minutes(), "Operations Manager", Now.AddMinutes(15)));
    }

    [Fact]
    public void It_cannot_be_raised_for_an_escalation_that_was_acknowledged()
    {
        var escalation = AnEscalationDueAt15Minutes();
        escalation.Acknowledge(Guid.NewGuid(), "Waiting-room Coordinator", null, Now.AddMinutes(20));

        Should.Throw<DomainRuleViolationException>(() =>
            FollowUpException.Raise(TenantId, escalation, "Operations Manager", Now.AddMinutes(30)));
    }

    [Fact]
    public void The_owner_can_close_it_with_a_recorded_finding()
    {
        var exception = AnOpenException();
        var reviewer = Guid.NewGuid();

        exception.Close(FollowUpException.ReviewOutcome.IncidentReported, "Datix W123456 raised; rota gap", reviewer,
            "Operations Manager", Now.AddHours(2));

        exception.State.ShouldBe(FollowUpException.FollowUpState.Closed);
        exception.Outcome.ShouldBe(FollowUpException.ReviewOutcome.IncidentReported);
        exception.ClosedByActorId.ShouldBe(reviewer);
        exception.ClosedAt.ShouldBe(Now.AddHours(2));
    }

    [Fact]
    public void A_supervisory_role_other_than_the_owner_can_close_it()
    {
        var exception = AnOpenException();

        exception.Close(FollowUpException.ReviewOutcome.AcknowledgedLate, "Picked up at 17 minutes, no harm",
            Guid.NewGuid(), "Matron", Now.AddHours(1));

        exception.State.ShouldBe(FollowUpException.FollowUpState.Closed);
    }

    [Fact]
    public void The_role_that_missed_the_acknowledgement_cannot_close_its_own_review()
    {
        var exception = AnOpenException();

        Should.Throw<DomainRuleViolationException>(() => exception.Close(
            FollowUpException.ReviewOutcome.NoFurtherActionRequired, "Was busy", Guid.NewGuid(), "Waiting-room Coordinator", Now.AddHours(1)));
    }

    [Fact]
    public void Closing_requires_an_identified_user_and_notes_and_happens_once()
    {
        var exception = AnOpenException();

        Should.Throw<DomainRuleViolationException>(() => exception.Close(
            FollowUpException.ReviewOutcome.Other, "Notes", Guid.Empty, "Matron", Now.AddHours(1)));
        Should.Throw<DomainRuleViolationException>(() => exception.Close(
            FollowUpException.ReviewOutcome.Other, " ", Guid.NewGuid(), "Matron", Now.AddHours(1)));

        exception.Close(FollowUpException.ReviewOutcome.Other, "Reviewed", Guid.NewGuid(), "Matron", Now.AddHours(1));

        Should.Throw<DomainRuleViolationException>(() => exception.Close(
            FollowUpException.ReviewOutcome.Other, "Again", Guid.NewGuid(), "Matron", Now.AddHours(2)));
    }
}
