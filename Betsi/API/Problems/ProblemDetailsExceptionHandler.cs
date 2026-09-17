namespace Betsi.API.Problems;

using Betsi.ControlPlane;
using Betsi.Domain;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Translates exceptions into RFC 9457 problem responses (MVP-065).
/// </summary>
/// <remarks>
/// Handlers signal failure by throwing, so this is the single place that decides what a
/// failure looks like on the wire. Keeping the mapping here rather than in each controller
/// is what stops an unmapped failure from becoming a 200 with an error body.
///
/// Unrecognised exceptions become a bare 500 with no detail: an exception message can carry
/// patient data or connection strings, and neither belongs in an HTTP response.
/// </remarks>
public sealed class ProblemDetailsExceptionHandler : IExceptionHandler
{
    private const string ProblemTypeBase = "https://betsi.nhs.uk/problems/";

    private readonly ILogger<ProblemDetailsExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public ProblemDetailsExceptionHandler(
        ILogger<ProblemDetailsExceptionHandler> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = Map(exception);

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        if (problem.Status >= StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception while handling {Path}", httpContext.Request.Path);
        else
            _logger.LogInformation("Request to {Path} rejected: {Title}", httpContext.Request.Path, problem.Title);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken);

        return true;
    }

    private ProblemDetails Map(Exception exception) => exception switch
    {
        ValidationException validation => Validation(validation),

        AggregateNotFoundException notFound => new ProblemDetails
        {
            Type = ProblemTypeBase + "not-found",
            Title = "Resource not found",
            Status = StatusCodes.Status404NotFound,
            Detail = notFound.Message
        },

        UnknownTenantException => new ProblemDetails
        {
            Type = ProblemTypeBase + "unknown-tenant",
            Title = "Unknown tenant",
            Status = StatusCodes.Status404NotFound,
            Detail = "The requested tenant is not served by this instance."
        },

        TenantUnavailableException => new ProblemDetails
        {
            Type = ProblemTypeBase + "tenant-unavailable",
            Title = "Tenant unavailable",
            Status = StatusCodes.Status503ServiceUnavailable,
            Detail = "This tenant is temporarily unavailable. Try again shortly."
        },

        LicenseRestrictedException restricted => LicenseRestricted(restricted),

        AggregateConcurrencyException conflict => Conflict(conflict),

        ConflictException conflict => new ProblemDetails
        {
            Type = ProblemTypeBase + "conflict",
            Title = "Conflicting change",
            Status = StatusCodes.Status409Conflict,
            Detail = conflict.Message
        },

        DbUpdateConcurrencyException => new ProblemDetails
        {
            Type = ProblemTypeBase + "concurrency-conflict",
            Title = "Concurrent modification",
            Status = StatusCodes.Status409Conflict,
            Detail = "The record changed while this request was in flight. Re-read it and retry."
        },

        // The request was well-formed and the caller is not racing anyone; the aggregate is
        // simply not in a state where this operation is meaningful.
        DomainRuleViolationException domainRule => new ProblemDetails
        {
            Type = ProblemTypeBase + "invalid-state-transition",
            Title = "Operation not valid in the current state",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = domainRule.Message
        },

        TenantNotResolvedException => new ProblemDetails
        {
            Type = ProblemTypeBase + "tenant-not-resolved",
            Title = "Tenant not specified",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request did not carry a resolvable tenant."
        },

        TenantIsolationViolationException => new ProblemDetails
        {
            Type = ProblemTypeBase + "tenant-isolation",
            Title = "Tenant isolation violation",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The request was refused to protect tenant isolation."
        },

        _ => new ProblemDetails
        {
            Type = ProblemTypeBase + "internal-error",
            Title = "An unexpected error occurred",
            Status = StatusCodes.Status500InternalServerError,
            // Exception text can contain patient data or credentials, so it is surfaced only
            // outside production, where the responder is the developer who caused it.
            Detail = _environment.IsDevelopment() ? exception.ToString() : null
        }
    };

    private static ProblemDetails Validation(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        var problem = new ProblemDetails
        {
            Type = ProblemTypeBase + "validation",
            Title = "The request failed validation",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = "One or more fields are invalid. See 'errors' for details."
        };

        problem.Extensions["errors"] = errors;
        return problem;
    }

    private static ProblemDetails LicenseRestricted(LicenseRestrictedException exception)
    {
        var problem = new ProblemDetails
        {
            Type = ProblemTypeBase + "license-restricted",
            Title = "Not permitted by the current licence",
            Status = StatusCodes.Status403Forbidden,
            Detail = "This operation is not available under the tenant's current licence. " +
                     "Patient care and escalation remain available."
        };

        problem.Extensions["feature"] = exception.Feature;
        problem.Extensions["licenseStatus"] = exception.Evaluation.Status.ToString();
        return problem;
    }

    private static ProblemDetails Conflict(AggregateConcurrencyException exception)
    {
        var problem = new ProblemDetails
        {
            Type = ProblemTypeBase + "concurrency-conflict",
            Title = "Concurrent modification",
            Status = StatusCodes.Status409Conflict,
            Detail = exception.Message
        };

        // The caller needs the current version to retry without another round trip.
        problem.Extensions["expectedVersion"] = exception.ExpectedVersion;
        problem.Extensions["actualVersion"] = exception.ActualVersion;
        return problem;
    }
}
