# Architecture

A modular monolith: one deployable ASP.NET Core application, internally divided into domain,
application, infrastructure and API layers, with one database per tenant.

## Request path

```
HTTP request
  │
  ├─ Authentication ──────────────── OIDC bearer · signed inbound message · Development headers
  │
  ├─ TenantResolutionMiddleware ──── tenant + acting role from verified claims only;
  │                                  no tenant 400 · unknown 404 · suspended 403 · not ready 503
  │
  ├─ Authorization ───────────────── endpoint permission from the role matrix; denials audited
  │
  ├─ Controller ──────────────────── dispatches a command, returns the result
  │
  ├─ MediatR pipeline
  │    ├─ LoggingBehaviour ───────── request name, tenant, actor, duration
  │    ├─ AuditBehaviour ─────────── one AuditLogs row per command, success or failure
  │    ├─ AuthorizationBehaviour ──── the command's own permission; system-only commands
  │    ├─ LicenseBehaviour ───────── refuses licence-gated commands in restricted mode
  │    └─ ValidationBehaviour ────── FluentValidation; throws on failure
  │
  ├─ Handler ─────────────────────── loads the aggregate, applies the change
  │
  ├─ AggregateRepository ─────────── aggregate + event log + outbox in one transaction
  │
  └─ ProblemDetailsExceptionHandler  translates any exception into an RFC 9457 response
```

Behaviours are ordered so that logging wraps everything, auditing sits outside licensing and
validation (a refused or invalid command is still audited), and validation runs immediately
before the handler.

## Tenant isolation

ADR-001 specifies **database per tenant**. Isolation is therefore a property of which
connection string a request resolves to, not of a `WHERE TenantId = @p` predicate:

1. `TenantResolutionMiddleware` reads the tenant from a claim, or in Development from a
   header, and rejects the request if it cannot.
2. `ITenantRegistry` maps that tenant to its connection string, refusing unknown tenants
   rather than defaulting and refusing tenants that are suspended or not ready.
3. `BetsiDbContext` is constructed per request against that connection.

`TenantId` is still stored on every row and asserted by `BetsiDbContext.SaveChangesAsync`, as
defence in depth: a write whose tenant does not match the resolved scope throws
`TenantIsolationViolationException` rather than landing in the wrong organisation's database.

> **Why not global query filters.** An earlier implementation used
> `HasQueryFilter(e => e.TenantId == _tenantId)` over an instance field. EF caches the built
> model per `DbContextOptions`, so the first tenant resolved in the process had its id
> compiled into the model for every tenant thereafter — a cross-tenant data leak. Under
> database-per-tenant the filter is also redundant. `TenantIsolationTests` holds this line.

## Authentication and authorisation

**Identity comes only from verified credentials.** Three authentication schemes sit behind one
policy scheme that picks by request: a `Bearer` token goes to JWT validation; the inbound
integration path goes to signature verification; anything else goes to the Development header
scheme if it is enabled, or JWT otherwise. A bad bearer token never falls back to headers. The
tenant middleware reads tenant, subject and roles from the resulting principal; it never reads
identity from a raw header.

**Tokens.** Any OIDC provider: discovery via `Authority`, or static public keys. Asymmetric
algorithms only. Claim names are configuration, not code, so Entra ID, NHS CIS2 or Keycloak need
mapping, not changes. Non-GUID subjects are hashed with the issuer into a stable actor id.

**Acting role.** Audit records and domain rules need one role per action, but NHS staff often
hold several. A multi-role token must select one — CIS2-style — by claim or header, and it must be
a role the token holds. `System` (background work) and `Integration` (signed inbound messages) are
reserved: `TenantContext.ResolveSystem` is unreachable from a request, and the Integration role is
granted only to a principal authenticated by the inbound signature scheme.

**Two layers of permission.** Controllers declare the permission a read needs; the
`AuthorizationBehaviour` enforces the permission every command declares. The second layer exists
because commands arrive by three routes — resource endpoints, the command envelope and inbound
integrations — and only a check at the application boundary covers all of them (spec §6). Tests
fail if any endpoint or command lacks a permission. The role → permission matrix is in
`Security/Permissions.cs` and is a governance artefact.

**Auditing.** Every refused authorisation and every successful read of patient data writes an
`AuditLogs` row (who, which role, what, when — never the data).

## Integrations

```
Outbox ──▶ CompositeOutboxPublisher ──▶ log
                                    └─▶ WebhookFanOutPublisher ──▶ WebhookDeliveries (same transaction)
                                                                          │
                                   WebhookDeliveryService (10s) ──▶ WebhookDeliverer ──▶ signed POST ─▶ subscriber
                                                                          │  backoff · dead letter · SSRF-checked connect

EPR ─▶ POST /integrations/inbound/{tenant}/{source}
         InboundSignature scheme (HMAC, 5 min window) ─▶ Integration principal
           InboundMessageProcessor: reserve message id ─▶ HL7 v2 / FHIR reader ─▶ intent
             ─▶ Register / Discharge / Cancel commands through the normal pipeline
             ─▶ Accepted, or Quarantined with the body for review
```

**Minimum data out.** Webhook payloads are built from an allowlist of fields per event type, not
by serialising the domain event, so a field added to an event for internal reasons never leaks to
subscribers. No names, dates of birth, NHS numbers or notes.

**Exactly-once effects from at-least-once transport.** Outbound: one delivery row per
(subscription, event), delivered at least once, subscribers dedupe on `Betsi-Event-Id`. Inbound:
a message id is reserved in the database before anything acts on it, so resends and concurrent
copies act once; a visit id maps to one episode.

**Quarantine, don't discard** (spec §6). An inbound message that cannot be processed is stored
with its error and acknowledged, so it is neither lost nor retried forever. Its body — patient
data — is readable only with episodes.read, and reading it is audited.

**Secrets** for webhooks and sources are generated server-side, shown once, and stored encrypted
with ASP.NET Data Protection. Every instance must share the key ring
(`DataProtection:KeysDirectory` or a key store) — Phase I provisions it properly.

## Control plane

The tenant registry lives in its own database, `betsi_control` (schema `control`), separate
from every tenant database and holding no clinical data (spec §3.3).

| Table | Holds |
|---|---|
| `control.Tenants` | Each tenant's name, lifecycle state, database server profile and name, schema version, licence key, licence high-water mark |
| `control.AuditLog` | Every provisioning, suspension, migration and licence action, and every licence status change |

**No credentials in the registry.** A tenant row names a *server profile*
(`Tenancy:DatabaseServers:<name>`); the profile's connection string lives in configuration
and, in production, a secret store. A copy of the control-plane database does not grant access
to any tenant's patient records.

**Lifecycle.** `Provisioning → Active ⇄ Suspended`, with `Failed` reachable from provisioning
and recoverable by re-running the operation. Every operation is idempotent. Operators drive it
with the host's CLI (`dotnet Betsi.Core.dll tenants …`); there is deliberately no HTTP API for
it, because it crosses tenants and so falls outside the per-tenant authorisation model. See
[`runbooks/tenant-operations.md`](runbooks/tenant-operations.md).

**Registry snapshot.** Each instance serves requests from an in-memory snapshot refreshed every
`ControlPlane:RegistryRefreshInterval` (30s). The request path never waits on the control
plane, and a control-plane outage leaves already-serving tenants serving. The cost is that a
suspension takes up to one interval to reach every instance. Until its first successful load
the snapshot is empty, so an instance serves no one.

**Availability.** A registered tenant is refused with 503 when it is provisioning, failed, on a
server profile this instance lacks, or on an **older** schema than this build's latest
migration. A newer schema is accepted, so the previous build keeps working during a rolling
deployment (expand/contract).

**Startup.** One tenant that cannot be migrated no longer stops the application: it is refused
while every other tenant serves. In production, migrations run as a deployment step
(`tenants migrate`), not at startup.

## Licensing

Licence keys are `BETSI1.<payload>.<signature>`: a base64url JSON payload (tenant, features,
issue/start/expiry dates, grace period, key id) signed with RSA-PSS/SHA-256. The service holds
**public keys only** (`Licensing:TrustedKeys`); signing lives in `tools/Betsi.LicenseTool`.

| Status | Mode |
|---|---|
| `Valid`, `GracePeriod` | Full |
| `Expired`, `NotYetValid`, `Missing`, `Malformed`, `UnsupportedFormat`, `UnknownSigningKey`, `InvalidSignature`, `WrongTenant` | Restricted |

**Restricted mode never stops patient care.** Every command carries exactly one of
`[AlwaysAvailable]` or `[RequiresLicense(feature)]`; there is no default, and a test fails if a
command has neither. All patient-episode and escalation commands are always available (spec §5:
licensing must never disable clinically necessary safety functions). Only site administration —
creating locations and queues — is gated. **This classification is a clinical safety decision
and must be reviewed into the DCB0129 hazard log.**

**Clock safety.** Each tenant has a licence high-water mark in the control plane that only moves
forward. Evaluation uses the later of the clock and the mark, so winding a server clock back
cannot revive an expired licence; a rollback beyond `Licensing:ClockRollbackTolerance` is
audited.

**Development keys.** Key ids beginning `dev-` are refused outside Development at startup, so a
development licence cannot unlock a production system. The development private key is not in
the repository (`*.private.pem` is ignored).

## Aggregates, versions and concurrency

`AggregateRoot.Version` does three jobs:

- **Event ordering.** `RaiseDomainEvent` increments the version and stamps the event with the
  value it produced, so `(AggregateId, Version)` identifies an event and orders the log.
- **Optimistic concurrency.** `Version` is an EF concurrency token. EF compares the value
  loaded from the database in the `UPDATE` predicate, so a concurrent write raises
  `DbUpdateConcurrencyException` → HTTP 409.
- **Caller coordination.** Commands carry `ExpectedVersion`; a mismatch at load time fails
  fast with `AggregateConcurrencyException`, and the 409 response includes the actual version
  so the caller can retry without another round trip.

Illegal state transitions throw `DomainRuleViolationException`, which maps to 422 — the
request was well formed and nobody is racing; the episode is simply not in a state where the
operation makes clinical sense.

## Events and the outbox

Aggregate state, the event log (`DomainEvents`) and the outbox (`OutboxMessages`) are written
in a single `SaveChangesAsync`, so they share one transaction. An event visible in the log but
not queued for delivery — or the reverse — would be a silently lost escalation.

`OutboxBackgroundService` polls each tenant's outbox on an interval and hands each message to
an `IOutboxPublisher`. The MVP publisher logs and treats the message as delivered (ADR-004:
no external broker yet); the seam is where a broker or the webhook delivery of MVP-067 plugs
in. Messages that fail are retried and, after `MaxAttempts`, dead-lettered and logged at error
so one poison message cannot block the queue behind it.

## Escalation engine

```
                ┌──────────────── EscalationPolicy (approved revision in force) ─────────────┐
                │                                                                             │
WaitingTimeMonitorService ─ every 15s, each available tenant ─▶ WaitingTimeMonitor            │
                                                                   │  reads: waiting patients │
                                                                   │  past tier 1; tiers      │
                                                                   │  already raised; overdue │
                                                                   │  escalations             │
                                                                   ▼                          │
                        RaisePolicyEscalationCommand ──▶ handler re-checks policy ◀───────────┘
                        RaiseFollowUpExceptionCommand ─▶ handler: exception + escalation → ManualFollowUp, one transaction
                                  │
                                  └─ same MediatR pipeline as a human command: logged, audited, licence-classified, validated
```

**The monitor decides who to consider; the handlers decide what happens.** Following spec
Appendix A, the monitor only reads and submits commands. Each handler re-validates against the
database, so a stale or duplicated command cannot raise an escalation the policy does not call
for, and every automatic action is audited exactly as a human one is.

**Idempotency is enforced by the database**, not only by checks: a unique index on
(patient, tier) for policy escalations, and on (escalation, missed deadline) for follow-up
exceptions. Two monitor instances racing on the same patient produce one escalation. The
monitor's commands are not bound to any HTTP endpoint, because they carry the evaluation time;
a test holds that line.

**Cost.** Evaluation is three queries per tenant plus one command per escalation actually due,
so steady-state cost is independent of how many patients have already been escalated. With 150
waiting patients a steady-state evaluation and a board load are each asserted under their
MVP-021/023 budgets (1s and 500ms) in the test suite.

**No default policy.** A tenant without an approved revision gets no automatic escalations,
a startup warning per tenant, and `policy.status: NoApprovedPolicy` on the board. A software
default would stand in for a clinical decision nobody made (spec §6). Missed-deadline follow-up
still runs for staff-raised escalations.

**Licensing.** Every escalation and policy command is `[AlwaysAvailable]`. An unlicensed tenant
still escalates, and can still change its thresholds.

**Time.** Handlers take the time from `TimeProvider`; the monitor passes its evaluation time
into the commands it sends, which is what lets the tests run a monitor "four hours from now".
Every instant is stored and read back as UTC; date of birth is excluded, because it is a
calendar date.

## Auditing

`AuditBehaviour` writes an `AuditLogs` row for every command: actor, role, action, affected
aggregate, outcome, and error message on failure. This is the DSPT and DCB0129 evidence trail.

`DomainEvents` and `AuditLogs` are append-only: `BetsiDbContext.SaveChangesAsync` throws if any
code path updates or deletes a row in either (MVP-024).

The audit row is written after the handler commits, in its own `SaveChanges`, so a failure to
audit cannot roll back clinical work. A process crash between the two leaves one command
unaudited; the event log, which shares a transaction with the change itself, remains the
primary record.

## Data the system deliberately does not log

Domain events carry patient data. The structured logs do not: `LoggingBehaviour` records the
command name, tenant and actor only, and `LoggingOutboxPublisher` logs event metadata rather
than payloads. `ProblemDetailsExceptionHandler` returns exception text only outside
production, because an exception message can contain patient data or a connection string.

## Testing strategy

| Suite | Runs against | Covers |
|---|---|---|
| `Domain/` | Nothing — pure objects | Every state transition and every rejected transition |
| `Infrastructure/` | SQLite in memory | Transactional consistency, concurrency, JSON mapping, outbox drain |
| `Infrastructure/DependencyInjectionTests` | The real container | That every registration can actually be constructed |
| `Api/` | The real app via `WebApplicationFactory`, SQLite | Full journeys, tenant isolation, the RFC 9457 contract |
| `Api/EscalationEngineTests` | The real app, monitor run at chosen times | Policy change control, tiers raised once, follow-up on missed deadlines, board, audit trail and CSV, performance budgets |
| `Infrastructure/SqlServerMigrationTests` | SQL Server via Testcontainers | That migrations apply and provider-specific mappings are valid |
| `ControlPlane/` | SQLite, fake migrator and clock | Registry resolution, lifecycle, idempotency, failure recovery, licence auditing |
| `ControlPlane/SqlServerProvisioningTests` | SQL Server via Testcontainers | Real database creation, control-plane migration, rowversion, partial failure |
| `Licensing/` | Nothing — pure objects | Signatures, tampering, expiry and grace, clock rollback, command classification |
| `Api/SecurityTests` | The real app, tokens minted per run | Token validation, acting roles, reserved roles, permissions on every endpoint and command, audit of denials and reads |
| `Api/QueryAndCommandApiTests` | The real app | Episode detail, waiting board paging and filters, command envelope idempotency |
| `Api/IntegrationApiTests` | The real app, recording webhook receiver | Signed minimal webhook payloads, retry and dead letter, SSRF, HL7 and FHIR inbound, quarantine |
| `Api/OpenApiDocumentTests` | The real app | The published contract matches `docs/openapi/v1.json` |

A handful of mappings — filtered index predicates, `GETUTCDATE()` defaults, `nvarchar(max)` —
are applied only when the provider is SQL Server. That is what lets the same model build
against SQLite for fast tests while SQL Server remains the production target, and the
Testcontainers suite is what stops the two drifting.

## Decisions recorded

| ID | Decision | Notes |
|---|---|---|
| ADR-001 | Modular monolith, database per tenant | Isolation by connection, not query filter |
| ADR-002 | .NET 10, EF Core 10, MediatR 12 | MediatR pinned to 12.5.0, the last Apache-2.0 release |
| ADR-003 | CQRS with separate read models | Escalation board reads the tenant database directly with no projection lag; a projection store when load warrants it, same contract |
| ADR-004 | Transactional outbox, no external broker in MVP | `IOutboxPublisher` is the seam |
| ADR-005 | Tenant established by middleware, fails closed | Claims, with a Development-only header fallback |
| ADR-006 | Offline licensing with local validation | RSA-PSS (in the BCL) rather than Ed25519; restricted mode gates administration only |
| ADR-007 | Separate control-plane database; operator CLI, no admin HTTP API | Registry stores server profiles, not credentials |
| ADR-008 | Escalation policy in the tenant database, change-controlled, no default | Two-person approval, effective dates, restore-as-new-revision |
| ADR-009 | Follow-up exception as its own aggregate, one per missed deadline | Own owner, lifecycle and closure authority; survives late acknowledgement |
| ADR-010 | Provider-agnostic OIDC; permissions not roles; checks at endpoint and command | Claim names configurable; acting role required for multi-role tokens; System and Integration reserved |
| ADR-011 | Webhooks and inbound messages HMAC-signed; payload allowlists; quarantine | No external broker or FHIR SDK; minimal readers for the arrival/discharge feed only |
| ADR-012 | URL-path API versioning; checked-in OpenAPI contract | See API-VERSIONING.md |
