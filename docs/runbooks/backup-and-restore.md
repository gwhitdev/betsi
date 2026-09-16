# Runbook: backup, restore, export and destruction

For operators protecting and recovering tenant data (MVP-108), and for the tenant export and
destruction Phase D deferred. Every command writes to `control.AuditLog` before it acts and
again with its outcome. Pass `--operator <name>` so the audit trail names you.

**Recovery objectives**: recovery point ≤ 5 minutes, recovery time ≤ 30 minutes. Neither is
met by the commands alone — they are met by the schedule below, and only if the monthly
restore drill is actually run.

```bash
# Development, from the repository root
dotnet run --project Betsi -- tenants backup --id <guid> --directory /var/opt/mssql/backups

# A deployed build
ASPNETCORE_ENVIRONMENT=Production dotnet Betsi.Core.dll tenants backup --id <guid> --directory /backups
```

Paths given to `backup` and `restore` are paths **on the database server**, because SQL Server
writes and reads them. The service never holds a copy of the data. `export` is the exception:
it writes to the local filesystem, because it is a file for a person.

---

## 1. What has to be backed up

| Database | Holds | If it is lost |
|---|---|---|
| Each tenant database | Every episode, event, escalation and audit row for one site | That site's clinical record is gone. This is the one that matters. |
| `betsi_control` | The tenant registry, the control-plane audit log and the Data Protection key ring | Tenants cannot be resolved, and every webhook and inbound integration secret becomes unreadable — they are encrypted with the key ring. Registrations must be re-issued. |

Back up **both**. A tenant database restored next to a control plane that has lost its key ring
serves patients correctly and cannot deliver a webhook.

## 2. The schedule

| What | When | Command |
|---|---|---|
| Full backup, every tenant | Nightly | `tenants backup --id <guid> --directory <dir> --certificate <cert>` per tenant |
| Log backup, every tenant | Every 5 minutes | `tenants backup --id <guid> --directory <dir> --type log --certificate <cert>` |
| Full backup, control plane | Nightly | SQL Server maintenance plan or `sqlcmd`; it is not a tenant, so the CLI does not cover it |
| Restore drill | Monthly | §5 |

Log backups are what make the 5-minute recovery point real: a full backup alone loses
everything since it was taken. They require the database to be in the **full** recovery model;
a database in simple recovery refuses a log backup, and the command reports that.

Every backup is taken `WITH CHECKSUM` and immediately verified with `RESTORE VERIFYONLY`. A
backup that cannot be read is reported as a failure at the time it is taken, not on the night
it is needed.

### Encryption

Pass `--certificate <name>` to encrypt with AES-256 using a server certificate. Without it the
command warns, and the backup is unencrypted — which does not meet DSPT. Create the certificate
once per server and **back up the certificate itself somewhere else**: an encrypted backup
cannot be restored without it.

```sql
CREATE MASTER KEY ENCRYPTION BY PASSWORD = '<strong password>';
CREATE CERTIFICATE betsi_backup WITH SUBJECT = 'Betsi backup encryption';
BACKUP CERTIFICATE betsi_backup TO FILE = '/secure/betsi_backup.cer'
    WITH PRIVATE KEY (FILE = '/secure/betsi_backup.key', ENCRYPTION BY PASSWORD = '<another password>');
```

## 3. Restoring a tenant

The default restores into a **different** database, leaving the live one untouched:

```bash
tenants restore --id <guid> --from /backups/betsi_site_a_20260916T020000Z.bak --database betsi_site_a_restored
```

Verify the restored database (row counts, the most recent episodes), then switch the tenant to
it. Switching requires the tenant to be suspended, because it changes which database a
department's staff are looking at:

```bash
tenants suspend --id <guid> --reason "Restoring after <incident>"
tenants restore --id <guid> --from <path> --database betsi_site_a_restored --repoint
tenants migrate --id <guid>          # bring the restored schema up to this build
tenants resume  --id <guid> --reason "Restore verified"
```

Running instances stop serving a suspended tenant within one registry refresh (30s by default)
and pick the new database up on the refresh after `--repoint`.

### Point in time

A point-in-time restore replays the log chain and is **not** automated: choosing the stop point
is a decision about which minutes of clinical record to discard, and it needs a person. Restore
the full backup with `NORECOVERY`, apply each log backup in order, and stop at the instant:

```sql
RESTORE DATABASE [betsi_site_a_restored] FROM DISK = '/backups/…full.bak' WITH NORECOVERY, REPLACE;
RESTORE LOG [betsi_site_a_restored] FROM DISK = '/backups/…0205.trn' WITH NORECOVERY;
RESTORE LOG [betsi_site_a_restored] FROM DISK = '/backups/…0210.trn'
    WITH STOPAT = '2026-09-16T02:12:00', RECOVERY;
```

Then `tenants restore … --repoint` is not used — the database already exists, so suspend,
repoint by re-provisioning the record, and resume. Record the chosen stop point and the data
lost in the incident log.

## 4. What is *not* restored with the data

- **The Data Protection key ring** lives in the control plane, not in the tenant database. A
  tenant restored alongside its original control plane keeps working. A tenant restored into a
  *new* deployment cannot decrypt webhook or inbound secrets, and every integration must be
  re-registered.
- **Schema version.** A restored database may be older than the running build. `tenants migrate`
  brings it forward; the registry refuses to serve a tenant whose schema is behind the build, so
  a forgotten migration fails closed rather than serving wrong data.

## 5. The monthly restore drill

An untested backup is a hope. Once a month, on a non-production server:

1. `tenants restore --id <guid> --from <last night's backup> --database betsi_drill`.
2. Check the row counts against the source: episodes, domain events, audit logs.
3. Time it. Record the elapsed time against the 30-minute recovery time objective.
4. Drop `betsi_drill`.
5. Record the result — date, tenant, backup age, elapsed time, outcome — in the DSPT evidence
   pack (`docs/DSPT-EVIDENCE.md`).

A drill that takes longer than 30 minutes is a finding, not a footnote.

## 6. Export

For data portability (UK GDPR Article 20), for a site leaving the service, and as the record
kept before destruction:

```bash
tenants export --id <guid> --file /secure/site-a-export.json
```

Writes every episode, location, queue, escalation, policy, follow-up exception, domain event
and audit row as JSON. **This file is the clinical record**: it holds names, dates of birth and
NHS numbers. Handle it as such — encrypted storage, a named recipient, and a deletion date. The
audit log records the export and its row counts, never its content.

## 7. Destruction

Irreversible. Take an export and a final backup first, and keep them for the retention period
agreed with the site — destruction of the live database is not destruction of the record.

```bash
tenants export  --id <guid> --file /secure/site-a-final-export.json
tenants backup  --id <guid> --directory /backups --certificate betsi_backup
tenants suspend --id <guid> --reason "Contract ended <date>, destruction approved by <name>"
tenants destroy --id <guid> --confirm "Site A"
```

Three gates, deliberately: the tenant must be suspended (so nothing is still using it), the
confirmation must match the tenant's name exactly (so no script does this by accident), and the
whole sequence is audited. `--confirm` takes the **name**, not the id.

After destruction the database is dropped and the registry keeps a tombstone record in state
`Destroyed`. Requests naming that tenant get 404, exactly as for a tenant that never existed.
The record is kept so the tenant's history is not silently erased from the registry and so its
database name is never handed to another site.
