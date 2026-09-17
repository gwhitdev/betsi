namespace Betsi.Infrastructure.Persistence;

using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Builds a context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Migrations describe the shape of the schema, which is identical for every tenant, so the
/// connection string here is a placeholder and is never connected to. The tenant context is
/// resolved to a fixed design-time id purely to satisfy the constructor.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BetsiDbContext>
{
    public BetsiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=betsi;Trusted_Connection=True;",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options;

        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        return new BetsiDbContext(options, tenantContext);
    }
}
