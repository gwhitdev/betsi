namespace Betsi.Infrastructure.Tenancy;

/// <summary>
/// Ambient tenant and actor for the current unit of work.
/// </summary>
/// <remarks>
/// Every read and write in the system is scoped by this. It is deliberately fail-closed:
/// an unresolved context throws rather than returning <see cref="Guid.Empty"/>, because a
/// silently-empty tenant id in a clinical system means reading or writing another
/// organisation's patient records.
/// </remarks>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid ActorId { get; }

    /// <summary>The role the actor is acting in for this request. Recorded on every event and audit row.</summary>
    string ActorRole { get; }

    /// <summary>
    /// Whether this scope is the system itself — a background job, migration or health probe —
    /// rather than a person or an integration. Never true for an HTTP request.
    /// </summary>
    bool IsSystem { get; }

    /// <summary>
    /// Whether a tenant has been established for this scope. Check this before accessing
    /// the other members if a missing tenant is an expected condition.
    /// </summary>
    bool IsResolved { get; }
}

/// <inheritdoc cref="ITenantContext"/>
public sealed class TenantContext : ITenantContext
{
    /// <summary>
    /// Roles that only the platform may act in. A token or header claiming one is refused, so
    /// that no caller can borrow the monitor's authority or an integration's.
    /// </summary>
    public const string SystemRole = "System";

    private Guid _tenantId;
    private Guid _actorId;
    private string _actorRole = string.Empty;
    private bool _isSystem;

    public bool IsResolved { get; private set; }

    public Guid TenantId => IsResolved ? _tenantId : throw new TenantNotResolvedException();

    public Guid ActorId => IsResolved ? _actorId : throw new TenantNotResolvedException();

    public string ActorRole => IsResolved ? _actorRole : throw new TenantNotResolvedException();

    public bool IsSystem => IsResolved ? _isSystem : throw new TenantNotResolvedException();

    /// <summary>
    /// Establishes the tenant and a person or integration acting in it. Called once per request
    /// by <see cref="TenantResolutionMiddleware"/>.
    /// </summary>
    public void Resolve(Guid tenantId, Guid actorId, string actorRole)
    {
        if (string.Equals(actorRole, SystemRole, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The '{SystemRole}' role is reserved for the platform's own background work.", nameof(actorRole));
        }

        Set(tenantId, actorId, string.IsNullOrWhiteSpace(actorRole) ? "Unknown" : actorRole, isSystem: false);
    }

    /// <summary>
    /// Establishes the tenant for the platform's own work in that tenant. Not reachable from a request.
    /// </summary>
    public void ResolveSystem(Guid tenantId) => Set(tenantId, Guid.Empty, SystemRole, isSystem: true);

    private void Set(Guid tenantId, Guid actorId, string actorRole, bool isSystem)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));

        if (IsResolved)
        {
            throw new InvalidOperationException(
                "The tenant context has already been resolved for this scope. Re-resolving " +
                "would allow one request to cross a tenant boundary mid-flight.");
        }

        _tenantId = tenantId;
        _actorId = actorId;
        _actorRole = actorRole;
        _isSystem = isSystem;
        IsResolved = true;
    }
}

/// <summary>
/// Thrown when tenant-scoped state is accessed outside a resolved tenant scope.
/// </summary>
public sealed class TenantNotResolvedException : InvalidOperationException
{
    public TenantNotResolvedException()
        : base("No tenant has been resolved for the current scope. Requests must carry a " +
               "resolvable tenant before touching tenant-scoped state.")
    {
    }
}
