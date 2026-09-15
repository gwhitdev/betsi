namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using Betsi.Application.Escalations;
using Betsi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

/// <summary>
/// Escalation lifecycle, board and audit trail (MVP-020–025).
/// </summary>
[ApiController]
[Route("api/v1/escalations")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public sealed class EscalationsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly EscalationQueries _queries;

    public EscalationsController(IMediator mediator, EscalationQueries queries)
    {
        _mediator = mediator;
        _queries = queries;
    }

    /// <summary>
    /// The escalation board: awaiting acknowledgement, active, open follow-up exceptions,
    /// recent history, metrics, and whether a policy is in force.
    /// </summary>
    /// <param name="historyDays">How many days of resolved and closed escalations to include (1–30, default 7).</param>
    [HttpGet("board")]
    [Authorize(Policy = Permissions.EscalationsRead)]
    [AuditRead("EscalationBoard")]
    [ProducesResponseType<EscalationBoard>(StatusCodes.Status200OK)]
    public Task<EscalationBoard> GetBoard([FromQuery] int historyDays = 7, CancellationToken cancellationToken = default) =>
        _queries.GetBoardAsync(historyDays, cancellationToken);

    /// <summary>
    /// Every recorded transition of an escalation and its follow-up exceptions, oldest first.
    /// </summary>
    /// <param name="escalationId">The escalation.</param>
    /// <param name="format"><c>json</c> (default) or <c>csv</c> for export.</param>
    [HttpGet("{escalationId:guid}/audit")]
    [Authorize(Policy = Permissions.EscalationsRead)]
    [AuditRead("EscalationAuditTrail", "escalationId")]
    [Produces("application/json", "text/csv")]
    [ProducesResponseType<IReadOnlyList<AuditTrailEntry>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuditTrail(
        Guid escalationId, [FromQuery] string format = "json", CancellationToken cancellationToken = default)
    {
        var entries = await _queries.GetAuditTrailAsync(escalationId, cancellationToken);

        return format.ToLowerInvariant() switch
        {
            "json" => Ok(entries),
            "csv" => File(
                Encoding.UTF8.GetBytes(EscalationQueries.ToCsv(entries)),
                "text/csv; charset=utf-8",
                $"escalation-{escalationId}-audit.csv"),
            _ => Problem(
                title: "Unsupported format",
                detail: "format must be 'json' or 'csv'.",
                statusCode: StatusCodes.Status400BadRequest)
        };
    }

    /// <summary>Raises an escalation because a patient has breached a waiting-time threshold.</summary>
    [HttpPost("waiting-time")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TriggerWaitingTimeEscalation(
        [FromBody] TriggerWaitingTimeEscalationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);

        return CreatedAtAction(
            nameof(TriggerWaitingTimeEscalation),
            new { escalationId = result.AggregateId },
            result);
    }

    /// <summary>Acknowledges an escalation, confirming someone has taken it on. Repeating it is harmless.</summary>
    [HttpPost("{escalationId:guid}/acknowledge")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<CommandResult> AcknowledgeEscalation(
        Guid escalationId,
        [FromBody] AcknowledgeEscalationCommand command,
        CancellationToken cancellationToken)
    {
        command.EscalationId = escalationId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Resolves an escalation once the underlying issue has been dealt with. Repeating it is harmless.</summary>
    [HttpPost("{escalationId:guid}/resolve")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<CommandResult> ResolveEscalation(
        Guid escalationId,
        [FromBody] ResolveEscalationCommand command,
        CancellationToken cancellationToken)
    {
        command.EscalationId = escalationId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Reassigns an escalation to another role, which must acknowledge it against a new deadline.</summary>
    [HttpPost("{escalationId:guid}/reassign")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<CommandResult> ReassignEscalation(
        Guid escalationId,
        [FromBody] ReassignEscalationCommand command,
        CancellationToken cancellationToken)
    {
        command.EscalationId = escalationId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Records the review of a missed acknowledgement and closes the follow-up exception.</summary>
    [HttpPost("follow-ups/{followUpExceptionId:guid}/close")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<CommandResult> CloseFollowUpException(
        Guid followUpExceptionId,
        [FromBody] CloseFollowUpExceptionCommand command,
        CancellationToken cancellationToken)
    {
        command.FollowUpExceptionId = followUpExceptionId;
        return _mediator.Send(command, cancellationToken);
    }
}
