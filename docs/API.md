# API reference

Base path `/api/v1`. All request and response bodies are JSON. Interactive documentation is
served at `/swagger` in Development.

## Every request names a tenant

Requests are rejected with 400 if no tenant can be resolved, 404 if the tenant is not served
by this instance, 403 if it has been suspended, and 503 (with `Retry-After`) if it is not ready
— provisioning, failed, or on an outdated schema. `/health`, `/swagger` and `/openapi` are
exempt. The reason a tenant is unavailable is never returned; operators see it in the logs and
via `tenants list`.

| Source | Header (Development only) | Claim (all environments) |
|---|---|---|
| Tenant | `X-Betsi-Tenant` | `betsi:tenant_id` |
| Actor | `X-Betsi-Actor` | `sub` / `NameIdentifier` |
| Actor role | `X-Betsi-Actor-Role` | `role` |

Header-supplied tenants are unauthenticated. The application refuses to start with
`TenantResolution:AllowHeaderFallback` enabled outside Development.

## Command results and versions

Every state-changing endpoint returns:

```json
{ "aggregateId": "3f2b…", "version": 3 }
```

`version` is the value to send as `expectedVersion` on the next command against that
aggregate. There is no success flag: a command that did not succeed returns a 4xx or 5xx with
a problem document, never a 200.

## Failures (RFC 9457)

Responses use `application/problem+json` and carry `type`, `title`, `status`, `detail`,
`instance` and `traceId`.

| Status | When | Extra fields |
|---|---|---|
| 400 | No tenant on the request | |
| 403 | Tenant suspended | |
| 403 | Licence-gated operation in restricted mode (`type` ends `/license-restricted`) | `feature`, `licenseStatus` |
| 404 | Unknown tenant, or an aggregate that does not exist in this tenant | |
| 409 | The aggregate changed since you read it | `expectedVersion`, `actualVersion` |
| 422 | Validation failed | `errors` — a map of field name to messages |
| 422 | The operation is not valid in the aggregate's current state | |
| 500 | Unexpected | `detail` only outside production |
| 503 | Tenant not ready | |

A 404 is returned rather than 403 when another tenant's record is named, so that callers
cannot probe for the existence of records they cannot see.

## Patients

| Method | Path | Purpose |
|---|---|---|
| POST | `/patients/register` | Register an arrival, opening an episode. 201. |
| POST | `/patients/{id}/triage/begin` | Begin triage. |
| POST | `/patients/{id}/triage/complete` | Complete triage; patient awaits treatment. |
| POST | `/patients/{id}/treatment/begin` | Move into treatment at a location. |
| POST | `/patients/{id}/discharge` | Discharge, ending the episode. |
| POST | `/patients/{id}/cancel` | Cancel, e.g. left without being seen. |

The episode state machine:

```
Waiting ──▶ InTriage ──▶ AwaitingTreatment ──▶ InTreatment ──▶ Discharged
   │            │                │                   │
   └────────────┴────────────────┴───────────────────┴──────▶ Cancelled
```

Anything else is a 422. `Waiting` and `AwaitingTreatment` are the states that accrue waiting
time against the escalation thresholds.

### Register

```http
POST /api/v1/patients/register
{ "firstName": "Gwen", "lastName": "Jones", "dateOfBirth": "1962-04-19", "nhsNumber": "9434765919" }
```

`nhsNumber` is optional — unidentified patients are routine — but when supplied it must be ten
digits with a valid modulus-11 check digit, so that a transposed digit is caught at entry
rather than attaching the record to the wrong person.

### Advance an episode

```http
POST /api/v1/patients/{id}/triage/begin
{ "expectedVersion": 1 }
```

The id in the path is authoritative; a conflicting id in the body is ignored.

## Escalations

| Method | Path | Purpose |
|---|---|---|
| GET | `/escalations/board?historyDays=7` | The escalation board (below). |
| GET | `/escalations/{id}/audit?format=json\|csv` | Every transition of the escalation and its follow-up exceptions. |
| POST | `/escalations/waiting-time` | Raise an escalation by hand. 201. 15-minute deadline. |
| POST | `/escalations/{id}/acknowledge` | Confirm someone has taken it on. Repeating it is harmless. |
| POST | `/escalations/{id}/resolve` | Record that the issue was dealt with. Repeating it is harmless. |
| POST | `/escalations/{id}/reassign` | Hand to another role: `{ "responsibleRole", "reason" }`. |
| POST | `/escalations/follow-ups/{id}/close` | Close a follow-up exception: `{ "outcome", "reviewNotes" }`. |

```
             ┌───────────── reassign (new role, new deadline) ─────────────┐
             ▼                                                              │
Created ──▶ Acknowledged ──▶ Resolved                                       │
   │              │                                                         │
   │              └──▶ Escalated ──▶ Resolved                               │
   │                                                                        │
   └─ deadline missed ──▶ ManualFollowUp ──▶ Acknowledged / Resolved ───────┘
                              │
                              └─ raises a FollowUpException (Open ──▶ Closed)
```

**Automatic escalation.** Once a site has an approved policy (next section), the waiting-time
monitor raises one escalation per patient per tier as each threshold passes, for patients in
`Waiting` or `AwaitingTreatment`. Each carries the tier, the policy revision, how long the
patient had waited, and the recommended action. The monitor runs every
`EscalationMonitor:Interval` (15s).

**Missed deadlines.** When an escalation in `Created` passes its acknowledgement deadline, the
monitor raises a follow-up exception and moves the escalation to `ManualFollowUp`, in one
transaction. There is one exception per missed deadline, so an escalation that is reassigned
and misses its new deadline raises a second. The escalation can still be acknowledged and
resolved; the exception stays open until its owner (the policy's `followUpOwnerRole`) or a
supervisory role closes it with an outcome: `AcknowledgedLate`, `Reassigned`,
`NoFurtherActionRequired`, `IncidentReported` or `Other`. Staff-raised escalations are followed
up the same way, even with no policy in force.

Supervisory roles, for approving policy and closing any follow-up exception, are `Clinical
Lead`, `Operations Manager`, `Site Manager` and `Matron`. Until Phase G roles come from an
unauthenticated header, so this is a safeguard against mistakes, not a security control.

Raising an escalation requires a `responsibleRole`; an escalation nobody owns is the failure
mode the inspection report describes. Raising one for a patient who does not exist is a 404.

Acknowledge, resolve and reassign take no `expectedVersion`: they are sent from dashboards
that do not track versions, and the aggregate still rejects transitions that do not make sense.

### The board

Read live from the tenant database, so staleness is however often the client polls.

| Field | Contents |
|---|---|
| `generatedAt`, `source` | When and where the data came from. All times are UTC (`Z`). |
| `policy.status` | `InForce`, `Disabled`, or `NoApprovedPolicy` — show this prominently: with no policy, nothing is escalated automatically. |
| `metrics` | Active, awaiting acknowledgement, overdue, open follow-up exceptions, median minutes to acknowledge (over `historyDays`), raised in the last 24h by trigger and tier. |
| `awaitingAcknowledgement` | Unacknowledged escalations, soonest deadline first, with `acknowledgementOverdue`. |
| `active` | Every open escalation. |
| `followUpExceptions` | Open follow-up exceptions with owner and missed deadline. |
| `history` | Resolved and closed escalations within `historyDays` (1–30). |
| `truncated` | True if any list hit 500 items. |

Every card includes a patient summary (name, state, arrival, minutes since arrival). **This is
patient data on an unauthenticated read endpoint until Phase G.**

### Audit trail

`GET /escalations/{id}/audit` returns the escalation's events and those of its follow-up
exceptions from the append-only event log, oldest first: event type, version, time, actor and
role, and the event's details. `?format=csv` downloads the same data. Cells that begin with a
spreadsheet formula character are prefixed with `'` so an exported note cannot execute when
opened. The event and audit tables reject updates and deletes.

## Escalation policy

A site's waiting-time thresholds, change-controlled (spec §9). **There is no default.**

| Method | Path | Purpose |
|---|---|---|
| GET | `/escalation-policy` | The revision in force and every revision with its decision history. |
| GET | `/escalation-policy/revisions/{n}` | One revision. |
| POST | `/escalation-policy/preview` | How many patients waiting now each proposed tier would apply to. Changes nothing. |
| POST | `/escalation-policy/proposals` | Propose a revision. 201. |
| POST | `/escalation-policy/proposals/restore` | Propose a revision restoring an earlier approved one: `{ "revision", "reason" }`. |
| POST | `/escalation-policy/{id}/approve` | `{ "expectedVersion", "reason", "effectiveFrom"? }` |
| POST | `/escalation-policy/{id}/reject` | `{ "expectedVersion", "reason" }` |
| POST | `/escalation-policy/{id}/withdraw` | A proposal, or an approval not yet in effect. |

```http
POST /api/v1/escalation-policy/proposals
{
  "enabled": true,
  "tiers": [
    { "thresholdMinutes": 240, "responsibleRole": "Waiting-room Coordinator", "acknowledgementDeadlineMinutes": 30, "recommendedAction": "Review clinical status; consider reassessment" },
    { "thresholdMinutes": 360, "responsibleRole": "Nurse in Charge",          "acknowledgementDeadlineMinutes": 30, "recommendedAction": "Decide on admission; escalate to bed management" },
    { "thresholdMinutes": 480, "responsibleRole": "Bed Manager",              "acknowledgementDeadlineMinutes": 30, "recommendedAction": "Escalate to site leadership" }
  ],
  "followUpOwnerRole": "Operations Manager",
  "reason": "Agreed at ED governance, 10 September"
}
```

Rules, enforced by the domain and returned as 422:

- 1–5 tiers, thresholds strictly increasing between 15 minutes and 72 hours, deadlines 5–240
  minutes, each with a role and a recommended action. Out-of-order thresholds are refused, not
  sorted: they usually mean a typo.
- `enabled: false` turns automatic escalation off deliberately, and must have no tiers.
- Proposer and approver must both be identified users (`X-Betsi-Actor`), and must be different
  people. Approving, rejecting, or withdrawing an approval requires a supervisory role.
- `effectiveFrom` may be in the future, never the past. The policy in force is the approved
  revision with the latest `effectiveFrom` that has arrived.
- A revision in force cannot be withdrawn; approve a new one. Nothing edits a revision.

Two proposals racing for the same revision number: the second gets a 409.

## Locations and queues

| Method | Path | Purpose |
|---|---|---|
| POST | `/locations` | Create a bay, cubicle or waiting area. 201. Requires licence feature `core`. |
| POST | `/queues` | Create a queue for a location. 201. Requires licence feature `core`. |
| POST | `/queues/{id}/patients` | Add a patient to the end of a queue. |

Capacity must be greater than zero. A patient cannot be queued twice — a duplicate would show
the same person waiting in two places and double-count them against the thresholds.

## Licence

| Method | Path | Purpose |
|---|---|---|
| GET | `/license` | The calling tenant's licence status, mode and usable features |

```json
{
  "status": "Valid", "mode": "Full", "licenseId": "4cd2…", "features": ["core"],
  "expiresAt": "2036-09-14T23:59:59Z", "gracePeriodEndsAt": "2036-10-14T23:59:59Z",
  "evaluatedAt": "2026-09-14T15:50:35Z"
}
```

In restricted mode `features` is empty and `POST /locations` and `POST /queues` return 403.
Every patient and escalation endpoint keeps working.

## Not yet implemented

The waiting board and the dashboard UI are Phase H; the escalation board and policy endpoints
above are the only read side so far. Authentication, authorisation and API versioning beyond the `/v1` path
segment are Phase G. See `IMPLEMENTATION_PLAN.md`.
