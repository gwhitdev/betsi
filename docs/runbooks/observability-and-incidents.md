# Runbook: observability, alerting and incidents

What this service emits, what to alert on, and what to do when an alert fires (MVP-109,
MVP-110). Deployment is [`deployment.md`](deployment.md); data recovery is
[`backup-and-restore.md`](backup-and-restore.md).

---

## 1. What is emitted

| Signal | Where it goes | Shape |
|---|---|---|
| Logs | stdout | Newline-delimited JSON outside Development; rendered text in Development and for operator commands |
| Traces | OTLP → `Observability:OtlpEndpoint` | ASP.NET Core requests, outbound HTTP, and a span per command |
| Metrics | OTLP → the same endpoint | The table in §3, plus ASP.NET Core, HttpClient and .NET runtime instrumentation |

With no OTLP endpoint configured, nothing is exported. Traces and metrics are still recorded
in-process, so a collector sidecar added later needs no application change.

### Correlation

Every request carries a correlation id, echoed back in `X-Correlation-Id`. A caller-supplied one
is honoured — so a trace can be followed from the EPR or integration engine that started it —
after being capped at 128 characters and checked for control characters, because it is
attacker-controlled text that reaches an operator's console and the log store's query language.
Anything suspicious is discarded and a fresh id is used.

Correlation id, trace id and span id are on every log line the request produces. To follow one
incident: find the id in the log store, then query traces for the same id.

### What is *not* in the logs

No patient name, date of birth, NHS number, or free-text note. The command pipeline logs the
command name, tenant, actor id and actor role, and nothing from the command body. Metrics carry
tenant ids and command names, never a patient identifier: a metrics store has no audit trail and
a long retention, which makes it the wrong place for clinical data.

Patient data is in the tenant database and in the audit log, where reads are themselves audited.
When an incident needs the clinical detail, go there — with the authorisation that requires.

## 2. Dashboards

Four panels answer most questions:

1. **Request rate and error rate** by route — `http.server.request.duration` count and status.
2. **Latency** p50/p95/p99 by route. The budget is median < 150ms, p95 < 500ms, p99 < 1000ms.
3. **Event lag** — `betsi.outbox.lag` p95. How long an event waited before publication, which is
   what says whether a webhook subscriber is seeing escalations in time.
4. **Escalations raised** per tenant — `betsi.escalations.raised`, split by trigger. A department
   that stops raising escalations has either had a quiet night or a broken monitor, and the
   difference matters.

## 3. Metrics this service publishes

| Metric | Type | Tags | Says |
|---|---|---|---|
| `betsi.commands` | Counter | command, tenant, outcome | Throughput and the error rate of writes |
| `betsi.command.duration` | Histogram (ms) | command, tenant, outcome | Latency as the caller experienced it, including validation and authorisation |
| `betsi.escalations.raised` | Counter | tenant, trigger | The engine that is the reason the product exists is working |
| `betsi.outbox.messages` | Counter | tenant, outcome (`published`, `failed`, `dead-lettered`) | Whether events are getting out |
| `betsi.outbox.lag` | Histogram (s) | tenant | Event lag |
| `betsi.webhook.deliveries` | Counter | tenant, outcome | Whether subscribers are actually receiving |

## 4. Alerts

| Alert | Condition | Severity | First action |
|---|---|---|---|
| Service down | `/health/ready` failing on every instance for 2 minutes | Page | §5.1 |
| Error rate | 5xx > 1% of requests over 5 minutes | Page | §5.2 |
| Latency | p95 > 500ms over 10 minutes | Ticket | §5.3 |
| Tenant unavailable | A tenant not `Available` for 5 minutes | Page | §5.4 |
| Event lag | `betsi.outbox.lag` p95 > 60s over 10 minutes | Page | §5.5 |
| Dead letters | Any `betsi.outbox.messages{outcome="dead-lettered"}` | Ticket | §5.5 |
| Webhook failures | `betsi.webhook.deliveries{outcome="failed"}` > 10% over 15 minutes | Ticket | §5.6 |
| Licence expiring | Licence status `Grace` | Ticket | `license status`, then issue a renewal |
| Clock rollback | `ClockRollbackDetected` on any tenant | Ticket | Check NTP on the host; the licence validator has refused to move its high-water mark back |
| Backup failed | `BackupTenant` audited `Failure`, or no `Success` in 26 hours | Page | [`backup-and-restore.md`](backup-and-restore.md) |
| Certificate expiry | TLS or backup-encryption certificate within 30 days of expiry | Ticket | Renew; a backup certificate that expires cannot be replaced retrospectively for old backups |

Every alert links to this runbook. An alert that does not say what to do next is a pager that
trains people to ignore it.

## 5. Responses

### 5.1 Service down

1. `/health` on one instance — it names which check failed.
2. If the tenant-database check failed, the database is the problem, not the service. Check the
   SQL Server, then the credentials the secret store is handing over.
3. If the process is not starting, read the first ten lines of its log. Start-up refuses loudly
   and by name: an unresolved `secret:` reference, an ephemeral key ring outside Development,
   header-based tenant resolution enabled outside Development, or missing authentication
   configuration all stop the service on purpose.
4. Roll back — [`deployment.md`](deployment.md) §6 — if the deployment was recent.

### 5.2 Error rate

Group 5xx by route and by tenant. One tenant means a database or a schema problem
(`tenants list` shows availability and schema version); one route means a code path; everything
means the database or the deployment.

### 5.3 Latency

Waiting-board and escalation-board queries are index-backed and keyset-paged. Sudden latency is
usually the database: check for blocking, and for a tenant whose statistics are stale after a
restore. The command histogram splits by command, so compare a write path against a read path
before assuming the whole service is slow.

### 5.4 A tenant is unavailable

`tenants list` gives the reason:

- **Suspended** — deliberate. Check the audit log for who and why.
- **Schema older than this build** — `tenants migrate --id <guid>`. Almost always a deployment
  that skipped its migration step.
- **Server profile not configured** — this instance is missing `Tenancy:DatabaseServers:<profile>`.
  Check the secret mount and restart.
- **Provisioning/Failed** — re-run `tenants provision`; it is idempotent and resumes.

### 5.5 Event lag or dead letters

Events are written in the same transaction as the aggregate, so lag is delivery, never loss: the
domain event is in the tenant database whatever happens next. Check the outbox drain — one
tenant's unreachable database stalls only that tenant.

A dead-lettered message has exceeded its attempts and is marked processed so the queue behind it
drains. **It was never delivered.** Find it in `OutboxMessages` by `LastError`, decide with the
subscriber whether it matters, and replay if it does.

### 5.6 Webhook failures

Delivery backs off and eventually dead-letters per subscription. A single failing subscriber
does not affect others. Check the subscriber's endpoint first; then confirm the key ring has not
changed — if the Data Protection key ring was lost, every stored secret became unreadable and
every subscription must be re-registered ([`deployment.md`](deployment.md) §4).

## 6. Incidents

1. **Declare.** Say in the channel that there is an incident, and who is running it.
2. **Mitigate before diagnosing.** Roll back, or suspend one tenant, before finding the cause.
3. **Record times.** Detection, mitigation, resolution. They are the recovery-time evidence for
   DSPT and the only honest input to a review.
4. **Clinical safety first.** If patients may have been affected — escalations not raised, a
   board showing stale waiting times — tell the clinical safety officer *during* the incident,
   not in the write-up. That is a DCB0129 obligation, and it has a reporting clock.
5. **Data breach**: if patient data may have been disclosed, the Caldicott Guardian and the DPO
   are informed the same day. The ICO clock is 72 hours and starts at awareness, not at
   confirmation.
6. **Postmortem within five working days**, blameless. What happened, what the effect was, why it
   was possible, and what changes. Systems are what fail; people are what notices. A postmortem
   naming a person as a cause is a postmortem that has stopped early.

## 7. On call

A weekly rotation, primary and secondary, handed over on a fixed day with a written summary of
anything still open. Escalate to the clinical safety officer for anything with a patient-safety
dimension, and to the service owner for anything lasting more than an hour.

**This is a template, not a rota.** A pilot site needs real names, a real paging tool and an
agreed response time before it holds real patient data.
