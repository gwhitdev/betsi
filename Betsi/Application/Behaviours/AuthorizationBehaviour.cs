namespace Betsi.Application.Behaviours;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using MediatR;
using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Enforces the permission each command requires, at the application boundary (spec §6).
/// </summary>
/// <remarks>
/// Endpoints are authorised too, but commands are also reachable from the command envelope
/// endpoint and inbound integrations. Checking here means no route to a command can skip the
/// check. System-only commands are refused for every person and integration, and system scopes
/// cannot send anything else.
/// </remarks>
public sealed class AuthorizationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly ConcurrentDictionary<Type, CommandAuthorization> Requirements = new();

    private readonly ITenantContext _tenantContext;

    public AuthorizationBehaviour(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommand)
            return next();

        var requirement = Requirements.GetOrAdd(typeof(TRequest), RequirementOf);

        if (requirement.SystemOnly)
        {
            return _tenantContext.IsSystem
                ? next()
                : throw new PermissionDeniedException("system", _tenantContext.ActorRole);
        }

        if (_tenantContext.IsSystem || !RoleMatrix.Grants(_tenantContext.ActorRole, requirement.Permission!))
            throw new PermissionDeniedException(requirement.Permission!, _tenantContext.ActorRole);

        return next();
    }

    internal static CommandAuthorization RequirementOf(Type commandType)
    {
        var permission = commandType.GetCustomAttribute<RequiresPermissionAttribute>();
        var systemOnly = commandType.GetCustomAttribute<SystemOnlyAttribute>() is not null;

        return (permission, systemOnly) switch
        {
            ({ } p, false) => new CommandAuthorization(p.Permission, false),
            (null, true) => new CommandAuthorization(null, true),
            _ => throw new InvalidOperationException(
                $"{commandType.Name} must carry exactly one of [RequiresPermission] or [SystemOnly].")
        };
    }
}

internal sealed record CommandAuthorization(string? Permission, bool SystemOnly);
