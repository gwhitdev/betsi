namespace Betsi.ControlPlane;

using Microsoft.Data.SqlClient;

public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";
    public const string ConnectionStringName = "ControlPlane";

    /// <summary>Apply control-plane migrations at startup. Development convenience.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>
    /// How often each instance reloads the registry. This bounds how long a suspension or
    /// licence change takes to reach every running instance.
    /// </summary>
    public TimeSpan RegistryRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>
    /// Migrate every tenant database at startup. Development convenience; in production,
    /// migrations run as a deployment step via <c>tenants migrate</c>.
    /// </summary>
    public bool MigrateTenantsOnStartup { get; set; }

    /// <summary>
    /// Server profiles: a name mapped to a connection string with no database. Tenant records
    /// name a profile rather than holding credentials themselves.
    /// </summary>
    public Dictionary<string, string> DatabaseServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tenants to ensure exist at startup. Honoured in Development only.</summary>
    public List<SeedTenant> SeedTenants { get; set; } = [];
}

public sealed class SeedTenant
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DatabaseServer { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Path to a licence key file, relative to the content root.</summary>
    public string? LicenseFile { get; set; }
}

/// <summary>Turns a tenant record's server profile and database name into a connection.</summary>
public sealed class DatabaseServerCatalog
{
    private readonly IReadOnlyDictionary<string, string> _servers;

    public DatabaseServerCatalog(TenancyOptions options)
    {
        _servers = new Dictionary<string, string>(options.DatabaseServers, StringComparer.OrdinalIgnoreCase);
    }

    public bool Contains(string server) => _servers.ContainsKey(server);

    public string ConnectionStringFor(string server, string databaseName)
    {
        if (!_servers.TryGetValue(server, out var serverConnection))
        {
            throw new InvalidOperationException(
                $"Database server profile '{server}' is not configured under Tenancy:DatabaseServers.");
        }

        // The builder, not string concatenation, so a database name cannot inject other
        // connection-string keywords.
        return new SqlConnectionStringBuilder(serverConnection) { InitialCatalog = databaseName }
            .ConnectionString;
    }
}
