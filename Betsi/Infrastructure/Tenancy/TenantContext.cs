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
    string ActorRole { get; }

    /// <summary>
    /// Whether a tenant has been established for this scope. Check this before accessing
    /// the other members if a missing tenant is an expected condition.
    /// </summary>
    bool IsResolved { get; }
}

/// <inheritdoc cref="ITenantContext"/>
public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;
    private Guid _actorId;
    private string _actorRole = string.Empty;

    public bool IsResolved { get; private set; }

    public Guid TenantId => IsResolved ? _tenantId : throw new TenantNotResolvedException();

    public Guid ActorId => IsResolved ? _actorId : throw new TenantNotResolvedException();

    public string ActorRole => IsResolved ? _actorRole : throw new TenantNotResolvedException();

    /// <summary>
    /// Establishes the tenant and actor for this scope. Called once per request by
    /// <see cref="TenantResolutionMiddleware"/>.
    /// </summary>
    public void Resolve(Guid tenantId, Guid actorId, string actorRole)
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
        _actorRole = string.IsNullOrWhiteSpace(actorRole) ? "Unknown" : actorRole;
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
