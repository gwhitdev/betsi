namespace Betsi.Tests.Api;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Outbox;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Tests.ControlPlane;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;
using System.Net.Http.Json;

/// <summary>
/// Runs the real application — real middleware, real pipeline behaviours, real handlers —
/// over a throwaway SQLite database per tenant.
/// </summary>
/// <remarks>
/// Two licensed tenants are configured so that isolation can be asserted rather than assumed,
/// plus one tenant in each state the edge must refuse or restrict. The control plane is a
/// SQLite database too, populated as an operator would leave it. The
/// outbox background service is removed: these tests drive the processor directly when they
/// care about it, and a timer firing mid-assertion makes failures non-deterministic.
/// </remarks>
public sealed class BetsiApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Suspended by an operator.</summary>
    public static readonly Guid SuspendedTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Active, with no licence installed: restricted mode.</summary>
    public static readonly Guid UnlicensedTenant = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>Active, but its database is on an older schema than this build.</summary>
    public static readonly Guid OutdatedSchemaTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>Licensed; reserved for escalation-engine tests, which change site-wide policy.</summary>
    public static readonly Guid EscalationTenant = Guid.Parse("66666666-6666-6666-6666-666666666666");

    /// <summary>Licensed; used only by the escalation performance tests, so their volumes are known.</summary>
    public static readonly Guid PerformanceTenant = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly Dictionary<Guid, SqliteConnection> _connections = new()
    {
        [EscalationTenant] = new SqliteConnection("DataSource=:memory:"),
        [PerformanceTenant] = new SqliteConnection("DataSource=:memory:"),
        [TenantA] = new SqliteConnection("DataSource=:memory:"),
        [TenantB] = new SqliteConnection("DataSource=:memory:"),
        [UnlicensedTenant] = new SqliteConnection("DataSource=:memory:")
    };

    private readonly SqliteConnection _controlPlane = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantResolution:AllowHeaderFallback"] = "true",
                // Never connected to: the contexts are replaced with SQLite below.
                ["ConnectionStrings:ControlPlane"] = "Server=(test);Database=betsi_control",
                ["Tenancy:DatabaseServers:test"] = "Server=(test)",
                ["Tenancy:MigrateTenantsOnStartup"] = "false",
                ["ControlPlane:MigrateOnStartup"] = "false",
                ["Authentication:Jwt:Issuer"] = TestTokens.Issuer,
                ["Authentication:Jwt:Audience"] = TestTokens.Audience,
                ["Authentication:Jwt:SigningKeys:0:KeyId"] = TestTokens.KeyId,
                ["Authentication:Jwt:SigningKeys:0:PublicKeyPem"] = TestTokens.PublicKeyPem,
                ["Licensing:TrustedKeys:0:KeyId"] = TestLicenses.KeyId,
                ["Licensing:TrustedKeys:0:PublicKeyPem"] = TestLicenses.PublicKeyPem
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // AddDbContext registers the context itself as well as its options, so both
            // must go before a SQLite-backed replacement can be put in their place.
            services.RemoveAll<BetsiDbContext>();
            services.RemoveAll<DbContextOptions<BetsiDbContext>>();
            services.RemoveAll<DbContextOptions>();

            services.AddScoped(provider =>
            {
                var tenantContext = provider.GetRequiredService<ITenantContext>();
                var connection = ConnectionFor(tenantContext.TenantId);

                var options = new DbContextOptionsBuilder<BetsiDbContext>()
                    .UseSqlite(connection)
                    .Options;

                return new BetsiDbContext(options, tenantContext);
            });

            services.AddScoped<IOutboxProcessor, OutboxProcessor>();

            // EF accumulates option configurations per context, so the SQL Server one must be
            // removed or the SQLite replacement would configure two providers.
            services.RemoveAll<IDbContextFactory<ControlPlaneDbContext>>();
            services.RemoveAll<DbContextOptions<ControlPlaneDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ControlPlaneDbContext>>();
            services.AddDbContextFactory<ControlPlaneDbContext>(options => options.UseSqlite(_controlPlane));

            // Schemas and registry records are created by InitialiseAsync. The real startup
            // runs migrations, which are SQL Server-shaped and would not apply to SQLite.
            services.RemoveAll<IPlatformStartup>();
            services.AddSingleton<IPlatformStartup, AlreadyInitialisedPlatform>();
        });
    }

    /// <summary>
    /// Creates the control plane and each tenant's schema, registers the tenants and loads the
    /// registry. Call once before the first request.
    /// </summary>
    public async Task InitialiseAsync()
    {
        await _controlPlane.OpenAsync();

        await using (var controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_controlPlane).Options))
        {
            await controlPlane.Database.EnsureCreatedAsync();

            var latest = new SqlServerTenantSchemaMigrator().LatestMigration;

            TenantRecord Tenant(Guid id, string name, TenantState state, bool licensed, string? schema = null) => new()
            {
                TenantId = id,
                Name = name,
                State = state,
                DatabaseServer = "test",
                DatabaseName = $"betsi_{name.Replace(' ', '_').ToLowerInvariant()}",
                SchemaVersion = schema ?? latest,
                LicenseKey = licensed ? TestLicenses.ValidFor(id) : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            controlPlane.Tenants.AddRange(
                Tenant(TenantA, "Tenant A", TenantState.Active, licensed: true),
                Tenant(TenantB, "Tenant B", TenantState.Active, licensed: true),
                Tenant(EscalationTenant, "Escalation", TenantState.Active, licensed: true),
                Tenant(PerformanceTenant, "Performance", TenantState.Active, licensed: true),
                Tenant(SuspendedTenant, "Suspended", TenantState.Suspended, licensed: true),
                Tenant(UnlicensedTenant, "Unlicensed", TenantState.Active, licensed: false),
                Tenant(OutdatedSchemaTenant, "Outdated", TenantState.Active, licensed: true, schema: "20200101000000_Ancient"));

            await controlPlane.SaveChangesAsync();
        }

        foreach (var (tenantId, connection) in _connections)
        {
            await connection.OpenAsync();

            var tenantContext = new TenantContext();
            tenantContext.ResolveSystem(tenantId);

            var options = new DbContextOptionsBuilder<BetsiDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new BetsiDbContext(options, tenantContext);
            await context.Database.EnsureCreatedAsync();
        }

        await Services.GetRequiredService<ITenantRegistry>().RefreshAsync(CancellationToken.None);
    }

    /// <summary>A client whose requests are attributed to the given tenant and actor.</summary>
    public HttpClient ClientFor(Guid tenantId, Guid? actorId = null, string actorRole = "Nurse")
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Add(
            TenantResolutionMiddleware.TenantHeaderName, tenantId.ToString());
        client.DefaultRequestHeaders.Add(
            TenantResolutionMiddleware.ActorHeaderName, (actorId ?? Guid.NewGuid()).ToString());
        client.DefaultRequestHeaders.Add(
            TenantResolutionMiddleware.ActorRoleHeaderName, actorRole);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>Runs one waiting-time evaluation for a tenant as the background monitor would, at a chosen time.</summary>
    public async Task<Betsi.Application.Escalations.WaitingTimeEvaluation> EvaluateWaitingTimesAsync(Guid tenantId, DateTime now)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(tenantId);

        return await scope.ServiceProvider.GetRequiredService<Betsi.Application.Escalations.IWaitingTimeMonitor>()
            .EvaluateAsync(now, CancellationToken.None);
    }

    /// <summary>A client authenticating with a bearer token only — no development headers.</summary>
    public HttpClient ClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Reads a tenant's database directly, to assert what a request actually wrote.</summary>
    public BetsiDbContext DatabaseFor(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(tenantId);

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlite(ConnectionFor(tenantId))
            .Options;

        return new BetsiDbContext(options, tenantContext);
    }

    private SqliteConnection ConnectionFor(Guid tenantId) =>
        _connections.TryGetValue(tenantId, out var connection)
            ? connection
            : throw new InvalidOperationException($"No test database for tenant {tenantId}.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();

            _controlPlane.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Stands in for platform startup in tests, where schemas and tenants are created up front.
/// </summary>
internal sealed class AlreadyInitialisedPlatform : IPlatformStartup
{
    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Shares one initialised application across every API test.
/// </summary>
/// <remarks>
/// A single host, shared through a collection rather than per class, because Serilog's
/// bootstrap logger is process-wide static state: two hosts starting concurrently race to
/// freeze it and one of them throws. Sharing also keeps the suite quick, since each host
/// start costs more than the tests it serves.
/// </remarks>
public sealed class BetsiApiFixture : IAsyncLifetime
{
    public BetsiApiFactory Factory { get; } = new();

    public async ValueTask InitializeAsync() => await Factory.InitialiseAsync();

    public ValueTask DisposeAsync()
    {
        Factory.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Helpers for reading command results and problem documents off a response.</summary>
public static class HttpResponseExtensions
{
    public static async Task<Betsi.Application.Commands.CommandResult> ReadCommandResultAsync(
        this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await response.Content
            .ReadFromJsonAsync<Betsi.Application.Commands.CommandResult>(cancellationToken);

        return result ?? throw new InvalidOperationException("The response had no command result.");
    }
}

/// <summary>
/// Groups the API tests so they share one host and do not run concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<BetsiApiFixture>
{
    public const string Name = "Betsi API";
}
