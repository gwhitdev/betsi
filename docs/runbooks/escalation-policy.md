# Runbook: setting a site's escalation policy

For site administrators and clinical leads bringing a site onto automatic waiting-time
escalation (MVP-020), and changing it afterwards.

> **A new site has no policy, and therefore no automatic escalation.** The escalation board
> shows `NoApprovedPolicy` and the service logs a warning for the tenant at startup. This is
> deliberate: thresholds are a local clinical decision and there is no safe software default.
> Missed-acknowledgement follow-up still applies to escalations raised by hand.

All examples use Development headers. In a deployed environment the tenant, actor and role come
from the signed-in user once Phase G lands; until then, header-based access is Development only.

---

## 1. Agree the thresholds

Before touching the system, record in the site's clinical governance minutes, for each tier:

| | Example (spec §2) |
|---|---|
| Minutes since arrival | 240 / 360 / 480 |
| Responsible role | Waiting-room Coordinator / Nurse in Charge / Bed Manager |
| Minutes to acknowledge | 30 / 30 / 30 |
| Recommended action | Review clinical status… / Decide on admission… / Escalate to site leadership… |

And who reviews missed acknowledgements (the **follow-up owner**), e.g. Operations Manager.

The roles must match the role names staff sign in with. Escalations are assigned by role name.

## 2. Preview against today's department

```bash
curl -X POST localhost:5080/api/v1/escalation-policy/preview \
  -H 'X-Betsi-Tenant: <tenant>' -H 'X-Betsi-Actor: <your id>' -H 'X-Betsi-Actor-Role: Site Administrator' \
  -H 'Content-Type: application/json' \
  -d '[{"thresholdMinutes":240,"responsibleRole":"Waiting-room Coordinator","acknowledgementDeadlineMinutes":30,"recommendedAction":"Review"}]'
```

`wouldBeNewlyEscalated` is how many escalations the tier would raise **within 15 seconds of
approval**. On a busy day that can be dozens at once, each with a 30-minute deadline. Consider
approving with an `effectiveFrom` at a quiet time, and tell the teams concerned first.

## 3. Propose

`POST /api/v1/escalation-policy/proposals` with the tiers, `followUpOwnerRole` and a `reason`
that cites the governance decision. See `docs/API.md` for the body. The response gives the
proposal's `aggregateId` and `version`.

## 4. Approve — a different person, in a supervisory role

Approval must come from someone other than the proposer, with one of: Clinical Lead,
Operations Manager, Site Manager, Matron.

```bash
curl -X POST localhost:5080/api/v1/escalation-policy/<id>/approve \
  -H 'X-Betsi-Tenant: <tenant>' -H 'X-Betsi-Actor: <approver id>' -H 'X-Betsi-Actor-Role: Clinical Lead' \
  -H 'Content-Type: application/json' \
  -d '{"expectedVersion":1,"reason":"Approved at ED governance 10/09","effectiveFrom":"2026-09-16T07:00:00Z"}'
```

Omit `effectiveFrom` to take effect immediately. It cannot be in the past.

## 5. Verify

`GET /api/v1/escalations/board` → `policy.status` is `InForce` with the expected `revision`
(once `effectiveFrom` has passed). The service log shows no "no enabled escalation policy"
warning for the tenant on its next evaluation.

---

## Changing thresholds

Propose a new revision and approve it the same way. The old revision stays on record. Patients
already escalated at a tier are not escalated again at that tier under the new revision.

## Rolling back

```bash
POST /api/v1/escalation-policy/proposals/restore   { "revision": 3, "reason": "Revision 4 caused alert fatigue" }
```

This proposes a new revision with revision 3's content. It still needs approval.

## Turning automatic escalation off

Propose `{ "enabled": false, "tiers": [], "followUpOwnerRole": "…", "reason": "…" }` and
approve it. The board then shows `Disabled`. There is no way to "delete" the policy in force:
the site always has an explicit, approved position.

## Withdrawing a mistake

A proposal, or an approval whose `effectiveFrom` has not yet arrived, can be withdrawn with
`POST /api/v1/escalation-policy/<id>/withdraw`. Once in effect, replace it with a new revision.

---

## Reviewing follow-up exceptions

Open exceptions are on the board under `followUpExceptions`. Each is one missed
acknowledgement deadline. Close it once reviewed:

```bash
POST /api/v1/escalations/follow-ups/<id>/close
{ "outcome": "IncidentReported", "reviewNotes": "Datix W123456; coordinator in resus, no cover" }
```

Only the follow-up owner role or a supervisory role can close one. Acknowledging the escalation
late does not close the exception — the review of why it was missed still has to happen.

## If escalations are not appearing

1. Board `policy.status` — `NoApprovedPolicy` or `Disabled` explains it.
2. Is the patient `Waiting` or `AwaitingTreatment`? Time in triage or treatment does not count.
3. Service log for `Could not raise … escalation` errors; each is retried every 15 seconds.
4. `EscalationMonitor:Enabled` must not be `false`; a warning is logged at startup if it is.
5. Is the tenant available (`tenants list`)? Suspended and not-ready tenants are not evaluated.
