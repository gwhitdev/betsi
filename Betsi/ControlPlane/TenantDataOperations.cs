namespace Betsi.ControlPlane;

using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>What a backup covers.</summary>
public enum BackupKind
{
    /// <summary>The whole database. The base of any restore.</summary>
    Full = 1,

    /// <summary>
    /// The transaction log since the last log backup. Taken on a schedule between full
    /// backups; this is what makes the ≤5 minute recovery point objective achievable, and
    /// what a point-in-time restore replays.
    /// </summary>
    Log = 2
}

public sealed record BackupRequest(string Directory, BackupKind Kind = BackupKind.Full, string? ServerCertificate = null);

public sealed record TenantBackupResult(Guid TenantId, string DatabaseName, BackupKind Kind, string Path, bool Verified);

/// <param name="TenantId">Tenant whose backup this is.</param>
/// <param name="Path">Backup file, as the database server sees it.</param>
/// <param name="TargetDatabase">Database to restore into. Never the live one unless <paramref name="Replace"/>.</param>
/// <param name="Replace">Overwrite <paramref name="TargetDatabase"/> if it exists. The tenant must be suspended.</param>
/// <param name="Repoint">Point the tenant record at <paramref name="TargetDatabase"/> once the restore succeeds.</param>
public sealed record RestoreRequest(
    Guid TenantId, string Path, string TargetDatabase, bool Replace = false, bool Repoint = false);

public sealed record TenantExportResult(Guid TenantId, string Path, IReadOnlyDictionary<string, int> RowCounts);

/// <summary>
/// Backup, restore, export and destruction of a tenant's data (MVP-108, and the tenant export
/// and destruction Phase D deferred).
/// </summary>
/// <remarks>
/// <para>
/// Every operation here moves or destroys patient data, so every one is audited in the control
/// plane before it is attempted and again with its outcome, and every one refuses to act on a
/// tenant that is serving requests. Backups are written by the database server to a path on the
/// database server — not streamed through this process, which has no business holding a copy of
/// every episode in a department.
/// </para>
/// <para>
/// Point-in-time recovery is a full backup plus the chain of log backups after it. This class
/// takes both kinds and restores a full backup; replaying a log chain to a chosen instant is
/// done by the operator following <c>docs/runbooks/backup-and-restore.md</c>, because choosing
/// the stop point is a judgement about what data loss is acceptable and is not automatable.
/// </para>
/// </remarks>
public interface ITenantDataOperations
{
    Task<TenantBackupResult> BackupAsync(Guid tenantId, BackupRequest request, string actor, CancellationToken cancellationToken);

    Task RestoreAsync(RestoreRequest request, string actor, CancellationToken cancellationToken);

    /// <summary>Writes the tenant's records to a JSON file, for data portability and for the record before destruction.</summary>
    Task<TenantExportResult> ExportAsync(Guid tenantId, string path, string actor, CancellationToken cancellationToken);

    /// <summary>Drops the tenant's database and marks the registry record destroyed. Irreversible.</summary>
    Task DestroyAsync(Guid tenantId, string confirmation, string actor, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITenantDataOperations"/>
public sealed partial class TenantDataOperations : ITenantDataOperations
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly DatabaseServerCatalog _servers;
    private readonly ITenantRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantDataOperations> _logger;

    public TenantDataOperations(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        DatabaseServerCatalog servers,
        ITenantRegistry registry,
        TimeProvider time,
        ILogger<TenantDataOperations> logger)
    {
        _contextFactory = contextFactory;
        _servers = servers;
        _registry = registry;
        _time = time;
        _logger = logger;
    }

    // Same rule as a database name: an unquoted SQL Server identifier with no punctuation, so
    // a certificate or database name cannot escape the identifier it is placed in.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex IdentifierPattern();

    public async Task<TenantBackupResult> BackupAsync(
        Guid tenantId, BackupRequest request, string actor, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, tenantId, cancellationToken);

        if (tenant.State == TenantState.Destroyed)
            throw new TenantOperationException($"Tenant '{tenant.Name}' has been destroyed; there is nothing to back up.");

        var certificate = request.ServerCertificate;
        if (certificate is not null && !IdentifierPattern().IsMatch(certificate))
            throw new TenantOperationException($"'{certificate}' is not a valid server certificate name.");

        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var extension = request.Kind == BackupKind.Full ? "bak" : "trn";
        var path = $"{request.Directory.TrimEnd('/', '\\')}/{tenant.DatabaseName}_{stamp}.{extension}";

        var options = new List<string> { "COMPRESSION", "CHECKSUM", "STATS = 10" };

        if (request.Kind == BackupKind.Full)
        {
            // FORMAT and INIT so the file is a backup set of exactly one backup: appending to
            // whatever was there before makes a restore an archaeology exercise.
            options.Add("FORMAT");
            options.Add("INIT");
        }

        if (certificate is not null)
            options.Add($"ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = [{certificate}])");
        else
            _logger.LogWarning("Backing up tenant {TenantId} unencrypted. DSPT requires encrypted backup storage.", tenantId);

        var verb = request.Kind == BackupKind.Full ? "BACKUP DATABASE" : "BACKUP LOG";

        try
        {
            await ExecuteOnServerAsync(
                tenant.DatabaseServer,
                $"{verb} [{tenant.DatabaseName}] TO DISK = @path WITH {string.Join(", ", options)}",
                cancellationToken,
                new SqlParameter("@path", path));

            // A backup that cannot be read is not a backup. Verifying here rather than in a
            // monthly drill means the failure is found while the data still exists.
            await ExecuteOnServerAsync(
                tenant.DatabaseServer,
                "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM",
                cancellationToken,
                new SqlParameter("@path", path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditAsync(context, tenantId, "BackupTenant", actor, "Failure", $"{path}: {exception.Message}", cancellationToken);
            throw new TenantOperationException($"Backup of '{tenant.Name}' failed: {exception.Message}", exception);
        }

        await AuditAsync(context, tenantId, "BackupTenant", actor, "Success",
            $"{request.Kind} backup to {path}, verified{(certificate is null ? ", unencrypted" : $", encrypted with [{certificate}]")}.",
            cancellationToken);

        return new TenantBackupResult(tenantId, tenant.DatabaseName, request.Kind, path, Verified: true);
    }

    public async Task RestoreAsync(RestoreRequest request, string actor, CancellationToken cancellationToken)
    {
        if (!IdentifierPattern().IsMatch(request.TargetDatabase))
            throw new TenantOperationException($"'{request.TargetDatabase}' is not a valid database name.");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, request.TenantId, cancellationToken);

        var overwritingLiveDatabase = string.Equals(
            request.TargetDatabase, tenant.DatabaseName, StringComparison.OrdinalIgnoreCase);

        // Restoring over the database a tenant is serving from would replace a department's
        // live record with a snapshot mid-shift. Suspending first is what makes that a decision
        // rather than an accident.
        if ((overwritingLiveDatabase || request.Replace) && tenant.State != TenantState.Suspended)
        {
            throw new TenantOperationException(
                $"Tenant '{tenant.Name}' is {tenant.State}. Suspend it before restoring over a database it may be serving.");
        }

        if (!overwritingLiveDatabase && !request.Replace && await DatabaseExistsAsync(tenant.DatabaseServer, request.TargetDatabase, cancellationToken))
        {
            throw new TenantOperationException(
                $"Database '{request.TargetDatabase}' already exists. Choose another name, or pass --replace to overwrite it.");
        }

        await AuditAsync(context, request.TenantId, "RestoreTenant", actor, "Started",
            $"From {request.Path} into {tenant.DatabaseServer}/{request.TargetDatabase}.", cancellationToken);

        try
        {
            // Every file in the backup is relocated to a name derived from the target database.
            // Without this, restoring under a new name fails the moment the source database
            // still exists — the backup carries the original file paths, and two databases
            // cannot share one .mdf. That is the normal case here: restore beside the live
            // database, verify, then repoint.
            var moves = await FileMovesAsync(tenant.DatabaseServer, request.Path, request.TargetDatabase, cancellationToken);

            // A restore needs the database to itself. Suspending the tenant stops new requests,
            // but connection pools hold what they already opened, and SQL Server will not
            // overwrite a database with a session on it. Rolling those back is the point of
            // suspending first: nothing on them is a request still being served.
            if (await DatabaseExistsAsync(tenant.DatabaseServer, request.TargetDatabase, cancellationToken))
            {
                await ExecuteOnServerAsync(
                    tenant.DatabaseServer,
                    $"ALTER DATABASE [{request.TargetDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                    cancellationToken);
            }

            await ExecuteOnServerAsync(
                tenant.DatabaseServer,
                $"RESTORE DATABASE [{request.TargetDatabase}] FROM DISK = @path WITH RECOVERY, REPLACE, STATS = 10" +
                (moves.Count == 0 ? "" : ", " + string.Join(", ", moves)),
                cancellationToken,
                new SqlParameter("@path", request.Path));

            // Restored databases come back in whatever access mode the backup carried, which for
            // a database taken into SINGLE_USER above is SINGLE_USER. Left that way, the tenant
            // resumes and the first request takes the only connection the database allows.
            await ExecuteOnServerAsync(
                tenant.DatabaseServer,
                $"ALTER DATABASE [{request.TargetDatabase}] SET MULTI_USER",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditAsync(context, request.TenantId, "RestoreTenant", actor, "Failure", exception.Message, cancellationToken);
            throw new TenantOperationException($"Restore for '{tenant.Name}' failed: {exception.Message}", exception);
        }

        if (request.Repoint && !overwritingLiveDatabase)
        {
            var previous = tenant.DatabaseName;
            tenant.DatabaseName = request.TargetDatabase;
            tenant.UpdatedAt = _time.GetUtcNow().UtcDateTime;

            await AuditAsync(context, request.TenantId, "RepointTenant", actor, "Success",
                $"Database moved from {previous} to {request.TargetDatabase} after a restore.", cancellationToken);

            await _registry.RefreshAsync(cancellationToken);
        }

        await AuditAsync(context, request.TenantId, "RestoreTenant", actor, "Success",
            $"Restored {request.Path} into {request.TargetDatabase}." +
            (request.Repoint ? " Tenant repointed." : " Tenant not repointed; verify before switching."),
            cancellationToken);
    }

    public async Task<TenantExportResult> ExportAsync(
        Guid tenantId, string path, string actor, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, tenantId, cancellationToken);

        if (tenant.State == TenantState.Destroyed)
            throw new TenantOperationException($"Tenant '{tenant.Name}' has been destroyed; there is nothing to export.");

        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(tenantId);

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(_servers.ConnectionStringFor(tenant.DatabaseServer, tenant.DatabaseName))
            .Options;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            await using var tenantDatabase = new BetsiDbContext(options, tenantContext);

            // Streamed to the file table by table: an export of a busy department is larger
            // than this process should hold in memory at once.
            await using var file = File.Create(path);
            await using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteString("tenantId", tenantId);
            writer.WriteString("tenantName", tenant.Name);
            writer.WriteString("schemaVersion", tenant.SchemaVersion);
            writer.WriteString("exportedAt", _time.GetUtcNow().UtcDateTime);
            writer.WriteStartObject("data");

            await WriteTableAsync(writer, "patientEpisodes", tenantDatabase.PatientEpisodes, counts, cancellationToken);
            await WriteTableAsync(writer, "locations", tenantDatabase.Locations, counts, cancellationToken);
            await WriteTableAsync(writer, "queues", tenantDatabase.Queues, counts, cancellationToken);
            await WriteTableAsync(writer, "escalations", tenantDatabase.Escalations, counts, cancellationToken);
            await WriteTableAsync(writer, "escalationPolicies", tenantDatabase.EscalationPolicies, counts, cancellationToken);
            await WriteTableAsync(writer, "followUpExceptions", tenantDatabase.FollowUpExceptions, counts, cancellationToken);
            await WriteTableAsync(writer, "domainEvents", tenantDatabase.DomainEvents, counts, cancellationToken);
            await WriteTableAsync(writer, "auditLogs", tenantDatabase.AuditLogs, counts, cancellationToken);

            writer.WriteEndObject();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditAsync(context, tenantId, "ExportTenant", actor, "Failure", exception.Message, cancellationToken);
            throw new TenantOperationException($"Export of '{tenant.Name}' failed: {exception.Message}", exception);
        }

        // The detail names the file and the volume, never a patient: the audit log is read by
        // people who are not entitled to the clinical record.
        await AuditAsync(context, tenantId, "ExportTenant", actor, "Success",
            $"Exported to {path}: {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}.", cancellationToken);

        return new TenantExportResult(tenantId, path, counts);
    }

    public async Task DestroyAsync(
        Guid tenantId, string confirmation, string actor, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, tenantId, cancellationToken);

        if (tenant.State == TenantState.Destroyed)
        {
            await AuditAsync(context, tenantId, "DestroyTenant", actor, "NoChange", "Already destroyed.", cancellationToken);
            return;
        }

        // Three separate gates, because this is the one operation with no undo: the tenant must
        // be out of service, and the operator must type its name, which no script does by accident.
        if (tenant.State != TenantState.Suspended)
        {
            throw new TenantOperationException(
                $"Tenant '{tenant.Name}' is {tenant.State}. Suspend it first: destruction is irreversible and " +
                "a suspended tenant proves nothing is still using it.");
        }

        if (!string.Equals(confirmation, tenant.Name, StringComparison.Ordinal))
        {
            await AuditAsync(context, tenantId, "DestroyTenant", actor, "Refused", "Confirmation did not match the tenant name.", cancellationToken);
            throw new TenantOperationException(
                $"Confirmation '{confirmation}' does not match the tenant name. Pass --confirm '{tenant.Name}' to destroy it.");
        }

        await AuditAsync(context, tenantId, "DestroyTenant", actor, "Started",
            $"Dropping {tenant.DatabaseServer}/{tenant.DatabaseName}.", cancellationToken);

        try
        {
            // Single-user first: a connection left open by a running instance would otherwise
            // make the drop fail, and the tenant is suspended, so nothing should hold one.
            await ExecuteOnServerAsync(
                tenant.DatabaseServer,
                $"""
                 IF DB_ID(@database) IS NOT NULL
                 BEGIN
                     ALTER DATABASE [{tenant.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                     DROP DATABASE [{tenant.DatabaseName}];
                 END
                 """,
                cancellationToken,
                new SqlParameter("@database", tenant.DatabaseName));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditAsync(context, tenantId, "DestroyTenant", actor, "Failure", exception.Message, cancellationToken);
            throw new TenantOperationException($"Could not drop '{tenant.Name}': {exception.Message}", exception);
        }

        tenant.State = TenantState.Destroyed;
        tenant.StateReason = $"Destroyed by {actor} on {_time.GetUtcNow().UtcDateTime:O}.";
        tenant.LicenseKey = null;
        tenant.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        // The registry record is kept, not deleted: it is the evidence that this tenant existed
        // and what became of it, and it keeps the database name from being handed to another tenant.
        await AuditAsync(context, tenantId, "DestroyTenant", actor, "Success",
            $"Database {tenant.DatabaseName} dropped. Registry record retained as a tombstone.", cancellationToken);

        await _registry.RefreshAsync(cancellationToken);
        _logger.LogWarning("Tenant {TenantId} ({TenantName}) destroyed by {Actor}", tenantId, tenant.Name, actor);
    }

    private static async Task WriteTableAsync<T>(
        Utf8JsonWriter writer, string name, DbSet<T> set, Dictionary<string, int> counts,
        CancellationToken cancellationToken) where T : class
    {
        writer.WriteStartArray(name);
        var count = 0;

        await foreach (var row in set.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            JsonSerializer.Serialize(writer, row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            count++;
        }

        writer.WriteEndArray();
        counts[name] = count;
    }

    /// <summary>
    /// MOVE clauses placing every file in the backup under the target database's name, in the
    /// server's own default data and log directories.
    /// </summary>
    /// <remarks>
    /// Read from the backup with RESTORE FILELISTONLY rather than guessed: a database may have
    /// more than one data file, and the logical names inside a backup are the source database's,
    /// which is exactly what this is renaming away from.
    /// </remarks>
    private async Task<List<string>> FileMovesAsync(
        string server, string backupPath, string targetDatabase, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_servers.ConnectionStringFor(server, "master"));
        await connection.OpenAsync(cancellationToken);

        // The server's configured default, where it has one. SQL Server on Linux often reports
        // nothing here, so each file falls back to the directory it came from — which is the
        // right answer anyway when restoring onto the server the backup was taken from.
        var (defaultData, defaultLog) = await DefaultFileDirectoriesAsync(connection, cancellationToken);

        var moves = new List<string>();

        await using (var fileList = connection.CreateCommand())
        {
            fileList.CommandText = "RESTORE FILELISTONLY FROM DISK = @path";
            fileList.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;
            fileList.Parameters.Add(new SqlParameter("@path", backupPath));

            await using var reader = await fileList.ExecuteReaderAsync(cancellationToken);
            var dataFiles = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                var logicalName = reader.GetString(reader.GetOrdinal("LogicalName"));
                var physicalName = reader.GetString(reader.GetOrdinal("PhysicalName"));
                var isLog = reader.GetString(reader.GetOrdinal("Type")).Equals("L", StringComparison.OrdinalIgnoreCase);

                var directory = (isLog ? defaultLog : defaultData) ?? DirectoryOf(physicalName);
                var separator = physicalName.Contains('\\') ? '\\' : '/';

                var fileName = isLog
                    ? $"{targetDatabase}_log.ldf"
                    : $"{targetDatabase}{(dataFiles++ == 0 ? "" : $"_{dataFiles}")}.mdf";

                var target = $"{directory.TrimEnd('/', '\\')}{separator}{fileName}";

                // Doubled quotes: a logical file name comes from the backup, not from an
                // operator, but it still reaches a SQL string literal.
                moves.Add($"MOVE '{logicalName.Replace("'", "''")}' TO '{target.Replace("'", "''")}'");
            }
        }

        return moves;
    }

    private static async Task<(string? Data, string? Log)> DefaultFileDirectoriesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), " +
            "       CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'))";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return (null, null);

        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>The directory part of a path the database server wrote, which may use either separator.</summary>
    private static string DirectoryOf(string physicalName)
    {
        var index = physicalName.LastIndexOfAny(['/', '\\']);
        return index <= 0 ? physicalName : physicalName[..index];
    }

    private async Task<bool> DatabaseExistsAsync(string server, string database, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_servers.ConnectionStringFor(server, "master"));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@database) IS NULL THEN 0 ELSE 1 END";
        command.Parameters.Add(new SqlParameter("@database", database));

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task ExecuteOnServerAsync(
        string server, string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
    {
        // Against master: a backup, restore or drop cannot run on a connection to the database
        // it is operating on.
        await using var connection = new SqlConnection(_servers.ConnectionStringFor(server, "master"));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // Backups and restores of a department's history take as long as they take; the default
        // 30 seconds would abandon one halfway and leave the operator unsure whether it ran.
        command.CommandTimeout = (int)TimeSpan.FromHours(2).TotalSeconds;
        command.Parameters.AddRange(parameters);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TenantRecord> FindAsync(
        ControlPlaneDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        await context.Tenants.SingleOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken)
        ?? throw new TenantOperationException($"Tenant '{tenantId}' is not registered.");

    private async Task AuditAsync(
        ControlPlaneDbContext context, Guid tenantId, string action, string actor, string outcome,
        string? detail, CancellationToken cancellationToken)
    {
        context.AuditLog.Add(ControlPlaneAudit.Entry(tenantId, action, actor, outcome, detail, _time.GetUtcNow()));
        await context.SaveChangesAsync(cancellationToken);
    }
}
