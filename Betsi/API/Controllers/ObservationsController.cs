namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using Betsi.Application.Queries;
using Betsi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Record observations and retrieve their complete correction history.</summary>
[ApiController]
[Route("api/v1/episodes/{episodeId:guid}/observations")]
[Produces("application/json")]
public sealed class ObservationsController(IMediator mediator, ObservationQueries queries) : ControllerBase
{
    /// <summary>Records an observation or a correction preserving the original measurements.</summary>
    [HttpPost]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommandResult> Record(Guid episodeId, RecordObservationCommand command, CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = episodeId;
        return mediator.Send(command, cancellationToken);
    }

    /// <summary>Returns original and corrected observations in recording order.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ObservationsRead)]
    [AuditRead("ObservationHistory", "episodeId")]
    [ProducesResponseType<IReadOnlyList<ObservationView>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IReadOnlyList<ObservationView>> History(Guid episodeId, CancellationToken cancellationToken)
    {
        return await queries.GetHistoryAsync(episodeId, cancellationToken);
    }
}
