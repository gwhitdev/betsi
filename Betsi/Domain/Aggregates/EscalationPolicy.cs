namespace Betsi.Domain.Aggregates;

/// <summary>
/// One threshold of a waiting-time escalation policy (spec §2: coordinator, senior, capacity).
/// </summary>
/// <param name="Level">1 for the first threshold, 2 for the next, and so on.</param>
/// <param name="ThresholdMinutes">Minutes since arrival after which this tier applies.</param>
/// <param name="ResponsibleRole">Who the escalation is assigned to.</param>
/// <param name="AcknowledgementDeadlineMinutes">How long that role has to acknowledge it.</param>
/// <param name="RecommendedAction">What the role is expected to do, shown on the escalation.</param>
public sealed record WaitingTimeTier(
    int Level,
    int ThresholdMinutes,
    string ResponsibleRole,
    int AcknowledgementDeadlineMinutes,
    string RecommendedAction);

/// <summary>
/// Who may approve safety configuration and close follow-up exceptions.
/// </summary>
/// <remarks>
/// Spec §2: MVP enforces a default role matrix; site-specific role mappings are v1.1. The
/// acting role comes from the authenticated principal — an OIDC token, or the Development-only
/// header scheme — so this is enforcement, not documentation. The permission model
/// (<c>Security/Permissions.cs</c>) decides who may reach these operations at all; this decides
/// who may take the decision once here.
/// </remarks>
public static class EscalationAuthority
{
    public static readonly IReadOnlySet<string> SupervisoryRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Clinical Lead",
            "Operations Manager",
            "Site Manager",
            "Matron"
        };

    public static bool IsSupervisory(string? role) => role is not null && SupervisoryRoles.Contains(role);

    /// <summary>Owner of follow-up exceptions for escalations raised without a policy.</summary>
    public const string DefaultFollowUpOwnerRole = "Operations Manager";
}

/// <summary>
/// A site's waiting-time escalation policy, as one change-controlled revision (MVP-020).
/// </summary>
/// <remarks>
/// Spec §9: threshold changes are versioned, approved by someone other than the requester,
/// take effect from a stated time, and can be rolled back. A revision is never edited: a change
/// is a new revision. The policy in force at any moment is the approved revision with the
/// latest effective time not after that moment.
///
/// There is no default policy. A site with no approved revision has no automatic escalation,
/// and says so on the escalation board — a silent software default would stand in for a
/// clinical decision nobody made (spec §6).
/// </remarks>
public class EscalationPolicy : AggregateRoot
{
    public enum PolicyState
    {
        Proposed = 1,
        Approved = 2,
        Rejected = 3,
        Withdrawn = 4
    }

    public const int MaxTiers = 5;
    public const int MinThresholdMinutes = 15;
    public const int MaxThresholdMinutes = 72 * 60;
    public const int MinDeadlineMinutes = 5;
    public const int MaxDeadlineMinutes = 240;

    /// <summary>How far in the past an effective time may be, to absorb clock skew. No retroactive policy.</summary>
    public static readonly TimeSpan EffectiveTimeTolerance = TimeSpan.FromMinutes(1);

    private List<WaitingTimeTier> _tiers = [];

    /// <summary>Sequential per site. What staff and auditors refer to: "revision 4".</summary>
    public int Revision { get; private set; }

    public PolicyState State { get; private set; }

    /// <summary>False means the site has deliberately turned automatic waiting-time escalation off.</summary>
    public bool Enabled { get; private set; }

    public IReadOnlyList<WaitingTimeTier> Tiers => _tiers.AsReadOnly();

    /// <summary>Who reviews follow-up exceptions raised under this policy.</summary>
    public string FollowUpOwnerRole { get; private set; } = string.Empty;

    public string ProposalReason { get; private set; } = string.Empty;
    public Guid ProposedByActorId { get; private set; }
    public string ProposedByRole { get; private set; } = string.Empty;
    public DateTime ProposedAt { get; private set; }

    /// <summary>Set when this revision restores an earlier one.</summary>
    public int? RestoresRevision { get; private set; }

    public Guid? DecidedByActorId { get; private set; }
    public string? DecidedByRole { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionReason { get; private set; }

    public DateTime? EffectiveFrom { get; private set; }

    protected EscalationPolicy() { }

    public static EscalationPolicy Propose(
        Guid tenantId,
        int revision,
        bool enabled,
        IReadOnlyList<WaitingTimeTier> tiers,
        string followUpOwnerRole,
        string reason,
        Guid actorId,
        string actorRole,
        DateTime now,
        int? restoresRevision = null)
    {
        if (revision < 1)
            throw new DomainRuleViolationException("Policy revisions start at 1.");

        RequireIdentified(actorId, "propose a policy change");
        RequireReason(reason, "A policy change");

        if (string.IsNullOrWhiteSpace(followUpOwnerRole))
            throw new DomainRuleViolationException("A policy must name who reviews follow-up exceptions.");

        ValidateTiers(enabled, tiers);

        var policy = new EscalationPolicy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Revision = revision,
            State = PolicyState.Proposed,
            Enabled = enabled,
            _tiers = tiers.ToList(),
            FollowUpOwnerRole = followUpOwnerRole.Trim(),
            ProposalReason = reason.Trim(),
            ProposedByActorId = actorId,
            ProposedByRole = actorRole,
            ProposedAt = now,
            RestoresRevision = restoresRevision
        };

        policy.RaiseDomainEvent(new EscalationPolicyProposed
        {
            Revision = revision,
            Enabled = enabled,
            Tiers = tiers.ToList(),
            FollowUpOwnerRole = policy.FollowUpOwnerRole,
            Reason = policy.ProposalReason,
            RestoresRevision = restoresRevision,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });

        return policy;
    }

    /// <summary>A new proposal with this revision's content, restoring it (spec §9 rollback).</summary>
    public EscalationPolicy ProposeRestoration(int newRevision, string reason, Guid actorId, string actorRole, DateTime now)
    {
        if (State != PolicyState.Approved)
            throw new DomainRuleViolationException($"Only an approved revision can be restored; revision {Revision} is {State}.");

        return Propose(TenantId, newRevision, Enabled, Tiers, FollowUpOwnerRole, reason, actorId, actorRole, now, Revision);
    }

    public void Approve(Guid actorId, string actorRole, string reason, DateTime? effectiveFrom, DateTime now)
    {
        RequireProposed("approve");
        RequireIdentified(actorId, "approve a policy change");
        RequireReason(reason, "An approval");

        // Two-person rule: the person who wrote a threshold is not the person who signs it off.
        if (actorId == ProposedByActorId)
            throw new DomainRuleViolationException("A policy change must be approved by someone other than the person who proposed it.");

        RequireSupervisory(actorRole, "approve a policy change");

        var effective = effectiveFrom ?? now;
        if (effective < now - EffectiveTimeTolerance)
            throw new DomainRuleViolationException("A policy cannot take effect in the past.");

        State = PolicyState.Approved;
        EffectiveFrom = effective;
        Decide(actorId, actorRole, reason, now);

        RaiseDomainEvent(new EscalationPolicyApproved
        {
            Revision = Revision,
            EffectiveFrom = effective,
            Reason = DecisionReason!,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    public void Reject(Guid actorId, string actorRole, string reason, DateTime now)
    {
        RequireProposed("reject");
        RequireIdentified(actorId, "reject a policy change");
        RequireReason(reason, "A rejection");
        RequireSupervisory(actorRole, "reject a policy change");

        State = PolicyState.Rejected;
        Decide(actorId, actorRole, reason, now);

        RaiseDomainEvent(new EscalationPolicyRejected
        {
            Revision = Revision,
            Reason = DecisionReason!,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    /// <summary>
    /// Withdraws a proposal, or an approval that has not yet taken effect. A policy that has
    /// taken effect is replaced by approving a new revision, never withdrawn, so there is no
    /// moment at which the site silently has no policy.
    /// </summary>
    public void Withdraw(Guid actorId, string actorRole, string reason, DateTime now)
    {
        RequireIdentified(actorId, "withdraw a policy change");
        RequireReason(reason, "A withdrawal");

        switch (State)
        {
            case PolicyState.Proposed:
                break;
            case PolicyState.Approved when EffectiveFrom > now:
                RequireSupervisory(actorRole, "withdraw an approved policy change");
                break;
            case PolicyState.Approved:
                throw new DomainRuleViolationException(
                    $"Revision {Revision} is already in effect. Approve a new revision to replace it.");
            default:
                throw new DomainRuleViolationException($"Revision {Revision} is {State} and cannot be withdrawn.");
        }

        State = PolicyState.Withdrawn;
        Decide(actorId, actorRole, reason, now);

        RaiseDomainEvent(new EscalationPolicyWithdrawn
        {
            Revision = Revision,
            Reason = DecisionReason!,
            ActorId = actorId,
            ActorRole = actorRole,
            OccurredAt = now
        });
    }

    public bool IsInEffectAt(DateTime now) => State == PolicyState.Approved && EffectiveFrom <= now;

    /// <summary>Every tier whose threshold a patient who has waited this long has reached.</summary>
    public IEnumerable<WaitingTimeTier> TiersReachedAfter(int waitedMinutes) =>
        Enabled ? _tiers.Where(t => waitedMinutes >= t.ThresholdMinutes) : [];

    internal static void ValidateTiers(bool enabled, IReadOnlyList<WaitingTimeTier> tiers)
    {
        if (!enabled)
        {
            if (tiers.Count > 0)
                throw new DomainRuleViolationException("A disabled policy must not define tiers; they would never apply.");
            return;
        }

        if (tiers.Count is 0 or > MaxTiers)
            throw new DomainRuleViolationException($"An enabled policy needs between 1 and {MaxTiers} tiers.");

        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            var label = $"Tier {i + 1}";

            if (tier.Level != i + 1)
                throw new DomainRuleViolationException($"{label} must have level {i + 1}; tiers are numbered in order from 1.");

            if (tier.ThresholdMinutes is < MinThresholdMinutes or > MaxThresholdMinutes)
                throw new DomainRuleViolationException($"{label} threshold must be between {MinThresholdMinutes} and {MaxThresholdMinutes} minutes.");

            // Not sorted on the caller's behalf: out-of-order thresholds usually mean a typo
            // ("40" for "400"), and quietly reordering would approve the typo.
            if (i > 0 && tier.ThresholdMinutes <= tiers[i - 1].ThresholdMinutes)
                throw new DomainRuleViolationException($"{label} threshold must be later than tier {i}'s.");

            if (string.IsNullOrWhiteSpace(tier.ResponsibleRole) || tier.ResponsibleRole.Length > 100)
                throw new DomainRuleViolationException($"{label} must name a responsible role of at most 100 characters.");

            if (tier.AcknowledgementDeadlineMinutes is < MinDeadlineMinutes or > MaxDeadlineMinutes)
                throw new DomainRuleViolationException($"{label} acknowledgement deadline must be between {MinDeadlineMinutes} and {MaxDeadlineMinutes} minutes.");

            if (string.IsNullOrWhiteSpace(tier.RecommendedAction) || tier.RecommendedAction.Length > 500)
                throw new DomainRuleViolationException($"{label} must state a recommended action of at most 500 characters.");
        }
    }

    private void RequireProposed(string action)
    {
        if (State != PolicyState.Proposed)
            throw new DomainRuleViolationException($"Cannot {action} revision {Revision}: it is {State}.");
    }

    private static void RequireIdentified(Guid actorId, string action)
    {
        // Change control needs a named person on both sides of the two-person rule.
        if (actorId == Guid.Empty)
            throw new DomainRuleViolationException($"An identified user is required to {action}.");
    }

    private static void RequireReason(string reason, string what)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleViolationException($"{what} must record a reason.");
    }

    private static void RequireSupervisory(string actorRole, string action)
    {
        if (!EscalationAuthority.IsSupervisory(actorRole))
        {
            throw new DomainRuleViolationException(
                $"Only {string.Join(", ", EscalationAuthority.SupervisoryRoles.Order())} may {action}.");
        }
    }

    private void Decide(Guid actorId, string actorRole, string reason, DateTime now)
    {
        DecidedByActorId = actorId;
        DecidedByRole = actorRole;
        DecidedAt = now;
        DecisionReason = reason.Trim();
    }
}

public class EscalationPolicyProposed : DomainEvent
{
    public int Revision { get; set; }
    public bool Enabled { get; set; }
    public List<WaitingTimeTier> Tiers { get; set; } = [];
    public string FollowUpOwnerRole { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int? RestoresRevision { get; set; }
}

public class EscalationPolicyApproved : DomainEvent
{
    public int Revision { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class EscalationPolicyRejected : DomainEvent
{
    public int Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class EscalationPolicyWithdrawn : DomainEvent
{
    public int Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
}
