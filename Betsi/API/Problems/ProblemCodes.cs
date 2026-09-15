namespace Betsi.API.Problems;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Stable, documented error codes (MVP-068). Every problem response carries one in its
/// <c>code</c> extension. Clients branch on the code, never on the title or detail text.
/// </summary>
/// <remarks>Codes are part of the v1 contract: add new ones freely, never rename or reuse one.</remarks>
public static class ProblemCodes
{
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string ActingRoleRequired = "ACTING_ROLE_REQUIRED";
    public const string ReservedRole = "RESERVED_ROLE";
    public const string TenantNotSpecified = "TENANT_NOT_SPECIFIED";
    public const string TenantMismatch = "TENANT_MISMATCH";
    public const string UnknownTenant = "UNKNOWN_TENANT";
    public const string TenantSuspended = "TENANT_SUSPENDED";
    public const string TenantUnavailable = "TENANT_UNAVAILABLE";
    public const string TenantIsolation = "TENANT_ISOLATION";
    public const string LicenseRestricted = "LICENSE_RESTRICTED";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string Conflict = "CONFLICT";
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string UnknownCommandType = "UNKNOWN_COMMAND_TYPE";
    public const string BadRequest = "BAD_REQUEST";
    public const string InvalidSignature = "INVALID_SIGNATURE";
    public const string InternalError = "INTERNAL_ERROR";

    public const string TypeBase = "https://betsi.nhs.uk/problems/";

    public static ProblemDetails Create(int status, string code, string title, string? detail, string? instance = null)
    {
        var problem = new ProblemDetails
        {
            Type = TypeBase + code.ToLowerInvariant().Replace('_', '-'),
            Title = title,
            Status = status,
            Detail = detail,
            Instance = instance
        };

        problem.Extensions["code"] = code;
        return problem;
    }

    public static Task WriteAsync(HttpContext httpContext, ProblemDetails problem)
    {
        problem.Instance ??= httpContext.Request.Path;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        return httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
