namespace Betsi.Application.Commands;

using Betsi.Domain.Aggregates;
using Betsi.Licensing;
using Betsi.Security;

// Every command here is part of the escalation workflow, which licensing must never disable
// (spec §5). That includes policy change control: an expired licence must not stop a site
// correcting an unsafe threshold.

// ============= Escalation workflow (MVP-025) =============

/// <summary>Hands an escalation to a different role, which must acknowledge it afresh.</summary>
[AlwaysAvailable("Escalation workflow: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.EscalationsRespond)]
public class ReassignEscalationCommand : ICommand
{
    public Guid EscalationId { get; set; }
    public string ResponsibleRole { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Records the supervisory review of a missed acknowledgement and closes the exception.</summary>
[AlwaysAvailable("Escalation workflow: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.EscalationsRespond)]
public class CloseFollowUpExceptionCommand : ICommand
{
    public Guid FollowUpExceptionId { get; set; }
    public FollowUpException.ReviewOutcome Outcome { get; set; }
    public string ReviewNotes { get; set; } = string.Empty;
}

// ============= Raised by the waiting-time monitor (MVP-021, MVP-022) =============
//
// These are dispatched only by WaitingTimeMonitor and are deliberately not bound to any HTTP
// endpoint: they carry the evaluation time, which a caller must not be able to choose. A test
// asserts no controller accepts them.

/// <summary>Raises the escalation for one policy tier a waiting patient has reached. Idempotent.</summary>
[AlwaysAvailable("Automatic escalation: a safety function licensing must never disable (spec §5).")]
[SystemOnly]
public class RaisePolicyEscalationCommand : ICommand
{
    public Guid PatientEpisodeId { get; set; }
    public int TierLevel { get; set; }
    public DateTime EvaluatedAt { get; set; }
}

/// <summary>Raises the follow-up exception for an escalation that missed its deadline. Idempotent.</summary>
[AlwaysAvailable("Missed-acknowledgement follow-up: a safety function licensing must never disable (spec §5).")]
[SystemOnly]
public class RaiseFollowUpExceptionCommand : ICommand
{
    public Guid EscalationId { get; set; }
    public DateTime EvaluatedAt { get; set; }
}

// ============= Policy change control (MVP-020) =============

public sealed class WaitingTimeTierInput
{
    public int ThresholdMinutes { get; set; }
    public string ResponsibleRole { get; set; } = string.Empty;
    public int AcknowledgementDeadlineMinutes { get; set; }
    public string RecommendedAction { get; set; } = string.Empty;
}

/// <summary>Proposes a new policy revision. Takes no effect until someone else approves it.</summary>
[AlwaysAvailable("Escalation policy change control: sites must be able to correct thresholds regardless of licence (spec §5).")]
[RequiresPermission(Permissions.PolicyPropose)]
public class ProposeEscalationPolicyCommand : ICommand
{
    public bool Enabled { get; set; } = true;

    /// <summary>In threshold order. Levels are assigned 1, 2, 3… in the order given.</summary>
    public List<WaitingTimeTierInput> Tiers { get; set; } = [];

    public string FollowUpOwnerRole { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Proposes a new revision restoring an earlier approved one (rollback).</summary>
[AlwaysAvailable("Escalation policy change control: sites must be able to correct thresholds regardless of licence (spec §5).")]
[RequiresPermission(Permissions.PolicyPropose)]
public class ProposeEscalationPolicyRestorationCommand : ICommand
{
    public int Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
}

[AlwaysAvailable("Escalation policy change control: sites must be able to correct thresholds regardless of licence (spec §5).")]
[RequiresPermission(Permissions.PolicyDecide)]
public class ApproveEscalationPolicyCommand : ICommand
{
    public Guid PolicyId { get; set; }
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>UTC. Omit to take effect immediately.</summary>
    public DateTime? EffectiveFrom { get; set; }
}

[AlwaysAvailable("Escalation policy change control: sites must be able to correct thresholds regardless of licence (spec §5).")]
[RequiresPermission(Permissions.PolicyDecide)]
public class RejectEscalationPolicyCommand : ICommand
{
    public Guid PolicyId { get; set; }
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = string.Empty;
}

[AlwaysAvailable("Escalation policy change control: sites must be able to correct thresholds regardless of licence (spec §5).")]
[RequiresPermission(Permissions.PolicyPropose)]
public class WithdrawEscalationPolicyCommand : ICommand
{
    public Guid PolicyId { get; set; }
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = string.Empty;
}
