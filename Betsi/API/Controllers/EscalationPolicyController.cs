namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using Betsi.Application.Escalations;
using Betsi.Domain.Aggregates;
using MediatR;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The site's waiting-time escalation policy and its change control (MVP-020).
/// </summary>
/// <remarks>
/// A change is proposed, then approved by a different, supervisory user with an effective time.
/// Nothing here edits a revision in place. Rollback proposes a new revision restoring an
/// earlier one, which is approved like any other change.
/// </remarks>
[ApiController]
[Route("api/v1/escalation-policy")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public sealed class EscalationPolicyController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly EscalationQueries _queries;

    public EscalationPolicyController(IMediator mediator, EscalationQueries queries)
    {
        _mediator = mediator;
        _queries = queries;
    }

    /// <summary>The revision in force, and every revision with its approval history.</summary>
    [HttpGet]
    [ProducesResponseType<PolicyOverview>(StatusCodes.Status200OK)]
    public Task<PolicyOverview> GetOverview(CancellationToken cancellationToken) =>
        _queries.GetPolicyOverviewAsync(cancellationToken);

    [HttpGet("revisions/{revision:int}")]
    [ProducesResponseType<PolicyRevisionView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRevision(int revision, CancellationToken cancellationToken) =>
        await _queries.GetPolicyRevisionAsync(revision, cancellationToken) is { } view
            ? Ok(view)
            : Problem(title: "Resource not found", detail: $"Policy revision {revision} does not exist.",
                statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Shows how many patients waiting right now each proposed tier would apply to. Changes nothing.
    /// </summary>
    [HttpPost("preview")]
    [ProducesResponseType<PolicyPreview>(StatusCodes.Status200OK)]
    public Task<PolicyPreview> Preview([FromBody] List<WaitingTimeTierInput> tiers, CancellationToken cancellationToken) =>
        _queries.PreviewAsync(
            tiers.Select((t, i) => new WaitingTimeTier(
                i + 1, t.ThresholdMinutes, t.ResponsibleRole.Trim(), t.AcknowledgementDeadlineMinutes, t.RecommendedAction.Trim()))
                .ToList(),
            cancellationToken);

    /// <summary>Proposes a new revision. It has no effect until approved.</summary>
    [HttpPost("proposals")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Propose(
        [FromBody] ProposeEscalationPolicyCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetOverview), null, result);
    }

    /// <summary>Proposes a new revision restoring an earlier approved revision.</summary>
    [HttpPost("proposals/restore")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ProposeRestoration(
        [FromBody] ProposeEscalationPolicyRestorationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetOverview), null, result);
    }

    /// <summary>Approves a proposal. Must be a different, supervisory user from the proposer.</summary>
    [HttpPost("{policyId:guid}/approve")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> Approve(
        Guid policyId, [FromBody] ApproveEscalationPolicyCommand command, CancellationToken cancellationToken)
    {
        command.PolicyId = policyId;
        return _mediator.Send(command, cancellationToken);
    }

    [HttpPost("{policyId:guid}/reject")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> Reject(
        Guid policyId, [FromBody] RejectEscalationPolicyCommand command, CancellationToken cancellationToken)
    {
        command.PolicyId = policyId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Withdraws a proposal, or an approval that has not yet taken effect.</summary>
    [HttpPost("{policyId:guid}/withdraw")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> Withdraw(
        Guid policyId, [FromBody] WithdrawEscalationPolicyCommand command, CancellationToken cancellationToken)
    {
        command.PolicyId = policyId;
        return _mediator.Send(command, cancellationToken);
    }
}
