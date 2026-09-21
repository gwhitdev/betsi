# N4 clinical-safety scope

**Reviewed:** 2026-09-21
**Source:** `.github/ISSUES.md`, MVP-030–045
**Status terms:** implemented means code exists locally; verified means an automated test exercises it. Neither term is clinical approval.

| Criterion | Current evidence | State | Remaining work and pilot impact |
|---|---|---|---|
| MVP-030 age-based routing | `AgeBands`, age recorded on every observation, paediatric NEWS refusal, age filters | Partial, verified | No configurable paediatric pathway or paediatric template routing. Required before paediatric pilot use. |
| MVP-031 paediatric ranges | Paediatric observations explicitly return `NotValidatedForAge` | Deferred safely | No paediatric ranges, PECARN, or automatic alerts. A clinical safety officer must select and approve the reference before implementation. |
| MVP-032 safeguarding | Episode flag, staff command/API/UI, immediate escalation, immutable events/outbox | Partial, verified | Escalation has no explicit critical-priority field and there is no concern-resolution model distinct from escalation resolution. |
| MVP-033 trained staff | Episode assignment records verified staff identity/role and paediatric-training declaration; configurable pathway age; one atomic missing-skill alert; audit/API/UI and rollback/duplicate tests | Implemented locally, verified | Site must approve the pathway age, competence declaration process, and charge-nurse ownership before paediatric pilot use. A staff directory and administrator-managed competence registry remain outside this slice. |
| MVP-034 unaccompanied child | Configurable age, carer details/history, automatic safeguarding escalation, atomic failure test, API/UI | Implemented locally, verified | Site must approve the age threshold and safeguarding owner. |
| MVP-035 paediatric pain tools | Numeric, FACES and FLACC types; scale validation; age-based recommendation in domain | Partial, verified | UI does not yet guide the scale by age. Clinical review of eligibility remains open. |
| MVP-040 structured observations | Structured vital signs, ACVPU, pain, NEWS2 metadata, explicit source, separate SBAR sections, generic breathing/circulation/mobility findings, validation, API/UI/correction history and SQL Server migration | Implemented locally; domain/API/SQL Server/browser verified | Generic findings intentionally have no diagnostic rules or automatic triggers. NEWS2 scale 2 remains deliberately unsupported pending clinical approval. |
| MVP-041 deterioration | Staff command/API/UI, reason, immediate senior-clinician escalation, event/audit, atomic commit | Implemented locally, verified | Escalation summary does not copy the latest structured observation. Default response role needs site approval. |
| MVP-042 pain workflow | Score, scale, location, character and onset persist/display; configurable threshold raises one atomic analgesic-review alert | Partial, verified | Queue-priority projection is absent. Threshold and response role remain provisional pending site clinical approval. |
| MVP-043 episode timeline | Chronological observation history, correction markers, responsive episode page | Partial, browser verified | No type/date controls; deterioration and pain need stronger timeline highlighting. |
| MVP-044 observation audit | Append-only observations, correction chain, actor/time, audited reads, unique competing-correction guard | Implemented locally, verified | Performance evidence exists for board queries, not a representative long observation history. |
| MVP-045 risk flags | Safeguarding and deterioration are fixed-purpose flags | Open | Add typed free-form risk flags, severity, lifecycle, optional escalation policy, episode UI, audit, and tests. P2; may be deferred from an initial pilot only by an accountable owner. |

## Engineering sequence

1. Close the already modelled safeguarding/deterioration API and UI slice, including full-suite and browser regression checks.
2. Add pain detail fields and a site-owned analgesic-review threshold, keeping the threshold provisional until clinical review.
3. Add trained-staff competence and assignment as a separate aggregate/slice, followed by the paediatric missing-skill alert.
4. ✅ Add structured SBAR and other observation templates without inventing clinical required-field rules.
5. Add risk-flag lifecycle and then complete timeline filtering/highlighting.
6. Implement paediatric reference ranges only after the clinical reference and responsible reviewer are recorded in the hazard log.

The Welsh terminology review and manual screen-reader/device pass remain deferred by the user. They are still pilot acceptance work and are not represented as passed here.

## Local structured-template checks — 2026-09-21

The solution built with zero warnings; the new migration applied on real SQL Server and
round-tripped source, SBAR and assessment findings; `has-pending-model-changes` reported no
drift. The full .NET suite passed **504/504** with no skips, including domain/API validation,
append-only correction history, SQL Server concurrency and the checked-in OpenAPI contract.
The full Chromium suite passed **38/38**, including structured entry and correction. It found
and drove a fix for stale edit-form state that could save the original value during a correction.
Keycloak's volume and realm-import mounts were corrected in Compose; no volume was deleted.
This is engineering verification, not clinical acceptance.
