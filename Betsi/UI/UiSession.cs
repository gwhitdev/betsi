namespace Betsi.UI;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

/// <summary>Who is using this browser session, and for which tenant.</summary>
/// <param name="TenantId">The tenant from the verified claim.</param>
/// <param name="TenantName">For display. Staff work in one department and should see which.</param>
/// <param name="ActorId">Stable id for this subject, as the API derives it.</param>
/// <param name="ActingRole">The single role this session acts in.</param>
/// <param name="DisplayName">The person's name, for the header. Falls back to the subject.</param>
public sealed record UiIdentity(
    Guid TenantId, string TenantName, Guid ActorId, string ActingRole, string DisplayName)
{
    public bool Can(string permission) => RoleMatrix.Grants(ActingRole, permission);
}

/// <summary>
/// The signed-in user of one browser session.
/// </summary>
/// <remarks>
/// <para>
/// The API resolves tenant and actor in middleware, per request. A Blazor circuit is not a
/// request: it outlives the one that opened it, and its components run in the circuit's
/// dependency-injection scope. So the same claims are read once, on first use, and held for the
/// life of the connection.
/// </para>
/// <para>
/// The claims are the same verified claims the API uses — tenant, subject, roles, acting role —
/// so the UI cannot see anything an API caller with that token could not. Nothing here reads a
/// header or a query string.
/// </para>
/// </remarks>
public sealed class UiSession
{
    private readonly UiIdentityResolver _resolver;
    private readonly AuthenticationStateProvider _authentication;
    private bool _attempted;

    public UiSession(UiIdentityResolver resolver, AuthenticationStateProvider authentication)
    {
        _resolver = resolver;
        _authentication = authentication;
    }

    private UiIdentity? _identity;

    /// <summary>The signed-in user. Call <see cref="EnsureAsync"/> first.</summary>
    public UiIdentity Identity =>
        _identity ?? throw new InvalidOperationException(
            "This session has no resolved identity. A component must await EnsureAsync() before " +
            "reading Identity, and must render UnavailableReason when it is not resolved.");

    public bool IsResolved => _identity is not null;

    /// <summary>Why this session cannot be used, when it could not be resolved. Shown to the user.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Resolves the session once, from the authenticated user.
    /// </summary>
    /// <remarks>
    /// Called from components rather than only when a circuit opens, because a page is rendered
    /// twice: once prerendered inside the HTTP request that asked for it, and again in the
    /// circuit that follows. Resolving in both means the prerendered HTML is the real page — a
    /// board a reader can see before any JavaScript runs — rather than an error or a blank.
    /// </remarks>
    public async Task EnsureAsync()
    {
        if (_attempted)
            return;

        _attempted = true;

        var state = await _authentication.GetAuthenticationStateAsync();
        var outcome = _resolver.Resolve(state.User);

        _identity = outcome.Identity;
        UnavailableReason = outcome.Reason;
    }
}

/// <summary>Either a resolved identity or the reason there is not one.</summary>
public readonly record struct UiIdentityOutcome(UiIdentity? Identity, string? Reason);

/// <summary>
/// Turns an authenticated user into the tenant and role they may act in.
/// </summary>
/// <remarks>
/// The same rules as <see cref="TenantResolutionMiddleware"/>, and for the same reasons: a
/// tenant claim is required, a tenant this instance does not serve is refused, a suspended one
/// is refused, and a session carrying several roles must have selected one. A session that
/// cannot be resolved renders an explanation rather than an empty board, because a board that
/// is empty because of a configuration error looks exactly like a department with nobody waiting.
/// </remarks>
public sealed class UiIdentityResolver
{
    private readonly ITenantRegistry _registry;
    private readonly BetsiClaimOptions _claims;
    private readonly ILogger<UiIdentityResolver> _logger;

    public UiIdentityResolver(
        ITenantRegistry registry, BetsiClaimOptions claims, ILogger<UiIdentityResolver> logger)
    {
        _registry = registry;
        _claims = claims;
        _logger = logger;
    }

    public UiIdentityOutcome Resolve(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return new(null, "You are not signed in.");

        if (!Guid.TryParse(user.FindFirst(_claims.Tenant)?.Value, out var tenantId))
        {
            _logger.LogWarning("A session was opened for a user whose credentials carry no tenant claim");
            return new(null,
                "Your account is not associated with a department. Ask your system administrator to " +
                "add the tenant claim to your identity provider account.");
        }

        if (!_registry.TryGet(tenantId, out var tenant))
        {
            _logger.LogWarning("A session was opened for unregistered tenant {TenantId}", tenantId);
            return new(null, "Your department is not served by this system.");
        }

        if (tenant.Availability != TenantAvailability.Available)
        {
            _logger.LogWarning(
                "A session was opened for tenant {TenantId}, which is {Availability}", tenantId, tenant.Availability);

            return new(null, tenant.Availability == TenantAvailability.Suspended
                ? "Your department has been suspended. Contact your system administrator."
                : "Your department is temporarily unavailable. Try again shortly.");
        }

        if (!TryResolveActingRole(user, out var actingRole))
        {
            return new(null,
                "Your account holds several roles and none is selected. Sign in again choosing the " +
                "role you are working in.");
        }

        var actorId = AuthenticationSetup.ActorIdFor(
            user.FindFirst(_claims.Subject)?.Value, user.FindFirst("iss")?.Value);

        var displayName = user.FindFirst("name")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? user.FindFirst(_claims.Subject)?.Value
            ?? "Unknown";

        return new(new UiIdentity(tenantId, tenant.Name, actorId, actingRole, displayName), null);
    }

    private bool TryResolveActingRole(ClaimsPrincipal user, out string actingRole)
    {
        var roles = user.FindAll(_claims.Role).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var selected = user.FindFirst(_claims.ActingRole)?.Value;

        if (!string.IsNullOrWhiteSpace(selected))
        {
            actingRole = roles.FirstOrDefault(r => string.Equals(r, selected, StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
            return actingRole.Length > 0;
        }

        // One role needs no choosing. No role at all is allowed through as "Unknown": the
        // permission checks then refuse each action and say so, which tells the user more than
        // a blank page would.
        switch (roles.Count)
        {
            case 1:
                actingRole = roles[0];
                return true;
            case 0:
                actingRole = "Unknown";
                return true;
            default:
                actingRole = string.Empty;
                return false;
        }
    }
}

/// <summary>Runs work in a scope that is resolved to this session's tenant and actor.</summary>
/// <remarks>
/// <para>
/// Every read and every command the UI performs goes through here. A Blazor circuit's own scope
/// lives as long as the browser tab, and a <c>DbContext</c> that lives that long accumulates
/// tracked entities and holds a connection open for a screen nobody is looking at. So each
/// operation gets its own scope, exactly as the escalation monitor's background loop does.
/// </para>
/// <para>
/// Resolving the tenant inside the scope, rather than trusting a component to pass an id, is
/// what stops a UI bug becoming a cross-tenant data leak: a page cannot ask for another
/// department's data, because it never names one.
/// </para>
/// </remarks>
public sealed class UiScopeRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly UiSession _session;

    public UiScopeRunner(IServiceScopeFactory scopes, UiSession session)
    {
        _scopes = scopes;
        _session = session;
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        var identity = _session.Identity;

        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(identity.TenantId, identity.ActorId, identity.ActingRole);

        return await work(scope.ServiceProvider);
    }

    public async Task RunAsync(Func<IServiceProvider, Task> work) =>
        await RunAsync<object?>(async services =>
        {
            await work(services);
            return null;
        });

    /// <summary>Runs a patient-data read and records it in the tenant audit trail.</summary>
    public async Task<T> RunReadAsync<T>(
        string resource, Guid affectedId, Func<IServiceProvider, Task<T>> work)
    {
        var identity = _session.Identity;

        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(identity.TenantId, identity.ActorId, identity.ActingRole);

        var result = await work(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<BetsiDbContext>();
        db.AuditLogs.Add(new AuditLogRecord
        {
            TenantId = identity.TenantId,
            Action = $"Read:{resource}",
            ActorId = identity.ActorId,
            ActorRole = identity.ActingRole,
            AffectedAggregateId = affectedId,
            AffectedAggregateType = resource,
            Outcome = "Read",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return result;
    }
}
