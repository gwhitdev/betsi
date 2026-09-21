# API reference

Base path `/api/v1`. All request and response bodies are JSON and all times are UTC. The
contract is published at `/openapi/v1.json` in every environment and checked in at
[`openapi/v1.json`](openapi/v1.json); interactive documentation is at `/swagger` in Development.
Versioning rules: [`API-VERSIONING.md`](API-VERSIONING.md). Within v1, fields and enum values
may be added: treat an unknown field as ignorable and an unknown enum value as "other".

## Authentication

Every endpoint except the health probes and `/openapi` requires credentials. A request without
them is 401 `UNAUTHENTICATED`.

The probes are anonymous because a load balancer and an orchestrator cannot hold credentials:
`/health/live` (the process is up; touches no database), `/health/ready` (this instance can
serve tenants) and `/health` (the aggregate). None of them discloses anything but a status and,
on `/health`, which named check failed.

Every response carries an `X-Correlation-Id`. Send your own to have it echoed back and recorded
against the request in this service's logs — useful for tracing a call that began in an EPR or
integration engine. It is length-capped at 100 characters — the width of the column a command
envelope stores it in — and one containing control characters is replaced rather than trusted.

**Bearer tokens (all environments).** An access token from the configured OIDC provider
(`Authentication:Jwt`). Tokens must be signed with RS256, PS256 or ES256, unexpired, and issued
by the configured issuer for the configured audience. Claims read, all configurable under
`Authentication:Claims`:

| Claim (default name) | Meaning |
|---|---|
| `betsi:tenant_id` | The tenant. Required. A request's `X-Betsi-Tenant` header, if sent, must match it (403 `TENANT_MISMATCH`). |
| `sub` | The user. A non-GUID subject is mapped to a stable id scoped by issuer. |
| `roles` | The user's roles. |
| `betsi:acting_role` | The role selected for this session, where the provider supports it (e.g. NHS CIS2). |

A token with several roles and no acting-role claim must name one with the
`X-Betsi-Acting-Role` header (else 403 `ACTING_ROLE_REQUIRED`), and it must be a role the token
holds. `System` and `Integration` cannot be claimed (403 `RESERVED_ROLE`).

**Development headers.** In Development only, `X-Betsi-Tenant`, `X-Betsi-Actor` and
`X-Betsi-Actor-Role` authenticate a request. They are ignored whenever a bearer token is present,
and the application refuses to start with them enabled elsewhere.

**Signed messages.** The inbound integration endpoint authenticates by HMAC signature instead
(see *Integrations*).

## Permissions

Endpoints and commands require permissions; roles grant them. A role not listed has none.

| Role | Permissions |
|---|---|
| Nurse, Staff Nurse, Nurse in Charge, Senior Clinician, Doctor, Consultant | register, care, discharge, queues, episodes.read, escalations raise/respond/read |
| Waiting-room Coordinator, Bed Manager | register, queues, episodes.read, escalations raise/respond/read; Bed Manager also locations |
| Receptionist | register, episodes.read, escalations.raise |
| Matron, Clinical Lead | register, care, discharge, episodes.read, escalations raise/respond/read, policy propose/decide; Matron also queues |
| Operations Manager, Site Manager | episodes.read, escalations respond/read, policy decide; Operations Manager also policy propose, locations |
| Site Administrator | policy propose, locations, webhooks, integration sources. **No patient data.** |
| *every staff role* | policy.read, license.read |
| Integration *(reserved)* | integration.ingest, register, discharge |

Refusals are 403 `FORBIDDEN` with the missing `permission`, and are recorded in the audit log
along with every read of patient data (episode, boards, escalation audit trail, quarantined
messages).

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
`instance`, `traceId` and a stable **`code`**. Branch on `code`; titles and details may change.
Codes are never renamed or reused within v1.

| Status | `code` | When | Extra fields |
|---|---|---|---|
| 400 | `TENANT_NOT_SPECIFIED` | Credentials carry no tenant | |
| 400 | `BAD_REQUEST` | Invalid query parameter or request | |
| 400 | `UNKNOWN_COMMAND_TYPE` | Command envelope names no known command | |
| 401 | `UNAUTHENTICATED` | Missing, expired or invalid credentials | |
| 401 | `INVALID_SIGNATURE` | Inbound message signature not verified | |
| 403 | `FORBIDDEN` | Role lacks the permission | `permission` |
| 403 | `ACTING_ROLE_REQUIRED`, `RESERVED_ROLE`, `TENANT_MISMATCH` | See *Authentication* | |
| 403 | `TENANT_SUSPENDED` | Tenant suspended | |
| 403 | `LICENSE_RESTRICTED` | Licence-gated operation in restricted mode | `feature`, `licenseStatus` |
| 404 | `NOT_FOUND`, `UNKNOWN_TENANT` | No such record in this tenant, or no such tenant | |
| 409 | `CONCURRENCY_CONFLICT` | The aggregate changed since you read it | `expectedVersion`, `actualVersion` |
| 409 | `CONFLICT`, `IDEMPOTENCY_IN_PROGRESS` | A conflicting change, or the same idempotency key still running | |
| 422 | `VALIDATION_ERROR` | Validation failed | `errors` — field name to messages |
| 422 | `INVALID_STATE_TRANSITION` | Not valid in the aggregate's current state | |
| 422 | `IDEMPOTENCY_KEY_REUSED` | Key already used for a different request | |
| 500 | `INTERNAL_ERROR`, `TENANT_ISOLATION` | Unexpected | `detail` only outside production |
| 503 | `TENANT_UNAVAILABLE` | Tenant not ready | |

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
| POST | `/episodes/{id}/carer-presence` | Record whether a carer is present; an unaccompanied child raises a linked safeguarding escalation atomically. |
| POST | `/episodes/{id}/staff-assignment` | Assign a clinician, record paediatric competence, and raise one linked skill-gap alert when required. |
| POST | `/episodes/{id}/safeguarding-concerns` | Record a safeguarding concern and create its assigned escalation atomically. |
| POST | `/episodes/{id}/deterioration-flags` | Record staff-observed deterioration and create its assigned escalation atomically. |
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

## Clinical observations

| Method | Path | Purpose |
|---|---|---|
| POST | `/episodes/{episodeId}/observations` | Record a structured observation or append-only correction. Requires `observations.record`. |
| GET | `/episodes/{episodeId}/observations` | Read the complete observation and correction history. Requires `observations.read`. |

The POST body may contain vital signs, pain score and scale, pain location, character and onset,
an explicit `source`, separate SBAR sections, breathing/circulation/mobility findings with
optional detail, notes, and `kind` (`Routine`,
`Triage` or `Concern`). A correction supplies `supersedesObservationId` and the observation's
`expectedVersion`; the original remains unchanged and the response identifies the new record.
The API returns a DTO so persistence details are not exposed. Adult NEWS2 is informational only,
and is unavailable for paediatric episodes or incomplete vital signs; it never raises an
escalation automatically. Observation and correction state, domain events and outbox records
commit in one transaction. Command audit records are saved separately afterwards; a crash between
the two saves can leave an unaudited command, while the transactional event record remains.

`source` is one of `NursingAssessment`, `MedicalReview`, `TriageAssessment`,
`ClinicalHandover` or `OtherClinicalAssessment`. SBAR is stored as four separately queryable
sections: situation, background, assessment and recommendation. Breathing, circulation and
mobility each use `NoConcern`, `Concern` or `UnableToAssess`, with a separate details field.
These generic findings carry no diagnostic meaning and never trigger an automatic escalation;
site-approved clinical meaning remains the responsibility of the recording clinician and local
procedure.
A failed command clears tracked changes before saving its failure audit so that the audit save
cannot accidentally persist a rejected correction. Use `POST /commands` with command type
`RecordObservation` and an idempotency key for safe retries; the direct observation POST does
not deduplicate repeated submissions.

A pain score requires `painScale`, `painLocation`, `painCharacter` and `painOnsetAt`. At or above
`Clinical:PainReviewThreshold` (default 5), recording a new assessment creates a `SystemAlert`
for `Clinical:PainReviewRole` in the same transaction. Correcting one high score to another does
not duplicate the alert. These threshold and ownership defaults are provisional site policy and
require clinical approval before real-patient use.

For patients younger than `Clinical:PaediatricPathwayAgeYears` (default 18), the episode records
the assigned clinician and whether paediatric training is declared. An untrained assignment, or
the first observation made without a trained assignee, creates one `SystemAlert` for
`Clinical:PaediatricSkillGapRole`. The episode marker prevents repeated observations from
creating duplicate alerts. A later trained assignment clears the marker but retains the existing
escalation and immutable history for review.

Scoring implements the adult oxygen-saturation Scale 1 table only. It does not establish
clinical eligibility for NEWS2 (including pregnancy or whether Scale 2 is required). This
limitation remains a clinical-use gate in H-05. Invalid numeric values and unsupported temperature
precision return no score when calling the calculator; the API rejects these inputs. Reference
and engineering boundary evidence: [N1 verification](N1-VERIFICATION.md).

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
Lead`, `Operations Manager`, `Site Manager` and `Matron`. The acting role comes from the
authenticated credentials (see *Authentication*), so these checks are enforced, not advisory.

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

Every card includes a patient summary (name, state, arrival, minutes since arrival). Reading the
board requires `escalations.read` and is recorded in the audit log.

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

## Episodes and the waiting board

| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/episodes/{id}` | episodes.read | Demographics, state, timings, escalations, open follow-ups. |
| GET | `/boards/waiting` | episodes.read | Waiting and awaiting-treatment patients, longest wait first. |

`/boards/waiting` filters: `locationId`, `state` (`Waiting` \| `AwaitingTreatment`),
`minWaitingMinutes`, `minAgeYears`, `maxAgeYears`; `pageSize` 1–200 (default 50). Pages are
keyset-paginated: pass the response's `nextCursor` as `cursor`; it is null on the last page.
Pages stay consistent while patients arrive and leave. Triage acuity is not yet modelled, so
there is no acuity filter.

## Command envelope

`POST /commands` submits any command by name, with retry safety. The resource endpoints above
remain the primary API.

```json
{
  "commandType": "BeginPatientTriage",
  "commandId": "7a1c…",
  "idempotencyKey": "tablet-17-000423",
  "correlationId": "ward-round-9f3",
  "expectedVersion": 1,
  "payload": { "patientEpisodeId": "3f2b…" }
}
```

→ `200 { "commandId", "commandType", "status": "Succeeded", "result": { "aggregateId", "version" }, "correlationId", "replayed": false }`

- `commandType` is the command name without `Command`. Monitor-only commands are not accepted.
- The same permissions, licence rules and validation apply as on the resource endpoints.
- With an `idempotencyKey`, a retry by the same actor with the same request returns the original
  result (`replayed: true`, header `Idempotent-Replayed: true`) without acting again. The key
  with a different request, or by another actor, is 422 `IDEMPOTENCY_KEY_REUSED`. A command that
  failed releases its key, so a corrected request can reuse it.
- Errors are problem details with `commandId` and `correlationId` extensions. The correlation
  id is also echoed in `X-Correlation-Id`. An envelope with no `correlationId` of its own takes
  the one this request already has, so every command is traceable whether or not the caller
  supplied an id.

## Webhooks

Site administrators subscribe HTTPS endpoints to events (permission webhooks.manage).

| Method | Path | Purpose |
|---|---|---|
| POST | `/webhooks/register` | `{ "url", "eventTypes": [...], "description"? }` → 201 with `secret`, **shown once** |
| GET | `/webhooks` | Subscriptions, without secrets |
| GET | `/webhooks/event-types` | Subscribable event types |
| POST | `/webhooks/{id}/rotate-secret` | New secret, shown once |
| POST | `/webhooks/{id}/deactivate` | Stop deliveries |
| GET | `/webhooks/{id}/deliveries?status=` | Recent deliveries and their state |
| POST | `/webhooks/deliveries/{id}/retry` | Retry a dead-lettered delivery |

Event types: `patient.arrived`, `patient.triage_started`, `patient.triage_completed`,
`patient.moved`, `patient.discharged`, `patient.cancelled`, `escalation.raised`,
`escalation.acknowledged`, `escalation.reassigned`, `escalation.escalated`,
`escalation.resolved`, `escalation.closed`, `escalation.follow_up_required`,
`escalation.follow_up_closed`.

```http
POST https://subscriber.example/betsi
Betsi-Signature: t=1789481234,v1=5f1c…
Betsi-Delivery-Id: 1b0e…
Betsi-Event-Id: 88c2…
Betsi-Event-Type: escalation.raised

{ "id": "88c2…", "type": "escalation.raised", "schemaVersion": 1, "occurredAt": "…Z",
  "tenantId": "…", "aggregateType": "Escalation", "aggregateId": "…", "aggregateVersion": 1,
  "actorRole": "System",
  "data": { "patientEpisodeId": "…", "responsibleRole": "Waiting-room Coordinator", "tierLevel": 1, … } }
```

**Verify every delivery**: compute HMAC-SHA256 with the secret over `"{t}.{raw body}"`, compare
to `v1` in constant time, and reject a `t` more than five minutes from now. Deduplicate on
`Betsi-Event-Id` — delivery is at least once.

**Payloads carry no patient identifiers** — no names, dates of birth, NHS numbers or free-text
notes. Use the episode API, with your own credentials, if you need them.

Any non-2xx response or a 10-second timeout is retried with exponential backoff (30s, 1m, 2m…
capped at an hour, with jitter); after 8 attempts the delivery is dead-lettered. Redirects are not
followed. Outside Development, URLs must be HTTPS and must not resolve to private, loopback or
link-local addresses — checked at registration and again at every connection.

## Integrations (HL7 v2 and FHIR R4)

Site administrators register a source system (permission integrations.manage):

| Method | Path | Purpose |
|---|---|---|
| POST | `/integrations/sources` | `{ "name", "format": "Hl7v2" \| "FhirR4" }` → 201 with `inboundPath` and `secret`, shown once |
| GET | `/integrations/sources` | Sources |
| POST | `/integrations/sources/{id}/rotate-secret` | New secret |
| POST | `/integrations/sources/{id}/deactivate` | Refuse further messages |
| GET | `/integrations/sources/{id}/messages?status=Quarantined` | Message metadata, no bodies |
| GET | `/integrations/messages/{id}` | One message with its quarantined body — **patient data**; permission episodes.read, audited |

The source then posts each message to `POST /integrations/inbound/{tenantId}/{sourceId}` with:

- `Betsi-Signature: t=…,v1=…` — as for webhooks, signed with the source's secret, within five minutes;
- `Betsi-Message-Id` — the sender's unique id. Resending the same id is safe and returns the original outcome.

| Format | Arrival | Discharge | Cancellation |
|---|---|---|---|
| HL7 v2.x ADT (`application/hl7-v2`) | A01, A04 | A03 | A11 |
| FHIR R4 Encounter in a Bundle, Patient in the Bundle or contained (`application/fhir+json`) | `arrived`, `triaged`, `in-progress` | `finished` | `cancelled`, `entered-in-error` |

Arrival reads name, date of birth and NHS number (HL7 PID-3 with type `NH` or an `NHS`
authority; FHIR identifier system `https://fhir.nhs.uk/Id/nhs-number`) and links the visit
identifier (HL7 PV1-19; FHIR `Encounter.identifier`) to the new episode, which discharge and
cancellation then find. The same visit announced twice is one episode.

A message that cannot be processed — unsupported type, missing field, unknown visit, invalid NHS
number — is **quarantined**, not discarded: it is stored with its error for review, and still
acknowledged (HL7 `MSA|AE`, FHIR `"status": "Quarantined"`) so the sender does not retry a
message that will never succeed. HL7 senders get an HL7 ACK; FHIR senders get
`202 { "messageId", "status", "episodeId", "error", "duplicate" }`.

Messages act as the reserved **Integration** role, which may only register, discharge and
cancel, and every action is audited against the source's id.

## Not yet implemented

Dashboard UI and real-time push are Phase H. Break-glass access, service-account scopes for
addons, a .NET client SDK (MVP-069) and a generic REST polling adapter (MVP-067) are not built.
See `IMPLEMENTATION_PLAN.md`.
