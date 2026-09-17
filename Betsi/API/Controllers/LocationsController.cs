namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using MediatR;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Locations and the queues attached to them.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public sealed class LocationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public LocationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Creates a location such as a bay, cubicle or waiting area.</summary>
    [HttpPost("locations")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateLocation(
        [FromBody] CreateLocationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);

        return CreatedAtAction(nameof(CreateLocation), new { locationId = result.AggregateId }, result);
    }

    /// <summary>Creates a queue for a location.</summary>
    [HttpPost("queues")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateQueue(
        [FromBody] CreateQueueCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);

        return CreatedAtAction(nameof(CreateQueue), new { queueId = result.AggregateId }, result);
    }

    /// <summary>Adds a patient to the end of a queue.</summary>
    [HttpPost("queues/{queueId:guid}/patients")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> EnqueuePatient(
        Guid queueId,
        [FromBody] EnqueuePatientCommand command,
        CancellationToken cancellationToken)
    {
        command.QueueId = queueId;
        return _mediator.Send(command, cancellationToken);
    }
}
