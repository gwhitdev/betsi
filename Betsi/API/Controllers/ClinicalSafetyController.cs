namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using Betsi.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

/// <summary>Clinical safety facts and staff-raised concerns attached to an episode.</summary>
[ApiController]
[Route("api/v1/episodes/{episodeId:guid}")]
[Produces("application/json")]
public sealed class ClinicalSafetyController(IMediator mediator) : ControllerBase
{
    /// <summary>Assigns clinical staff and records whether paediatric competence is held.</summary>
    [HttpPost("staff-assignment")]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommandResult> AssignClinicalStaff(
        Guid episodeId, AssignClinicalStaffCommand command, CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = episodeId;
        return mediator.Send(command, cancellationToken);
    }

    /// <summary>Records whether a carer is currently with the patient.</summary>
    [HttpPost("carer-presence")]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommandResult> RecordCarerPresence(
        Guid episodeId, RecordCarerPresenceCommand command, CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = episodeId;
        return mediator.Send(command, cancellationToken);
    }

    /// <summary>Raises a safeguarding concern and its linked escalation atomically.</summary>
    [HttpPost("safeguarding-concerns")]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommandResult> RaiseSafeguardingConcern(
        Guid episodeId, RaiseSafeguardingConcernCommand command, CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = episodeId;
        return mediator.Send(command, cancellationToken);
    }

    /// <summary>Records clinical deterioration and raises its linked escalation atomically.</summary>
    [HttpPost("deterioration-flags")]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommandResult> FlagDeterioration(
        Guid episodeId, FlagPatientDeteriorationCommand command, CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = episodeId;
        return mediator.Send(command, cancellationToken);
    }
}
