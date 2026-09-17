namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

/// <summary>
/// The escalation lifecycle is the product's answer to the ED report's prolonged-wait and
/// delayed-response findings, so its transitions — especially the manual-follow-up path for
/// escalations nobody acknowledged — are covered in full.
/// </summary>
public class EscalationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly WaitingTimeTier CoordinatorTier =
        new(1, 240, "Waiting-room Coordinator", 30, "Review clinical status and consider reassessment");

    private static Escalation AWaitingTimeEscalation() =>
        Escalation.CreateFromWaitingTime(
            TenantId, Guid.NewGuid(), Guid.NewGuid(), "Senior Clinician", Now,
            queueId: Guid.NewGuid(), actorId: ActorId, actorRole: "Staff Nurse");

    private static Escalation AnAcknowledgedEscalation()
    {
        var escalation = AWaitingTimeEscalation();
        escalation.Acknowledge(ActorId, "Senior Clinician", "Reviewing now", Now.AddMinutes(5));
        return escalation;
    }

    public class WhenRaised
    {
        [Fact]
        public void It_starts_in_the_created_state_awaiting_acknowledgement()
        {
            var escalation = AWaitingTimeEscalation();

            escalation.State.ShouldBe(Escalation.EscalationState.Created);
            escalation.Trigger.ShouldBe(Escalation.EscalationTrigger.WaitingTimeThreshold);
            escalation.ResponsibleRole.ShouldBe("Senior Clinician");
            escalation.CreatedAt.ShouldBe(Now);
        }

        [Fact]
        public void A_staff_raised_escalation_has_the_default_fifteen_minute_deadline()
        {
            var escalation = AWaitingTimeEscalation();

            escalation.AcknowledgementDueAt.ShouldBe(Now.AddMinutes(15));
            escalation.AcknowledgementDeadlineMinutes.ShouldBe(Escalation.DefaultAcknowledgementDeadlineMinutes);
        }

        [Fact]
        public void A_policy_escalation_records_the_tier_policy_and_wait_that_raised_it()
        {
            var episode = Guid.NewGuid();

            var escalation = Escalation.CreateFromWaitingTimePolicy(
                TenantId, episode, locationId: null, policyRevision: 3, CoordinatorTier, waitedMinutes: 247, Now);

            escalation.TierLevel.ShouldBe(1);
            escalation.PolicyRevision.ShouldBe(3);
            escalation.ThresholdMinutes.ShouldBe(240);
            escalation.WaitedMinutes.ShouldBe(247);
            escalation.ResponsibleRole.ShouldBe("Waiting-room Coordinator");
            escalation.RecommendedAction.ShouldBe(CoordinatorTier.RecommendedAction);
            escalation.AcknowledgementDueAt.ShouldBe(Now.AddMinutes(30));

            var created = escalation.GetUncommittedEvents().Single().ShouldBeOfType<EscalationCreated>();
            created.ActorRole.ShouldBe("System");
            created.TierLevel.ShouldBe(1);
            created.OccurredAt.ShouldBe(Now);
        }

        [Fact]
        public void A_policy_escalation_cannot_be_raised_before_its_threshold()
        {
            Should.Throw<DomainRuleViolationException>(() => Escalation.CreateFromWaitingTimePolicy(
                TenantId, Guid.NewGuid(), null, 1, CoordinatorTier, waitedMinutes: 239, Now));
        }

        [Fact]
        public void An_escalation_must_name_who_is_responsible()
        {
            Should.Throw<DomainRuleViolationException>(() =>
                Escalation.CreateFromWaitingTime(TenantId, Guid.NewGuid(), null, " ", Now));
        }

        [Fact]
        public void A_manual_escalation_records_who_raised_it_and_why()
        {
            var escalation = Escalation.CreateManual(
                TenantId, Guid.NewGuid(), Guid.NewGuid(), "Nurse in Charge",
                "Patient deteriorating in corridor", ActorId, "Staff Nurse", Now);

            escalation.Trigger.ShouldBe(Escalation.EscalationTrigger.ManualEscalation);
            escalation.Notes.ShouldBe("Patient deteriorating in corridor");
            var created = escalation.GetUncommittedEvents().Single().ShouldBeOfType<EscalationCreated>();
            created.ActorId.ShouldBe(ActorId);
            created.ActorRole.ShouldBe("Staff Nurse");
        }
    }

    public class Acknowledgement
    {
        [Fact]
        public void Acknowledging_records_who_took_it_on_in_which_role_and_when()
        {
            var escalation = AWaitingTimeEscalation();
            var responder = Guid.NewGuid();

            escalation.Acknowledge(responder, "Senior Clinician", "On my way", Now.AddMinutes(4)).ShouldBeTrue();

            escalation.State.ShouldBe(Escalation.EscalationState.Acknowledged);
            escalation.AcknowledgedByActorId.ShouldBe(responder);
            escalation.AcknowledgedByRole.ShouldBe("Senior Clinician");
            escalation.AcknowledgedAt.ShouldBe(Now.AddMinutes(4));
            escalation.Notes.ShouldBe("On my way");
        }

        [Fact]
        public void Acknowledging_twice_is_harmless_and_records_nothing_new()
        {
            var escalation = AnAcknowledgedEscalation();
            _ = escalation.GetUncommittedEvents();

            escalation.Acknowledge(Guid.NewGuid(), "Staff Nurse", "Me too", Now.AddMinutes(6)).ShouldBeFalse();

            escalation.GetUncommittedEvents().ShouldBeEmpty();
            escalation.AcknowledgedByRole.ShouldBe("Senior Clinician");
        }

        [Fact]
        public void A_resolved_escalation_cannot_be_acknowledged()
        {
            var escalation = AnAcknowledgedEscalation();
            escalation.Resolve(ActorId, "Senior Clinician", null, Now.AddMinutes(20));

            Should.Throw<DomainRuleViolationException>(
                () => escalation.Acknowledge(ActorId, "Senior Clinician", null, Now.AddMinutes(21)));
        }
    }

    public class Resolution
    {
        [Fact]
        public void Resolving_records_the_outcome_and_who_resolved_it()
        {
            var escalation = AnAcknowledgedEscalation();

            escalation.Resolve(ActorId, "Senior Clinician", "Patient moved to majors", Now.AddMinutes(30)).ShouldBeTrue();

            escalation.State.ShouldBe(Escalation.EscalationState.Resolved);
            escalation.ResolvedAt.ShouldBe(Now.AddMinutes(30));
            escalation.ResolvedByRole.ShouldBe("Senior Clinician");
            escalation.Notes.ShouldNotBeNull().ShouldContain("Patient moved to majors");
        }

        [Fact]
        public void Resolution_preserves_the_earlier_acknowledgement_note()
        {
            var escalation = AnAcknowledgedEscalation();

            escalation.Resolve(ActorId, "Senior Clinician", "Seen by registrar", Now.AddMinutes(30));

            escalation.Notes.ShouldNotBeNull().ShouldContain("Reviewing now");
            escalation.Notes.ShouldNotBeNull().ShouldContain("Seen by registrar");
        }

        [Fact]
        public void Resolving_twice_is_harmless()
        {
            var escalation = AnAcknowledgedEscalation();
            escalation.Resolve(ActorId, "Senior Clinician", null, Now.AddMinutes(30));

            escalation.Resolve(ActorId, "Senior Clinician", null, Now.AddMinutes(31)).ShouldBeFalse();
        }

        [Fact]
        public void An_unacknowledged_escalation_cannot_jump_straight_to_resolved()
        {
            var escalation = AWaitingTimeEscalation();

            Should.Throw<DomainRuleViolationException>(
                () => escalation.Resolve(ActorId, "Senior Clinician", null, Now.AddMinutes(1)));
        }
    }

    public class Reassignment
    {
        [Fact]
        public void Reassigning_hands_it_to_the_new_role_against_a_fresh_deadline()
        {
            var escalation = AnAcknowledgedEscalation();

            escalation.Reassign("Bed Manager", "Needs a bed decision", ActorId, "Nurse in Charge", Now.AddMinutes(40));

            escalation.ResponsibleRole.ShouldBe("Bed Manager");
            escalation.State.ShouldBe(Escalation.EscalationState.Created);
            escalation.AcknowledgedAt.ShouldBeNull();
            escalation.AcknowledgedByRole.ShouldBeNull();
            escalation.AcknowledgementDueAt.ShouldBe(Now.AddMinutes(40 + 15));

            var reassigned = escalation.GetUncommittedEvents().OfType<EscalationReassigned>().Single();
            reassigned.PreviousResponsibleRole.ShouldBe("Senior Clinician");
            reassigned.Reason.ShouldBe("Needs a bed decision");
            reassigned.ActorRole.ShouldBe("Nurse in Charge");
        }

        [Fact]
        public void A_reassignment_must_say_why_and_change_the_role()
        {
            var escalation = AWaitingTimeEscalation();

            Should.Throw<DomainRuleViolationException>(
                () => escalation.Reassign("Bed Manager", "", ActorId, "Nurse in Charge", Now));
            Should.Throw<DomainRuleViolationException>(
                () => escalation.Reassign("senior clinician", "Same person", ActorId, "Nurse in Charge", Now));
        }

        [Fact]
        public void A_resolved_escalation_cannot_be_reassigned()
        {
            var escalation = AnAcknowledgedEscalation();
            escalation.Resolve(ActorId, "Senior Clinician", null, Now.AddMinutes(30));

            Should.Throw<DomainRuleViolationException>(
                () => escalation.Reassign("Bed Manager", "Too late", ActorId, "Nurse in Charge", Now.AddMinutes(31)));
        }
    }

    public class TheManualFollowUpPath
    {
        [Fact]
        public void The_deadline_is_only_exceeded_once_it_has_passed_unacknowledged()
        {
            var escalation = AWaitingTimeEscalation();

            escalation.HasExceededAcknowledgementDeadline(Now.AddMinutes(15)).ShouldBeFalse();
            escalation.HasExceededAcknowledgementDeadline(Now.AddMinutes(16)).ShouldBeTrue();

            escalation.Acknowledge(ActorId, "Senior Clinician", null, Now.AddMinutes(16));
            escalation.HasExceededAcknowledgementDeadline(Now.AddMinutes(60)).ShouldBeFalse();
        }

        [Fact]
        public void An_escalation_nobody_acknowledged_can_be_marked_for_manual_follow_up()
        {
            var escalation = AWaitingTimeEscalation();
            var exceptionId = Guid.NewGuid();

            escalation.MarkForManualFollowUp(exceptionId, Guid.Empty, "System", Now.AddMinutes(16));

            escalation.State.ShouldBe(Escalation.EscalationState.ManualFollowUp);
            escalation.Notes.ShouldNotBeNull().ShouldContain("MANUAL FOLLOW-UP");
            escalation.GetUncommittedEvents().OfType<EscalationMarkedForManualFollowUp>().Single()
                .FollowUpExceptionId.ShouldBe(exceptionId);
        }

        [Fact]
        public void An_escalation_in_manual_follow_up_can_still_be_acknowledged()
        {
            // The missed deadline must not strand the escalation: someone picking it up late
            // is exactly the recovery path the workflow exists for.
            var escalation = AWaitingTimeEscalation();
            escalation.MarkForManualFollowUp(Guid.NewGuid(), Guid.Empty, "System", Now.AddMinutes(16));

            escalation.Acknowledge(ActorId, "Senior Clinician", "Picked up late", Now.AddMinutes(20));

            escalation.State.ShouldBe(Escalation.EscalationState.Acknowledged);
        }

        [Fact]
        public void An_escalation_in_manual_follow_up_can_be_resolved_directly()
        {
            var escalation = AWaitingTimeEscalation();
            escalation.MarkForManualFollowUp(Guid.NewGuid(), Guid.Empty, "System", Now.AddMinutes(16));

            escalation.Resolve(ActorId, "Matron", "Dealt with at handover", Now.AddMinutes(30));

            escalation.State.ShouldBe(Escalation.EscalationState.Resolved);
        }

        [Fact]
        public void An_acknowledged_escalation_is_not_a_candidate_for_manual_follow_up()
        {
            var escalation = AnAcknowledgedEscalation();

            Should.Throw<DomainRuleViolationException>(
                () => escalation.MarkForManualFollowUp(Guid.NewGuid(), Guid.Empty, "System", Now.AddMinutes(60)));
        }
    }

    public class EscalatingFurther
    {
        [Fact]
        public void An_acknowledged_escalation_can_be_passed_to_a_higher_role()
        {
            var escalation = AnAcknowledgedEscalation();

            escalation.EscalateToHigherAuthority("Consultant", ActorId, "Senior Clinician", "Needs consultant review", Now.AddMinutes(10));

            escalation.State.ShouldBe(Escalation.EscalationState.Escalated);
            escalation.ResponsibleRole.ShouldBe("Consultant");
        }

        [Fact]
        public void An_escalated_escalation_can_be_resolved()
        {
            var escalation = AnAcknowledgedEscalation();
            escalation.EscalateToHigherAuthority("Consultant", ActorId, "Senior Clinician", null, Now.AddMinutes(10));

            escalation.Resolve(ActorId, "Consultant", "Admitted", Now.AddMinutes(50));

            escalation.State.ShouldBe(Escalation.EscalationState.Resolved);
        }

        [Fact]
        public void An_unacknowledged_escalation_cannot_be_passed_upward()
        {
            var escalation = AWaitingTimeEscalation();

            Should.Throw<DomainRuleViolationException>(
                () => escalation.EscalateToHigherAuthority("Consultant", ActorId, "Staff Nurse", null, Now));
        }
    }

    public class Closure
    {
        [Fact]
        public void An_escalation_can_be_closed_from_any_open_state()
        {
            var escalation = AWaitingTimeEscalation();

            escalation.Close(ActorId, "Matron", "Duplicate of earlier escalation", Now.AddMinutes(3));

            escalation.State.ShouldBe(Escalation.EscalationState.Closed);
            escalation.ClosedAt.ShouldBe(Now.AddMinutes(3));
            escalation.Notes.ShouldNotBeNull().ShouldContain("CLOSED");
        }

        [Fact]
        public void A_closed_escalation_cannot_be_closed_again()
        {
            var escalation = AWaitingTimeEscalation();
            escalation.Close(ActorId, "Matron", null, Now);

            Should.Throw<DomainRuleViolationException>(() => escalation.Close(ActorId, "Matron", null, Now));
        }
    }

    public class TheAuditTrail
    {
        [Fact]
        public void Every_transition_appends_an_ordered_event_with_the_actors_role()
        {
            var escalation = AWaitingTimeEscalation();
            escalation.Acknowledge(ActorId, "Senior Clinician", "On it", Now.AddMinutes(2));
            escalation.Reassign("Bed Manager", "Bed needed", ActorId, "Senior Clinician", Now.AddMinutes(5));
            escalation.Close(ActorId, "Matron", null, Now.AddMinutes(9));

            var events = escalation.GetUncommittedEvents();

            events.Select(e => e.GetType()).ShouldBe([
                typeof(EscalationCreated),
                typeof(EscalationAcknowledged),
                typeof(EscalationReassigned),
                typeof(EscalationClosed)
            ]);

            events.Select(e => e.Version).ShouldBe([1, 2, 3, 4]);
            events.Select(e => e.ActorRole).ShouldBe(["Staff Nurse", "Senior Clinician", "Senior Clinician", "Matron"]);
            events.Select(e => e.OccurredAt).ShouldBe([Now, Now.AddMinutes(2), Now.AddMinutes(5), Now.AddMinutes(9)]);
        }
    }
}
