namespace Betsi.API.Controllers;

using Betsi.API.Problems;
using Betsi.Application.Commands;
using Betsi.Infrastructure.Observability;
using Betsi.Security;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Generic command submission with an envelope and idempotency (spec §4.1, MVP-061).
/// </summary>
/// <remarks>
/// The per-resource endpoints remain the primary API. This exists for integrations that need
/// retry-safe submission, command ids and correlation ids. Authorisation is per command, in the
/// pipeline, exactly as for the resource endpoints.
/// </remarks>
[ApiController]
[Route("api/v1/commands")]
[Produces("application/json")]
public sealed class CommandsController : ControllerBase
{
    /// <summary>The header the correlation id is read from and echoed in.</summary>
    public const string CorrelationHeader = CorrelationIdMiddleware.HeaderName;

    private readonly CommandEnvelopeDispatcher _dispatcher;
    private readonly IHostEnvironment _environment;

    public CommandsController(CommandEnvelopeDispatcher dispatcher, IHostEnvironment environment)
    {
        _dispatcher = dispatcher;
        _environment = environment;
    }

    /// <summary>Submits one command. A retry with the same idempotency key returns the original result.</summary>
    [HttpPost]
    [DispatchesAuthorizedCommands]
    [ProducesResponseType<CommandEnvelopeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit([FromBody] CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        // The envelope's own id wins; otherwise the one the middleware resolved for this
        // request, which has already honoured or replaced the caller's header. Sanitised
        // either way: this value is persisted with the idempotency record and appears on every
        // log line for the command, so it cannot be allowed to carry control characters or to
        // be longer than the column that stores it.
        var correlationId = CorrelationIdMiddleware.Sanitise(envelope.CorrelationId)
            ?? HttpContext.Items[CorrelationHeader] as string;

        envelope = envelope with { CorrelationId = correlationId };

        if (correlationId is not null)
            HttpContext.Response.Headers[CorrelationHeader] = correlationId;

        try
        {
            var result = await _dispatcher.DispatchAsync(envelope, cancellationToken);
            if (result.Replayed)
                HttpContext.Response.Headers["Idempotent-Replayed"] = "true";

            return Ok(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var problem = exception is EnvelopeRejectedException rejected
                ? ProblemCodes.Create(rejected.Status, rejected.Code, "Command not accepted", rejected.Message)
                : ProblemDetailsExceptionHandler.Map(exception, _environment.IsDevelopment());

            problem.Extensions["commandId"] = envelope.CommandId;
            if (correlationId is not null)
                problem.Extensions["correlationId"] = correlationId;

            return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        }
    }
}
