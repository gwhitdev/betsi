namespace Betsi.Infrastructure;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Outbox;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Dependency injection configuration for infrastructure components.
/// </summary>
public static class InfrastructureModule
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton(TimeProvider.System);

        // Recording metrics is infrastructure and every component here may do it; exporting
        // them is AddBetsiObservability's job. Registered here so a container built without the
        // observability module — the DI smoke test, a test host — still constructs.
        services.AddMetrics();
        services.TryAddSingleton<Betsi.Infrastructure.Observability.BetsiMetrics>();

        AddControlPlane(services, configuration, environment);

        // The concrete TenantContext is registered as well as the interface so that
        // TenantResolutionMiddleware can call Resolve() on the same scoped instance that
        // everything downstream reads through ITenantContext.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // Database-per-tenant: the connection string is chosen per request from the resolved
        // tenant, so the context cannot be pooled or configured once at startup.
        services.AddDbContext<BetsiDbContext>((provider, options) =>
        {
            var tenantContext = provider.GetRequiredService<ITenantContext>();
            var registry = provider.GetRequiredService<ITenantRegistry>();

            options.UseSqlServer(
                registry.ResolveConnectionString(tenantContext.TenantId),
                sqlOptions =>
                {
                    sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                    sqlOptions.EnableRetryOnFailure(maxRetryCount: 3);
                });
        });

        // One open-generic registration covers every aggregate type. Closed registrations
        // are not needed and, if written as a factory resolving their own service type,
        // recurse forever.
        services.AddScoped(typeof(IAggregateRepository<>), typeof(AggregateRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var outboxOptions = new OutboxOptions();
        configuration.GetSection(OutboxOptions.SectionName).Bind(outboxOptions);
        services.AddSingleton(outboxOptions);
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        // The default publisher only logs; the integrations module replaces it with one that also
        // fans events out to webhook subscribers.
        services.TryAddScoped<IOutboxPublisher, LoggingOutboxPublisher>();
        services.AddHostedService<OutboxBackgroundService>();

        return services;
    }

    private static T Bind<T>(IServiceProvider services, string section) where T : new()
    {
        var options = new T();
        services.GetRequiredService<IConfiguration>().GetSection(section).Bind(options);
        return options;
    }

    private static void AddControlPlane(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Bound when first resolved, not here: configuration added after service registration
        // (by a test host, or a later configuration provider) must still be seen.
        services.AddSingleton(sp => Bind<ControlPlaneOptions>(sp, ControlPlaneOptions.SectionName));
        services.AddSingleton(sp => Bind<TenancyOptions>(sp, TenancyOptions.SectionName));
        services.AddSingleton(sp => Bind<LicensingOptions>(sp, LicensingOptions.SectionName));
        services.AddSingleton(sp => new DatabaseServerCatalog(sp.GetRequiredService<TenancyOptions>()));
        services.AddSingleton(sp => new LicenseValidator(
            sp.GetRequiredService<LicensingOptions>(), environment.IsDevelopment()));

        services.AddDbContextFactory<ControlPlaneDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                .GetConnectionString(ControlPlaneOptions.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ControlPlaneOptions.ConnectionStringName} is not configured. " +
                    "The control plane holds the tenant registry; nothing can be served without it.");
            }

            options.UseSqlServer(
                connectionString,
                sql =>
                {
                    sql.MigrationsHistoryTable("__EFMigrationsHistory", "control");
                    sql.EnableRetryOnFailure(maxRetryCount: 3);
                });
        });

        services.AddSingleton<ITenantSchemaMigrator, SqlServerTenantSchemaMigrator>();
        services.AddSingleton<ITenantRegistry, TenantRegistry>();
        services.AddSingleton<ITenantOperations, TenantOperations>();
        services.AddSingleton<ITenantDataOperations, TenantDataOperations>();
        services.AddSingleton<IPlatformStartup, PlatformStartup>();
        services.AddHostedService<TenantRegistryRefreshService>();
    }
}
