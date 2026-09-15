namespace Betsi.Tests.Infrastructure;

using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A throwaway relational database for one test, scoped to one tenant.
/// </summary>
/// <remarks>
/// SQLite in-memory rather than the EF in-memory provider: the behaviour under test here is
/// relational — concurrency tokens, transactions, value converters, unique indexes — none of
/// which the in-memory provider implements. The connection is held open because an in-memory
/// SQLite database is discarded when its last connection closes.
/// </remarks>
public sealed class TenantDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<BetsiDbContext> _options;

    public TenantDatabase(Guid tenantId, Guid actorId, string actorRole = "Nurse")
    {
        TenantId = tenantId;
        ActorId = actorId;
        ActorRole = actorRole;

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public Guid TenantId { get; }
    public Guid ActorId { get; }
    public string ActorRole { get; }

    public ITenantContext TenantContext => BuildTenantContext();

    /// <summary>
    /// A fresh context over the same database, standing in for a new request scope. Use one
    /// per logical unit of work so that tests do not accidentally pass through the change
    /// tracker instead of the database.
    /// </summary>
    public BetsiDbContext NewContext() => new(_options, BuildTenantContext());

    public AggregateRepository<T> NewRepository<T>(BetsiDbContext context)
        where T : Betsi.Domain.AggregateRoot =>
        new(context, BuildTenantContext());

    private TenantContext BuildTenantContext()
    {
        var tenantContext = new TenantContext();
        tenantContext.Resolve(TenantId, ActorId, ActorRole);
        return tenantContext;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
