# Runbook: tenant operations

For operators onboarding, suspending, migrating and licensing tenants (MVP-007, 008, 009).
Every command below is idempotent and writes to `control.AuditLog`. Pass `--operator <name>` so
the audit trail names you; it defaults to your OS user.

Commands run through the service binary, with the same configuration as the service:

```bash
# Development, from the repository root
dotnet run --project Betsi -- tenants list

# A deployed build
ASPNETCORE_ENVIRONMENT=Production dotnet Betsi.Core.dll tenants list
```

Exit codes: `0` success, `1` refused or failed (the reason is printed), `2` usage error.

---

## Before the first tenant: configuration

| Setting | Purpose |
|---|---|
| `ConnectionStrings:ControlPlane` | The control-plane database. Created and migrated by any CLI command. |
| `Tenancy:DatabaseServers:<profile>` | A connection string **without** a database, per SQL Server. Secret — keep in a secret store. |
| `Licensing:TrustedKeys` | Public keys that verify licences: `[{ "KeyId": "...", "PublicKeyPem": "..." }]`. |
| `ControlPlane:MigrateOnStartup`, `Tenancy:MigrateTenantsOnStartup` | Leave `false` in production. Migrations are a deployment step. |

The account in a server profile needs `CREATE DATABASE` to provision, and `db_owner` on each
tenant database to migrate.

---

## Onboard a tenant

1. **Choose the database name.** Letters, digits and underscores, starting with a letter:
   `betsi_ysbyty_gwynedd`. It must not already belong to another tenant; the command refuses
   if it does (ADR-001).

2. **Obtain a licence** from the licence issuer (see *Issue a licence* below). You need the
   tenant id to issue one, so either agree the id first and pass `--id`, or provision without a
   licence and install it afterwards.

3. **Provision:**

   ```bash
   dotnet Betsi.Core.dll tenants provision \
     --name "Ysbyty Gwynedd" --database betsi_ysbyty_gwynedd --server default \
     --id 7c1e…  --license-file ysbyty-gwynedd.lic --operator "a.jones"
   ```

   This registers the tenant, creates the database, applies every migration and marks the
   tenant `Active`.

4. **Verify:**

   ```bash
   dotnet Betsi.Core.dll tenants list
   ```

   Expect `state Active, Available` and `licence Valid (Full)`. Running instances pick the tenant
   up within one registry refresh (30 seconds by default).

**If provisioning fails** (unreachable server, permissions), the tenant is left `Failed` with the
reason shown in `tenants list`. Fix the cause and run the **same** `tenants provision` command
again; it resumes. Nothing needs cleaning up first.

A tenant without a licence serves in **restricted mode**: patient care and escalation work,
site administration (creating locations and queues) is refused.

---

## Suspend and resume

```bash
dotnet Betsi.Core.dll tenants suspend --id <tenant> --reason "INC-1234: suspected credential compromise"
dotnet Betsi.Core.dll tenants resume  --id <tenant> --reason "INC-1234 closed"
```

A reason is mandatory. Suspension refuses every API request for the tenant with 403 within one
refresh interval on every instance. **Data is retained**; nothing is deleted.

> Suspending a hospital stops its staff using the system. Outside a security incident, confirm
> the site's downtime procedure is in place first.

---

## Deploy a release: migrate every tenant

Run after deploying a build that contains new migrations, **before** routing traffic to it:

```bash
dotnet Betsi.Core.dll tenants migrate --operator "release-2026.10"
```

Each tenant is migrated in turn and a line is printed per tenant. One failure does not stop the
others. The exit code is `1` if any tenant failed.

A tenant whose migration failed stays `Active` and keeps working on the **previous** build, but
the new build refuses it (503) until its schema catches up. Fix the cause and re-run for that
tenant only:

```bash
dotnet Betsi.Core.dll tenants migrate --id <tenant>
```

Migrations must be expand/contract — additive in the release that introduces them, with removals
in a later release — so the previous build keeps working during a rolling deployment.

---

## Licences

### Check status

```bash
dotnet Betsi.Core.dll license status [--id <tenant>]
```

`GracePeriod` means the licence has expired but full function continues until the grace date.
Renew before then. `[CLOCK ROLLBACK DETECTED]` means the server clock is behind a time the
licence was previously evaluated at; fix NTP. The licence is evaluated against the later time.

### Install a renewal

```bash
dotnet Betsi.Core.dll license install --id <tenant> --file renewal.lic
```

The licence is validated before it replaces the current one. A licence that is not in force
**now** — wrong tenant, bad signature, expired, or not yet started — is refused and the current
licence is left in place.

### Issue a licence (licence issuer only)

On the issuing workstation, never on a hospital server:

```bash
dotnet run --project tools/Betsi.LicenseTool -- issue \
  --private-key /secure/prod-2026.private.pem --key-id prod-2026 \
  --tenant <tenant id> --features core --expires 2027-09-30 --grace-days 30 --out ysbyty-gwynedd.lic
```

### Rotate the signing key

1. `dotnet run --project tools/Betsi.LicenseTool -- keygen --key-id prod-2027 --out /secure`
2. Add `prod-2027`'s public key to `Licensing:TrustedKeys` **alongside** `prod-2026`, and deploy.
3. Issue new licences with `prod-2027` as they come up for renewal.
4. Once no installed licence uses `prod-2026` (`license status` shows each licence's expiry),
   remove it from `TrustedKeys`.

Key ids beginning `dev-` are refused by any non-Development environment at startup.

---

## Backup, restore, export and destruction

These have their own runbook: [`backup-and-restore.md`](backup-and-restore.md). In short,
`tenants backup`, `tenants restore`, `tenants export` and `tenants destroy`, each audited in
`control.AuditLog` like every command above.

Two things to carry across from here:

- **Back up `betsi_control` as well as each tenant database, and restore them together.** A
  tenant database restored without its control-plane row is unreachable, and the control plane
  also holds the Data Protection key ring that every webhook and inbound integration secret is
  encrypted with.
- **`tenants destroy` is irreversible.** It needs the tenant suspended and its name typed back,
  and it leaves a tombstone record so the database name is never reissued.

## Not yet automated

Provisioning is an operator command, not infrastructure as code: a new tenant is a decision with
a licence and a database behind it, and it is audited as such. If a site provisions often enough
for that to chafe, the CLI is the thing to script.
