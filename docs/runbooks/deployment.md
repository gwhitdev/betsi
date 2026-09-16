# Runbook: deployment

For operators deploying, configuring and rolling back the service (MVP-105, MVP-106, MVP-107).
Tenant onboarding is [`tenant-operations.md`](tenant-operations.md); recovering data is
[`backup-and-restore.md`](backup-and-restore.md); alerts and incidents are
[`observability-and-incidents.md`](observability-and-incidents.md).

---

## 1. What is deployed

One image (`Dockerfile`) that is both the service and the operator command line. With no
arguments it serves the API; with `tenants …` or `license …` it runs the CLI against the same
configuration. A deployment therefore applies migrations with **exactly the build it is about
to run**, not with a separately built migration image that may be a different commit.

```bash
docker run --rm --env-file .env ghcr.io/gwhitdev/betsi@sha256:… tenants migrate
```

Images are tagged by commit SHA as well as by branch, so a deployment names one build and a
rollback names one other.

## 2. Configuration

Everything is configuration; nothing is baked into the image. Environment variables use the
double-underscore form (`ConnectionStrings__ControlPlane`).

| Setting | Required | Notes |
|---|---|---|
| `ConnectionStrings:ControlPlane` | Yes | The control-plane database. |
| `Tenancy:DatabaseServers:<profile>` | Yes | One per SQL Server; a connection string with no database. |
| `Authentication:Jwt:Authority` / `Audience` | Yes | The service refuses to start outside Development without authentication. |
| `Licensing:TrustedKeys` | Yes | Public keys only. |
| `DataProtection:Store` | No | `ControlPlane` (default), `FileSystem`, `Ephemeral`. See §4. |
| `DataProtection:CertificatePath` / `CertificatePassword` | Recommended | Encrypts the key ring at rest. Without it, start-up logs a DSPT warning. |
| `Observability:OtlpEndpoint` | Recommended | Where traces and metrics go. |
| `Observability:DeploymentEnvironment` | Recommended | `staging`, `production`. Tags every span and metric. |
| `Secrets:Directory` | No | Where bare `secret:` references resolve. Default `/run/secrets`. |

### Secrets

No credential belongs in a settings file or in the container's environment listing. Write the
value as a reference and the service resolves it at startup:

| Reference | Resolves to |
|---|---|
| `secret:control-plane` | the file `control-plane` in `Secrets:Directory` — how Docker, Kubernetes and Azure Container Apps all mount a secret |
| `secret:file:/var/run/betsi/control-plane` | an explicit path |
| `secret:env:BETSI_CONTROL_PLANE` | an environment variable, for hosts that inject secrets only that way |

A reference that cannot be resolved **stops the service starting**, naming the configuration key
and never the value. That is deliberate: an unresolved connection string would otherwise become
an empty one, and the symptom of that is a service that starts and then refuses every request.

Rotating a secret takes a restart. Rolling restart one instance at a time; the others keep
serving.

## 3. Health and readiness

| Endpoint | Answers | Used by |
|---|---|---|
| `/health/live` | Is the process up? Touches no database. | The orchestrator's liveness probe |
| `/health/ready` | Can this instance serve tenants? Checks every available tenant database. | The load balancer, and the deployment's smoke test |
| `/health` | The aggregate, with detail. | A person |

Liveness deliberately does not touch a database. If it did, a database outage would have every
instance killed and restarted into the same outage.

The container's `HEALTHCHECK` runs `Betsi.Core --health-probe`, which calls `/health/ready` over
loopback — the runtime image has no curl, and adding one to a clinical system's image to answer
one question is a worse trade.

## 4. The Data Protection key ring

Webhook secrets and inbound integration secrets are encrypted with a Data Protection key ring.
**Every instance must read the same one**: a secret written by one instance is read by whichever
instance next delivers a webhook.

The default store is the control-plane database, so a second instance needs no shared filesystem
and no extra configuration to be correct. `FileSystem` is available for deployments that prefer
a mounted volume — it must be shared storage, not a container-local directory.

`Ephemeral` is refused outside Development: it would be lost on restart, and every webhook and
inbound secret with it, which looks like every subscriber silently going quiet.

Set `DataProtection:CertificatePath` to encrypt the ring at rest. Without it the keys sit
unencrypted in the control-plane database — the service logs a warning on every start, and it is
a DSPT finding. **Back the certificate up separately**: without it the ring cannot be read.

## 5. Deploying

`.github/workflows/release.yml` builds the image on every push to `main`, deploys it to staging
automatically, and deploys to production on a `v*` tag or a manual dispatch — behind the
approval rule configured on the `production` GitHub environment. The reviewer list lives in
repository settings rather than in the workflow file, so a pull request cannot change who
approves a production deployment.

Each environment needs:

| Kind | Name | Value |
|---|---|---|
| Variable | `DEPLOY_ENABLED` | `true`. Until it is set the deploy jobs are skipped, so pushes to `main` do not fail on infrastructure that does not exist yet. |
| Variable | `REMOTE_DIR` | Compose project directory on the host, e.g. `/opt/betsi`. |
| Variable | `HEALTH_URL` | Readiness URL as the host sees it. |
| Variable | `BETSI_PUBLIC_URL` | Base URL for the smoke test. |
| Secret | `DEPLOY_HOST` | `user@host`. |
| Secret | `DEPLOY_SSH_KEY` | Private key for that user. |

`tools/deploy.sh` does the work, in this order:

1. Record the running image, so a failure can be undone.
2. Pull the new image.
3. **Migrate**, using the new image, while the old version is still serving.
4. Switch and wait up to `HEALTH_TIMEOUT` (default 120s) for readiness.
5. If readiness does not come back, restore the previous image and exit non-zero.

Migrating before switching is safe because migrations in this system are expand-only during a
release: the tenant registry allows a database to be **ahead** of the build and refuses one that
is behind. The old version keeps working against the new schema, so the switch is the only
moment of risk.

After staging deploys, the workflow smoke-tests two anonymous things: that the OpenAPI document
is served, and that an unauthenticated waiting-board request is still refused with 401. Neither
touches patient data.

## 6. Rolling back

An automatic rollback happens when readiness fails. To roll back deliberately:

```bash
cd /opt/betsi
BETSI_IMAGE=ghcr.io/gwhitdev/betsi@sha256:<previous> docker compose up -d --wait betsi
```

**The schema is not rolled back with it.** The previous build tolerates a newer schema by design,
and reversing a migration under a live database is how data is lost. If the migration itself is
the fault, restore from backup — [`backup-and-restore.md`](backup-and-restore.md) §3.

## 7. Blue/green, when a site needs it

The pilot deployment is a rolling replace with a readiness gate and automatic rollback, which is
what a single-host site can run. A site that needs zero-downtime releases runs two stacks behind
its load balancer:

1. Deploy to the idle stack and wait for `/health/ready`.
2. Run the smoke tests against it directly.
3. Move the load balancer to it.
4. Keep the previous stack running, untouched, until the next release — it is the rollback.

Migrations still run once, before the idle stack is started; both stacks then run against one
schema, which is why expand-only migrations are a rule and not a preference.

## 8. First deployment to a new environment

1. Create the control-plane database's server and an empty database, and mount its connection
   string as a secret.
2. Start one instance with `ControlPlane:MigrateOnStartup=true`, or run `tenants migrate` once.
   Leave the flag `false` afterwards: migrations are a deployment step, not a start-up side effect.
3. Install a licence and provision the first tenant — [`tenant-operations.md`](tenant-operations.md).
4. Configure the identity provider and register the API as a resource —
   [`identity-and-integrations.md`](identity-and-integrations.md).
5. Configure backups and the log-backup schedule **before** any real patient data arrives —
   [`backup-and-restore.md`](backup-and-restore.md) §2.
6. Point `Observability:OtlpEndpoint` at the collector and confirm traces and metrics arrive.
7. Run the restore drill once, immediately, rather than waiting a month to find out.
