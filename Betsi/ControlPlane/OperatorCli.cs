namespace Betsi.ControlPlane;

using Betsi.Licensing;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Operator commands for the control plane, run through the application host so they use
/// exactly the configuration the service does.
/// </summary>
/// <remarks>
/// A command line rather than an HTTP API. Provisioning and suspending tenants is control-plane
/// work, not tenant work: it crosses tenants, so it does not fit the per-tenant authorisation
/// model, and access to these commands is access to the host, which is already a privileged
/// boundary. An authenticated admin API would need its own identity and audit story; if one is
/// ever added, it belongs behind the control plane, not the tenant API.
/// </remarks>
public static class OperatorCli
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;

    public static bool IsOperatorCommand(string[] args) =>
        args.Length > 0 && args[0] is "tenants" or "license";

    private const string Usage = """
        Usage:
          tenants list
          tenants provision --name <name> --database <db> [--server <profile>] [--id <guid>] [--license-file <path>]
          tenants suspend   --id <guid> --reason <text>
          tenants resume    --id <guid> --reason <text>
          tenants migrate   [--id <guid>]
          tenants backup    --id <guid> --directory <path on the database server> [--type full|log] [--certificate <name>]
          tenants restore   --id <guid> --from <path on the database server> --database <db> [--replace] [--repoint]
          tenants export    --id <guid> --file <path>
          tenants destroy   --id <guid> --confirm <tenant name>
          license install   --id <guid> --file <path>
          license status    [--id <guid>]

        backup, restore and destroy act on the database server, so paths are as that server
        sees them. export writes patient data to the local filesystem. See
        docs/runbooks/backup-and-restore.md.

        Every command accepts --operator <name> (default: the OS user), recorded in the audit log.
        """;

    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            await output.WriteLineAsync(Usage);
            return UsageError;
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseOptions(args[2..]);
        }
        catch (FormatException exception)
        {
            await output.WriteLineAsync(exception.Message);
            await output.WriteLineAsync(Usage);
            return UsageError;
        }

        var operations = services.GetRequiredService<ITenantOperations>();
        var data = services.GetRequiredService<ITenantDataOperations>();
        var registry = services.GetRequiredService<ITenantRegistry>();
        var actor = $"operator:{options.GetValueOrDefault("operator", Environment.UserName)}";

        try
        {
            // Every command works against the current control-plane schema.
            await using (var context = await services
                .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
                .CreateDbContextAsync(cancellationToken))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await registry.RefreshAsync(cancellationToken);

            switch (args[0], args[1])
            {
                case ("tenants", "list"):
                    // From the control plane, not the registry: a destroyed tenant is absent
                    // from the registry by design, and an operator still needs to see it.
                    await ListAsync(await operations.ListAsync(cancellationToken), registry, output);
                    return Success;

                case ("tenants", "provision"):
                {
                    string? license = options.TryGetValue("license-file", out var file)
                        ? (await File.ReadAllTextAsync(file, cancellationToken)).Trim()
                        : null;

                    var tenant = await operations.ProvisionAsync(
                        new ProvisionTenantRequest(
                            Require(options, "name"),
                            options.GetValueOrDefault("server", "default"),
                            Require(options, "database"),
                            options.TryGetValue("id", out var id) ? ParseId(id) : null,
                            license),
                        actor, cancellationToken);

                    await output.WriteLineAsync($"Tenant '{tenant.Name}' ({tenant.TenantId}) is {tenant.State}, schema {tenant.SchemaVersion}.");
                    return tenant.State == TenantState.Active ? Success : Failure;
                }

                case ("tenants", "suspend"):
                    await operations.SuspendAsync(ParseId(Require(options, "id")), Require(options, "reason"), actor, cancellationToken);
                    await output.WriteLineAsync("Suspended. Running instances stop serving the tenant within one registry refresh interval.");
                    return Success;

                case ("tenants", "resume"):
                    await operations.ResumeAsync(ParseId(Require(options, "id")), Require(options, "reason"), actor, cancellationToken);
                    await output.WriteLineAsync("Resumed.");
                    return Success;

                case ("tenants", "migrate"):
                {
                    var results = await operations.MigrateAsync(
                        options.TryGetValue("id", out var id) ? ParseId(id) : null, actor, cancellationToken);

                    foreach (var result in results)
                    {
                        await output.WriteLineAsync(result.Succeeded
                            ? $"  OK      {result.Name} ({result.TenantId}) schema {result.SchemaVersion}"
                            : $"  FAILED  {result.Name} ({result.TenantId}): {result.Error}");
                    }

                    await output.WriteLineAsync($"{results.Count(r => r.Succeeded)} of {results.Count} tenants migrated.");
                    return results.All(r => r.Succeeded) ? Success : Failure;
                }

                case ("tenants", "backup"):
                {
                    var kind = options.GetValueOrDefault("type", "full").ToLowerInvariant() switch
                    {
                        "full" => BackupKind.Full,
                        "log" => BackupKind.Log,
                        var other => throw new FormatException($"--type must be 'full' or 'log', not '{other}'.")
                    };

                    var result = await data.BackupAsync(
                        ParseId(Require(options, "id")),
                        new BackupRequest(Require(options, "directory"), kind, options.GetValueOrDefault("certificate")),
                        actor, cancellationToken);

                    await output.WriteLineAsync($"{result.Kind} backup of {result.DatabaseName} written to {result.Path} and verified.");
                    if (options.GetValueOrDefault("certificate") is null)
                        await output.WriteLineAsync("WARNING: the backup is not encrypted. DSPT requires encrypted backup storage.");
                    return Success;
                }

                case ("tenants", "restore"):
                {
                    await data.RestoreAsync(
                        new RestoreRequest(
                            ParseId(Require(options, "id")),
                            Require(options, "from"),
                            Require(options, "database"),
                            Replace: options.ContainsKey("replace"),
                            Repoint: options.ContainsKey("repoint")),
                        actor, cancellationToken);

                    await output.WriteLineAsync(options.ContainsKey("repoint")
                        ? "Restored, and the tenant now points at the restored database. Resume it when you are satisfied."
                        : "Restored. The tenant still points at its original database; verify the restore, then repeat with --repoint.");
                    return Success;
                }

                case ("tenants", "export"):
                {
                    var result = await data.ExportAsync(
                        ParseId(Require(options, "id")), Require(options, "file"), actor, cancellationToken);

                    await output.WriteLineAsync($"Exported to {result.Path}:");
                    foreach (var (table, count) in result.RowCounts)
                        await output.WriteLineAsync($"  {table,-22}{count}");
                    await output.WriteLineAsync("This file contains patient data. Handle it as the clinical record.");
                    return Success;
                }

                case ("tenants", "destroy"):
                {
                    await data.DestroyAsync(
                        ParseId(Require(options, "id")), Require(options, "confirm"), actor, cancellationToken);

                    await output.WriteLineAsync("Destroyed. The database is dropped; the registry keeps a tombstone record.");
                    return Success;
                }

                case ("license", "install"):
                {
                    var key = await File.ReadAllTextAsync(Require(options, "file"), cancellationToken);
                    var evaluation = await operations.InstallLicenseAsync(
                        ParseId(Require(options, "id")), key, actor, cancellationToken);

                    await output.WriteLineAsync(
                        $"Installed licence {evaluation.LicenseId}: {evaluation.Status}, expires {evaluation.ExpiresAt:yyyy-MM-dd}, " +
                        $"features {string.Join(", ", evaluation.Features)}.");
                    return Success;
                }

                case ("license", "status"):
                    await LicenseStatusAsync(
                        registry, options.TryGetValue("id", out var statusId) ? ParseId(statusId) : null, output);
                    return Success;

                default:
                    await output.WriteLineAsync(Usage);
                    return UsageError;
            }
        }
        catch (FormatException exception)
        {
            await output.WriteLineAsync(exception.Message);
            return UsageError;
        }
        catch (TenantOperationException exception)
        {
            await output.WriteLineAsync($"Refused: {exception.Message}");
            return Failure;
        }
    }

    private static async Task ListAsync(
        IReadOnlyList<TenantRecord> records, ITenantRegistry registry, TextWriter output)
    {
        if (records.Count == 0)
        {
            await output.WriteLineAsync("No tenants are registered.");
            return;
        }

        foreach (var record in records.OrderBy(t => t.Name))
        {
            if (!registry.TryGet(record.TenantId, out var tenant))
            {
                await output.WriteLineAsync(
                    $"{record.TenantId}  {record.Name}\n" +
                    $"    state {record.State} — {record.StateReason}\n" +
                    $"    database {record.DatabaseServer}/{record.DatabaseName} (gone)");
                continue;
            }

            await output.WriteLineAsync(
                $"{tenant.TenantId}  {tenant.Name}\n" +
                $"    state {tenant.State}, {tenant.Availability}{(tenant.UnavailableReason is null ? "" : $" — {tenant.UnavailableReason}")}\n" +
                $"    database {tenant.DatabaseServer}/{tenant.DatabaseName}, schema {tenant.SchemaVersion ?? "none"}\n" +
                $"    licence {tenant.License.Status} ({tenant.License.Mode})");
        }
    }

    private static async Task LicenseStatusAsync(ITenantRegistry registry, Guid? tenantId, TextWriter output)
    {
        foreach (var tenant in registry.All.Where(t => tenantId is null || t.TenantId == tenantId).OrderBy(t => t.Name))
        {
            var license = tenant.License;
            await output.WriteLineAsync(
                $"{tenant.Name} ({tenant.TenantId}): {license.Status}, mode {license.Mode}" +
                (license.LicenseId is null ? "" :
                    $", licence {license.LicenseId}, expires {license.ExpiresAt:yyyy-MM-dd}, " +
                    $"grace until {license.GracePeriodEndsAt:yyyy-MM-dd}, features {string.Join(", ", license.Features)}") +
                (license.ClockRollbackDetected ? " [CLOCK ROLLBACK DETECTED]" : ""));
        }
    }

    internal static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new FormatException($"Expected '--option value' but found '{args[i]}'.");

            // A switch with no value is a flag: '--replace' and '--repoint' say to do
            // something, not what to do it with.
            var isFlag = i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal);

            options[args[i][2..]] = isFlag ? "true" : args[++i];
        }

        return options;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"--{name} is required.");

    private static Guid ParseId(string value) =>
        Guid.TryParse(value, out var id) ? id : throw new FormatException($"'{value}' is not a tenant id.");
}
