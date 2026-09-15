namespace Betsi.Tests.Infrastructure;

using Betsi.Application.Commands;
using Betsi.ControlPlane;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

/// <summary>
/// Verifies the container can actually build what it advertises.
/// </summary>
/// <remarks>
/// The original infrastructure module compiled but could not resolve a single repository:
/// the generic repository took an unconstructible <c>Guid</c> parameter, four closed
/// registrations resolved themselves recursively, and <c>ITenantContext</c> was never
/// registered at all. None of that is visible to the compiler, so it is asserted here.
/// </remarks>
public class DependencyInjectionTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = "Server=(test);Database=betsi_control;",
                ["Tenancy:DatabaseServers:default"] = "Server=(test);"
            })
            .Build();

        var environment = new HostingEnvironment { EnvironmentName = Environments.Development, ContentRootPath = AppContext.BaseDirectory };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration, environment);

        // The real registry is empty until it loads from the control plane, which these tests
        // do not have. A registry that knows one tenant lets the tenant-scoped graph resolve.
        services.RemoveAll<ITenantRegistry>();
        services.AddSingleton<ITenantRegistry>(new SingleTenantRegistry(TenantId));

        // ValidateOnBuild surfaces unconstructible registrations at build time rather than
        // on the first request that happens to need one.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static IServiceScope BuildResolvedScope(ServiceProvider provider)
    {
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(TenantId, Guid.NewGuid(), "Nurse");
        return scope;
    }

    [Fact]
    public void The_infrastructure_container_builds()
    {
        Should.NotThrow(BuildProvider);
    }

    [Fact]
    public void The_tenant_context_is_registered()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<ITenantContext>().ShouldNotBeNull();
    }

    [Fact]
    public void The_interface_and_concrete_tenant_context_are_the_same_instance()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var abstraction = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // Middleware resolves the tenant on the concrete type; everything else reads the
        // interface. If these were different instances every read would see an unresolved
        // context.
        abstraction.ShouldBeSameAs(concrete);
    }

    [Theory]
    [InlineData(typeof(IAggregateRepository<PatientEpisode>))]
    [InlineData(typeof(IAggregateRepository<Location>))]
    [InlineData(typeof(IAggregateRepository<Queue>))]
    [InlineData(typeof(IAggregateRepository<Escalation>))]
    public void Every_aggregate_repository_resolves(Type repositoryType)
    {
        using var provider = BuildProvider();
        using var scope = BuildResolvedScope(provider);

        scope.ServiceProvider.GetService(repositoryType).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(typeof(IPlatformStartup))]
    [InlineData(typeof(ITenantOperations))]
    [InlineData(typeof(Betsi.Licensing.LicenseValidator))]
    public void Control_plane_services_resolve(Type serviceType)
    {
        using var provider = BuildProvider();

        provider.GetService(serviceType).ShouldNotBeNull();
    }

    [Fact]
    public void The_real_tenant_registry_resolves()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = "Server=(test);Database=betsi_control;"
            })
            .Build();

        var environment = new HostingEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration, environment);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        provider.GetRequiredService<ITenantRegistry>().ShouldBeOfType<TenantRegistry>();
    }

    [Fact]
    public void Every_command_has_exactly_one_handler()
    {
        var applicationAssembly = typeof(RegisterPatientCommand).Assembly;

        var commandTypes = applicationAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => t.GetInterfaces().Contains(typeof(ICommand)))
            .ToArray();

        commandTypes.ShouldNotBeEmpty();

        var missing = new List<string>();

        foreach (var commandType in commandTypes)
        {
            var handlerInterface =
                typeof(IRequestHandler<,>).MakeGenericType(commandType, typeof(CommandResult));

            var handlers = applicationAssembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false })
                .Where(t => handlerInterface.IsAssignableFrom(t))
                .ToArray();

            if (handlers.Length != 1)
                missing.Add($"{commandType.Name} has {handlers.Length} handlers");
        }

        // Six of the original eight endpoints dispatched commands that had no handler and
        // failed only when a real request arrived.
        missing.ShouldBeEmpty();
    }
}

internal sealed class SingleTenantRegistry : ITenantRegistry
{
    private readonly TenantDescriptor _tenant;

    public SingleTenantRegistry(Guid tenantId)
    {
        _tenant = new TenantDescriptor
        {
            TenantId = tenantId,
            Name = "Test Tenant",
            State = TenantState.Active,
            Availability = TenantAvailability.Available,
            DatabaseServer = "default",
            DatabaseName = "betsi_test",
            ConnectionString = "Server=(test);Database=betsi_test;",
            License = new Betsi.Licensing.LicenseValidator(new(), isDevelopment: false)
                .Validate(null, tenantId, DateTimeOffset.UtcNow)
        };
    }

    public IReadOnlyCollection<TenantDescriptor> All => [_tenant];
    public IReadOnlyCollection<TenantDescriptor> Available => [_tenant];
    public DateTimeOffset? LastRefreshedAt => DateTimeOffset.UtcNow;

    public bool TryGet(Guid tenantId, out TenantDescriptor tenant)
    {
        tenant = _tenant;
        return tenantId == _tenant.TenantId;
    }

    public string ResolveConnectionString(Guid tenantId) =>
        tenantId == _tenant.TenantId ? _tenant.ConnectionString! : throw new UnknownTenantException(tenantId);

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
