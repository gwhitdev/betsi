namespace Betsi.API.Controllers;

using Betsi.Application.Commands;
using MediatR;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Patient episode lifecycle: arrival through to discharge.
/// </summary>
/// <remarks>
/// Controllers dispatch and return. Validation, auditing and failure translation all happen
/// in the MediatR pipeline and the problem-details handler, so there is no error handling
/// here — an unsuccessful command throws and never reaches a 200.
/// </remarks>
[ApiController]
[Route("api/v1/patients")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public sealed class PatientsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PatientsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Registers a patient's arrival, opening a new episode.</summary>
    [HttpPost("register")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RegisterPatient(
        [FromBody] RegisterPatientCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);

        return CreatedAtAction(
            nameof(RegisterPatient), new { patientId = result.AggregateId }, result);
    }

    /// <summary>Begins triage for a waiting patient.</summary>
    [HttpPost("{patientId:guid}/triage/begin")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> BeginTriage(
        Guid patientId,
        [FromBody] BeginPatientTriageCommand command,
        CancellationToken cancellationToken)
    {
        // The route is authoritative, so a body that disagrees is corrected rather than
        // rejected. This removes a class of 400s caused by clients templating the body.
        command.PatientEpisodeId = patientId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Completes triage, moving the patient to awaiting treatment.</summary>
    [HttpPost("{patientId:guid}/triage/complete")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> CompleteTriage(
        Guid patientId,
        [FromBody] CompletePatientTriageCommand command,
        CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = patientId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Moves the patient into treatment at a location.</summary>
    [HttpPost("{patientId:guid}/treatment/begin")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> BeginTreatment(
        Guid patientId,
        [FromBody] BeginPatientTreatmentCommand command,
        CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = patientId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Discharges the patient, ending the episode.</summary>
    [HttpPost("{patientId:guid}/discharge")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> DischargePatient(
        Guid patientId,
        [FromBody] DischargePatientCommand command,
        CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = patientId;
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>Cancels the episode, for example a patient who left without being seen.</summary>
    [HttpPost("{patientId:guid}/cancel")]
    [ProducesResponseType<CommandResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<CommandResult> CancelEpisode(
        Guid patientId,
        [FromBody] CancelPatientEpisodeCommand command,
        CancellationToken cancellationToken)
    {
        command.PatientEpisodeId = patientId;
        return _mediator.Send(command, cancellationToken);
    }
}
