namespace Betsi.API.Problems;

using Betsi.ControlPlane;
using Betsi.Domain;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using Betsi.Security;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Translates exceptions into RFC 9457 problem responses with stable codes (MVP-068).
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
        var problem = Map(exception, _environment.IsDevelopment());

        if (problem.Status >= StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception while handling {Path}", httpContext.Request.Path);
        else
            _logger.LogInformation("Request to {Path} rejected: {Code}", httpContext.Request.Path, problem.Extensions["code"]);

        await ProblemCodes.WriteAsync(httpContext, problem);
        return true;
    }

    /// <summary>The problem an exception becomes. Shared with the command envelope endpoint.</summary>
    public static ProblemDetails Map(Exception exception, bool isDevelopment) => exception switch
    {
        ValidationException validation => Validation(validation),

        AggregateNotFoundException notFound => ProblemCodes.Create(
            StatusCodes.Status404NotFound, ProblemCodes.NotFound, "Resource not found", notFound.Message),

        UnknownTenantException => ProblemCodes.Create(
            StatusCodes.Status404NotFound, ProblemCodes.UnknownTenant, "Unknown tenant",
            "The requested tenant is not served by this instance."),

        TenantUnavailableException => ProblemCodes.Create(
            StatusCodes.Status503ServiceUnavailable, ProblemCodes.TenantUnavailable, "Tenant unavailable",
            "This tenant is temporarily unavailable. Try again shortly."),

        PermissionDeniedException denied => WithExtension(ProblemCodes.Create(
                StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Not permitted",
                $"Your role does not permit this operation."),
            "permission", denied.Permission),

        LicenseRestrictedException restricted => LicenseRestricted(restricted),

        AggregateConcurrencyException conflict => Conflict(conflict),

        ConflictException conflict => ProblemCodes.Create(
            StatusCodes.Status409Conflict, ProblemCodes.Conflict, "Conflicting change", conflict.Message),

        DbUpdateConcurrencyException => ProblemCodes.Create(
            StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, "Concurrent modification",
            "The record changed while this request was in flight. Re-read it and retry."),

        // The request was well-formed and the caller is not racing anyone; the aggregate is
        // simply not in a state where this operation is meaningful.
        DomainRuleViolationException domainRule => ProblemCodes.Create(
            StatusCodes.Status422UnprocessableEntity, ProblemCodes.InvalidStateTransition,
            "Operation not valid in the current state", domainRule.Message),

        TenantNotResolvedException => ProblemCodes.Create(
            StatusCodes.Status400BadRequest, ProblemCodes.TenantNotSpecified, "Tenant not specified",
            "The request did not carry a resolvable tenant."),

        TenantIsolationViolationException => ProblemCodes.Create(
            StatusCodes.Status500InternalServerError, ProblemCodes.TenantIsolation, "Tenant isolation violation",
            "The request was refused to protect tenant isolation."),

        BadHttpRequestException badRequest => ProblemCodes.Create(
            badRequest.StatusCode, ProblemCodes.BadRequest, "Bad request", badRequest.Message),

        _ => ProblemCodes.Create(
            StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "An unexpected error occurred",
            // Exception text can contain patient data or credentials, so it is surfaced only
            // outside production, where the responder is the developer who caused it.
            isDevelopment ? exception.ToString() : null)
    };

    private static ProblemDetails Validation(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return WithExtension(
            ProblemCodes.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationError,
                "The request failed validation", "One or more fields are invalid. See 'errors' for details."),
            "errors", errors);
    }

    private static ProblemDetails LicenseRestricted(LicenseRestrictedException exception)
    {
        var problem = ProblemCodes.Create(
            StatusCodes.Status403Forbidden, ProblemCodes.LicenseRestricted, "Not permitted by the current licence",
            "This operation is not available under the tenant's current licence. Patient care and escalation remain available.");

        problem.Extensions["feature"] = exception.Feature;
        problem.Extensions["licenseStatus"] = exception.Evaluation.Status.ToString();
        return problem;
    }

    private static ProblemDetails Conflict(AggregateConcurrencyException exception)
    {
        var problem = ProblemCodes.Create(
            StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, "Concurrent modification", exception.Message);

        // The caller needs the current version to retry without another round trip.
        problem.Extensions["expectedVersion"] = exception.ExpectedVersion;
        problem.Extensions["actualVersion"] = exception.ActualVersion;
        return problem;
    }

    private static ProblemDetails WithExtension(ProblemDetails problem, string key, object value)
    {
        problem.Extensions[key] = value;
        return problem;
    }
}
