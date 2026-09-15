namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

/// <summary>
/// Waiting-time thresholds are clinical safety configuration (spec §9). They are versioned,
/// validated, approved by a second supervisory person, effective from a stated time, and never
/// edited in place.
/// </summary>
public class EscalationPolicyTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid Proposer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    internal static List<WaitingTimeTier> SpecTiers() =>
    [
        new(1, 240, "Waiting-room Coordinator", 30, "Review clinical status, consider bed allocation or reassessment"),
        new(2, 360, "Nurse in Charge", 30, "Clinical decision on admission; escalate to bed management"),
        new(3, 480, "Bed Manager", 30, "Escalate to site leadership or activate capacity protocols")
    ];

    private static EscalationPolicy AProposal(IReadOnlyList<WaitingTimeTier>? tiers = null, bool enabled = true) =>
        EscalationPolicy.Propose(TenantId, 1, enabled, tiers ?? SpecTiers(), "Operations Manager",
            "Initial policy agreed at ED governance 2026-09-10", Proposer, "Site Administrator", Now);

    private static EscalationPolicy AnApprovedPolicy(DateTime? effectiveFrom = null)
    {
        var policy = AProposal();
        policy.Approve(Approver, "Clinical Lead", "Signed off by ED clinical lead", effectiveFrom, Now.AddMinutes(10));
        return policy;
    }

    public class Proposing
    {
        [Fact]
        public void A_proposal_records_its_content_and_proposer_and_is_not_in_effect()
        {
            var policy = AProposal();

            policy.State.ShouldBe(EscalationPolicy.PolicyState.Proposed);
            policy.Revision.ShouldBe(1);
            policy.Tiers.Count.ShouldBe(3);
            policy.ProposedByActorId.ShouldBe(Proposer);
            policy.IsInEffectAt(Now.AddYears(1)).ShouldBeFalse();

            var proposed = policy.GetUncommittedEvents().Single().ShouldBeOfType<EscalationPolicyProposed>();
            proposed.Tiers.ShouldBe(SpecTiers());
            proposed.Reason.ShouldContain("governance");
        }

        [Fact]
        public void An_anonymous_user_cannot_propose()
        {
            Should.Throw<DomainRuleViolationException>(() => EscalationPolicy.Propose(
                TenantId, 1, true, SpecTiers(), "Operations Manager", "Reason", Guid.Empty, "Unknown", Now));
        }

        [Fact]
        public void A_proposal_must_give_a_reason_and_a_follow_up_owner()
        {
            Should.Throw<DomainRuleViolationException>(() => EscalationPolicy.Propose(
                TenantId, 1, true, SpecTiers(), "Operations Manager", " ", Proposer, "Admin", Now));
            Should.Throw<DomainRuleViolationException>(() => EscalationPolicy.Propose(
                TenantId, 1, true, SpecTiers(), "", "Reason", Proposer, "Admin", Now));
        }

        public static TheoryData<string, List<WaitingTimeTier>> UnsafeTiers => new()
        {
            { "no tiers", [] },
            { "threshold too short", [new(1, 5, "Coordinator", 30, "Review")] },
            { "threshold beyond 72h", [new(1, 72 * 60 + 1, "Coordinator", 30, "Review")] },
            { "thresholds out of order", [new(1, 400, "A", 30, "Review"), new(2, 240, "B", 30, "Review")] },
            { "duplicate thresholds", [new(1, 240, "A", 30, "Review"), new(2, 240, "B", 30, "Review")] },
            { "deadline too short", [new(1, 240, "Coordinator", 1, "Review")] },
            { "deadline too long", [new(1, 240, "Coordinator", 241, "Review")] },
            { "no responsible role", [new(1, 240, " ", 30, "Review")] },
            { "no recommended action", [new(1, 240, "Coordinator", 30, "")] },
            { "levels not numbered from 1", [new(2, 240, "Coordinator", 30, "Review")] },
            {
                "more than five tiers",
                [.. Enumerable.Range(1, 6).Select(i => new WaitingTimeTier(i, 60 * i, "Role", 30, "Review"))]
            }
        };

        [Theory]
        [MemberData(nameof(UnsafeTiers))]
        public void Unsafe_or_malformed_tiers_are_refused(string _, List<WaitingTimeTier> tiers)
        {
            Should.Throw<DomainRuleViolationException>(() => AProposal(tiers));
        }

        [Fact]
        public void A_site_can_propose_turning_automatic_escalation_off_but_not_with_tiers()
        {
            AProposal(tiers: [], enabled: false).Enabled.ShouldBeFalse();

            Should.Throw<DomainRuleViolationException>(() => AProposal(SpecTiers(), enabled: false));
        }
    }

    public class Approving
    {
        [Fact]
        public void Approval_by_a_second_supervisory_user_puts_it_in_effect()
        {
            var policy = AnApprovedPolicy();

            policy.State.ShouldBe(EscalationPolicy.PolicyState.Approved);
            policy.EffectiveFrom.ShouldBe(Now.AddMinutes(10));
            policy.DecidedByActorId.ShouldBe(Approver);
            policy.DecidedByRole.ShouldBe("Clinical Lead");
            policy.IsInEffectAt(Now.AddMinutes(10)).ShouldBeTrue();
        }

        [Fact]
        public void The_proposer_cannot_approve_their_own_change()
        {
            var policy = AProposal();

            Should.Throw<DomainRuleViolationException>(
                    () => policy.Approve(Proposer, "Clinical Lead", "Looks right", null, Now))
                .Message.ShouldContain("someone other than");
        }

        [Fact]
        public void A_non_supervisory_role_cannot_approve()
        {
            var policy = AProposal();

            Should.Throw<DomainRuleViolationException>(
                () => policy.Approve(Approver, "Staff Nurse", "Looks right", null, Now));
        }

        [Fact]
        public void An_approval_must_give_a_reason()
        {
            Should.Throw<DomainRuleViolationException>(
                () => AProposal().Approve(Approver, "Clinical Lead", "", null, Now));
        }

        [Fact]
        public void A_future_effective_time_is_not_in_effect_until_it_arrives()
        {
            var policy = AnApprovedPolicy(effectiveFrom: Now.AddDays(2));

            policy.IsInEffectAt(Now.AddDays(1)).ShouldBeFalse();
            policy.IsInEffectAt(Now.AddDays(2)).ShouldBeTrue();
        }

        [Fact]
        public void A_policy_cannot_take_effect_retrospectively()
        {
            Should.Throw<DomainRuleViolationException>(
                () => AProposal().Approve(Approver, "Clinical Lead", "Backdate", Now.AddHours(-1), Now));
        }

        [Fact]
        public void An_approved_revision_cannot_be_approved_or_rejected_again()
        {
            var policy = AnApprovedPolicy();

            Should.Throw<DomainRuleViolationException>(() => policy.Approve(Guid.NewGuid(), "Matron", "Again", null, Now.AddHours(1)));
            Should.Throw<DomainRuleViolationException>(() => policy.Reject(Guid.NewGuid(), "Matron", "Changed mind", Now.AddHours(1)));
        }
    }

    public class RejectingAndWithdrawing
    {
        [Fact]
        public void A_supervisor_can_reject_a_proposal_with_a_reason()
        {
            var policy = AProposal();

            policy.Reject(Approver, "Matron", "8h tier should go to the site manager", Now.AddHours(1));

            policy.State.ShouldBe(EscalationPolicy.PolicyState.Rejected);
            policy.DecisionReason.ShouldNotBeNull().ShouldContain("site manager");
        }

        [Fact]
        public void A_proposal_can_be_withdrawn()
        {
            var policy = AProposal();

            policy.Withdraw(Proposer, "Site Administrator", "Superseded by a corrected proposal", Now.AddMinutes(5));

            policy.State.ShouldBe(EscalationPolicy.PolicyState.Withdrawn);
        }

        [Fact]
        public void An_approval_that_has_not_yet_taken_effect_can_be_withdrawn_by_a_supervisor()
        {
            var policy = AnApprovedPolicy(effectiveFrom: Now.AddDays(3));

            Should.Throw<DomainRuleViolationException>(
                () => policy.Withdraw(Proposer, "Site Administrator", "Wrong date", Now.AddDays(1)));

            policy.Withdraw(Approver, "Clinical Lead", "Wrong date", Now.AddDays(1));
            policy.State.ShouldBe(EscalationPolicy.PolicyState.Withdrawn);
        }

        [Fact]
        public void A_policy_in_effect_cannot_be_withdrawn_so_the_site_is_never_silently_without_one()
        {
            var policy = AnApprovedPolicy();

            Should.Throw<DomainRuleViolationException>(
                    () => policy.Withdraw(Approver, "Clinical Lead", "Turn it off", Now.AddHours(1)))
                .Message.ShouldContain("Approve a new revision");
        }
    }

    public class Restoring
    {
        [Fact]
        public void An_approved_revision_can_be_restored_as_a_new_proposal()
        {
            var original = AnApprovedPolicy();

            var restoration = original.ProposeRestoration(5, "Revision 4 thresholds caused alert fatigue", Proposer, "Site Administrator", Now.AddDays(10));

            restoration.Revision.ShouldBe(5);
            restoration.RestoresRevision.ShouldBe(original.Revision);
            restoration.State.ShouldBe(EscalationPolicy.PolicyState.Proposed);
            restoration.Tiers.ShouldBe(original.Tiers);
            restoration.Id.ShouldNotBe(original.Id);
        }

        [Fact]
        public void A_revision_that_was_never_approved_cannot_be_restored()
        {
            var rejected = AProposal();
            rejected.Reject(Approver, "Matron", "No", Now);

            Should.Throw<DomainRuleViolationException>(
                () => rejected.ProposeRestoration(2, "Try again", Proposer, "Site Administrator", Now));
        }
    }

    [Fact]
    public void Tiers_reached_are_every_threshold_the_wait_has_passed()
    {
        var policy = AnApprovedPolicy();

        policy.TiersReachedAfter(239).ShouldBeEmpty();
        policy.TiersReachedAfter(240).Select(t => t.Level).ShouldBe([1]);
        policy.TiersReachedAfter(500).Select(t => t.Level).ShouldBe([1, 2, 3]);
    }
}
