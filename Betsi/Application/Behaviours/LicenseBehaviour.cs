namespace Betsi.Application.Behaviours;

using Betsi.Application.Commands;
using Betsi.ControlPlane;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using MediatR;
using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Refuses licence-gated commands when the tenant's licence does not grant them (MVP-008).
/// </summary>
/// <remarks>
/// Commands marked <see cref="AlwaysAvailableAttribute"/> pass regardless of licence state.
/// A command with neither attribute is refused with an exception rather than silently
/// allowed or blocked, so a missing classification fails loudly in tests, not quietly in a
/// hospital.
/// </remarks>
public sealed class LicenseBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly ConcurrentDictionary<Type, string?> RequiredFeatures = new();

    private readonly ITenantRegistry _registry;
    private readonly ITenantContext _tenantContext;

    public LicenseBehaviour(ITenantRegistry registry, ITenantContext tenantContext)
    {
        _registry = registry;
        _tenantContext = tenantContext;
    }

    public Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommand)
            return next();

        var feature = RequiredFeatures.GetOrAdd(typeof(TRequest), RequiredFeatureOf);

        if (feature is null)
            return next();

        if (!_registry.TryGet(_tenantContext.TenantId, out var tenant))
            throw new UnknownTenantException(_tenantContext.TenantId);

        return tenant.License.Grants(feature)
            ? next()
            : throw new LicenseRestrictedException(feature, tenant.License);
    }

    internal static string? RequiredFeatureOf(Type requestType)
    {
        var gated = requestType.GetCustomAttribute<RequiresLicenseAttribute>();
        var always = requestType.GetCustomAttribute<AlwaysAvailableAttribute>();

        return (gated, always) switch
        {
            ({ } g, null) => g.Feature,
            (null, { }) => null,
            _ => throw new InvalidOperationException(
                $"{requestType.Name} must carry exactly one of [RequiresLicense] or [AlwaysAvailable].")
        };
    }
}
