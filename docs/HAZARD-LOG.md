# Clinical risk management: hazard log

**Standard**: DCB0129 (Clinical Risk Management: its Application in the Manufacture of Health IT
Systems).
**Status**: ⚠ **PROVISIONAL.** Kept by the developer. No clinical safety officer is appointed,
and every clinical judgement below is the developer's own.
**Opened**: 2026-09-17. **Last reviewed**: 2026-09-20 (engineering evidence review only).

---

## What this document is, and is not

DCB0129 requires a manufacturer of health IT to appoint a **clinical safety officer** — a
registered clinician — who owns the hazard log, judges each risk, and signs the clinical safety
case. **There is no such person on this project.**

This log exists because the alternative was worse. Leaving Phase F blocked on an appointment
that may never come meant the clinical features were not built, so the hazards they carry were
never written down either. A log kept by an engineer is not compliance; it is an honest record
of what was built, what could go wrong, and which controls are in the code — ready for a
clinician to challenge.

**No entry here is signed off. Nothing in this system may be used with real patients until a
clinical safety officer has reviewed every hazard, accepted or rejected each control, and issued
a clinical safety case report.** Where a judgement needed making to write the code, it was made
conservatively and is flagged 🔍 for that review.

Severity and likelihood use the DCB0129 tables. Risk = severity × likelihood, as its matrix.

---

## Control evidence status

**Planned** means required behaviour is not integrated. **Implemented, unverified** means code
exists but the required acceptance evidence is missing. **Historically verified** refers only
to dated checks in [implementation status](../IMPLEMENTATION_STATUS.md); it is not a fresh
result for the uncommitted clinical work or clinical approval. All residual likelihoods below
are provisional estimates, not demonstrated reductions in risk.

| Hazards | Engineering evidence and remaining work |
|---|---|
| H-01/H-02 | Delivered escalation engine; historical checks in [Phase E](../IMPLEMENTATION_STATUS.md#phase-e--escalation-engine-mvp-020025). N3 still needs missing screen workflows; N5 needs site response acceptance. |
| H-03/H-11 | API/security and UI-session tests are supplemented by [N2 real-browser isolation, role-refusal and authentication checks](N2-VERIFICATION.md). Human/site acceptance remains open. |
| H-04 | Observation persistence/correction API tested for episode and tenant boundaries, permissions, retries, competing SQL Server writers and injected database failure. Structured source, SBAR and assessment templates round-trip through correction history and SQL Server. See [N1 evidence](N1-VERIFICATION.md). Patient-identifier confirmation in the entry UI and clinical review remain open. |
| H-05/H-06 | Reference-based Scale 1 boundary, age, missing/invalid-input tests now exist; see [N1 evidence](N1-VERIFICATION.md). Clinical eligibility and sign-off remain unresolved; observation UI is N3 work. |
| H-07/H-08 | Other clinical handlers/configuration exist, but their end-to-end verification and workflows remain N4/N3 work. No clinical control claim is signed off. |
| H-09 | Historical operational checks are recorded in implementation status. Representative deployed recovery and site fallback procedures remain N5 work. |
| H-12 | Structured pain details and atomic threshold alerts are verified locally; threshold, owner and queue-priority policy require clinical approval. |
| H-10 | [LiveBoard](../Betsi/UI/Components/LiveBoard.razor) uses the browser's authenticated hub connection. [N2 checks](N2-VERIFICATION.md) cover disconnect warnings, missed-change reconciliation, hub retry and actual ticket expiry; physical tablet sleep/wake review is deferred. |

## Hazards

### H-01 A patient waits past a clinically significant threshold without anyone being told

| | |
|---|---|
| **Cause** | No escalation policy approved for the site; the monitor stops; the board is not looked at |
| **Effect** | Deterioration unnoticed — the failure the Ysbyty Glan Clwyd inspection documented |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | Waiting-time monitor evaluates every 15s per tenant. A site with no approved policy is shown a prominent warning on the dashboard rather than an empty board. Escalations are raised once per patient per tier, enforced by a unique index. Failures are logged and retried on the next tick. The `betsi.escalations.raised` metric makes a department that stops raising them visible |
| **Residual likelihood** | Low |
| 🔍 | **The thresholds are the site's, not ours.** The system ships no default policy on purpose: a wrong default is worse than an obvious absence. A reviewer must confirm that refusing to escalate at all, rather than escalating on a guessed default, is the safer failure |

### H-02 An escalation is raised and nobody acknowledges it

| | |
|---|---|
| **Cause** | The responsible role is absent, busy, or does not see the board |
| **Effect** | The escalation is recorded and has no effect — the appearance of safety without it |
| **Severity** | Major |
| **Initial likelihood** | Medium |
| **Controls** | Every escalation carries an acknowledgement deadline. A missed deadline raises a follow-up exception owned by a named role, which cannot be closed without an outcome and notes. Overdue rows are marked in words as well as colour |
| **Residual likelihood** | Low |
| 🔍 | The default follow-up owner when no policy names one is Operations Manager. A reviewer must confirm that is the right role at a site |

### H-03 A clinician sees another department's patients

| | |
|---|---|
| **Cause** | A defect in tenant resolution |
| **Effect** | Confidentiality breach; a clinical decision made on the wrong patient's data |
| **Severity** | Catastrophic |
| **Initial likelihood** | Low |
| **Controls** | Database per tenant; the connection is chosen per request from a verified claim. Tenant is never read from an unauthenticated header outside Development, and the service refuses to start with header fallback enabled elsewhere. API isolation, UI-session and real-browser two-tenant checks cover both boards and live updates; see N2 verification. Site identity configuration and clinical acceptance remain open |
| **Residual likelihood** | Very low |

### H-04 An observation is recorded against the wrong patient

| | |
|---|---|
| **Cause** | A user selects the wrong episode; an integration maps an identifier wrongly |
| **Effect** | Clinical decisions made on another patient's observations |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** the [handler](../Betsi/Application/Commands/Handlers/ClinicalHandlers.cs) checks episode association; persistence retains history and commits corrections atomically through `IUnitOfWork`. Source, SBAR and generic breathing/circulation/mobility findings remain visible in correction history. API failure and SQL Server concurrency tests are linked in [N1 evidence](N1-VERIFICATION.md). The episode UI shows identifiers and requires explicit identity confirmation before saving; original and corrected observations remain visible with attribution. Clinical workflow review remains open |
| **Residual likelihood** | Medium |
| 🔍 | **Not adequately controlled.** The real control is a confirmation step showing patient identifiers before an observation is committed, and that is a user-interface design decision needing clinical input. Until then this hazard is open |

### H-05 A deteriorating patient's early warning score is calculated wrongly

| | |
|---|---|
| **Cause** | An error in the scoring implementation, or in a reference range |
| **Effect** | Deterioration missed, or a false alarm that erodes trust in every alert |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** [scoring tests](../tests/Betsi.Tests/Domain/ClinicalScoringTests.cs) check Scale 1 boundaries against RCP NEWS2 Chart 1 and refuse incomplete/invalid inputs. Temperature precision outside the supported bands no longer silently scores zero. Tables are compiled C#; changes require a release. **Unresolved:** the system does not determine pregnancy or whether Scale 2 is needed; age alone does not establish eligibility. This remains a clinical-use gate. **Planned (N3):** display the score alongside source observations. The separate manual deterioration workflow remains N4 work |
| **Residual likelihood** | Low |
| 🔍 | **The scoring values in this build were transcribed by the developer from the published NEWS2 specification and have not been verified by a clinician.** They must be checked value by value before use. This is the single most important item in this log |

### H-06 A child is triaged against adult physiological ranges

| | |
|---|---|
| **Cause** | Age not considered; the wrong age band applied |
| **Effect** | A seriously unwell child scored as stable |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** [age/scoring tests](../tests/Betsi.Tests/Domain/ClinicalScoringTests.cs) check the sixteenth birthday and refusal to score every paediatric band; domain tests retain measurements without an adult score. **Planned (N3/N4):** consistent paediatric identification in relevant screens; this is not implemented throughout the UI. Clinical age-band judgement remains provisional |
| **Residual likelihood** | Low |
| 🔍 | Refusing to score rather than scoring with a paediatric tool is the conservative choice made here. A reviewer must decide whether the site's own paediatric tool should be implemented instead, and which one |

### H-07 An unaccompanied child is not identified

| | |
|---|---|
| **Cause** | Carer presence not recorded; a carer leaves and nobody updates it |
| **Effect** | A safeguarding failure |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** the episode API/UI records carer presence; a child below the configured age raises a safeguarding escalation in the same transaction. An injected database-failure test proves the episode, escalation, events and outbox roll back together. Response ownership uses a validated configured role. Site approval of the threshold and procedure remains open |
| **Residual likelihood** | Medium |
| 🔍 | The system can only know what someone tells it. A carer leaving without anyone recording it is not detectable, and no software control fixes that — the control is procedural and belongs in the site's own safeguarding policy |

### H-08 A safeguarding concern is recorded and not acted on

| | |
|---|---|
| **Cause** | The flag is raised but no one is told |
| **Effect** | A safeguarding failure |
| **Severity** | Catastrophic |
| **Initial likelihood** | Low |
| **Controls** | **Engineering verified:** the API/UI creates the episode concern and its assigned escalation in one `IUnitOfWork` commit. The existing escalation workflow supplies acknowledgement deadlines, reassignment, missed-deadline follow-up and an immutable audit trail. Site workflow acceptance remains open |
| **Residual likelihood** | Low |

### H-09 The system is unavailable during a busy period

| | |
|---|---|
| **Cause** | Database outage, deployment failure, an expired licence |
| **Effect** | The department loses its view of who is waiting and reverts to paper mid-shift |
| **Severity** | Major |
| **Initial likelihood** | Medium |
| **Controls** | The registry is served from an in-memory snapshot, so a control-plane outage does not stop tenants already being served. Liveness probes deliberately do not touch a database, so a database outage cannot restart every instance into it. Licence expiry never disables patient or escalation commands: those are marked always-available and a test enforces it |
| **Residual likelihood** | Low |
| 🔍 | Which operations a licence may ever gate is a clinical safety decision. The current classification blocks only location and queue creation. A reviewer must confirm nothing clinical can be switched off commercially |

### H-10 Stale data is read as current

| | |
|---|---|
| **Cause** | A live board loses its connection and keeps showing the last state |
| **Effect** | A clinician reads an hour-old waiting time as now |
| **Severity** | Major |
| **Initial likelihood** | Medium |
| **Controls** | Boards show the last update time. A dropped circuit or browser offline event shows a warning independent of the circuit and makes stale main content inert. Recovery reloads authorised current data; the browser hub retries initial failures and refetches after reconnect. Browser automation covers missed changes and actual login expiry; physical device review remains deferred |
| **Residual likelihood** | Low |

### H-11 A clinician acts in the wrong role, or sees data they should not

| | |
|---|---|
| **Cause** | Role claims wrong at the identity provider; a multi-role account not choosing |
| **Effect** | An action attributed to the wrong role in the clinical record; patient data seen by an administrator |
| **Severity** | Major |
| **Initial likelihood** | Low |
| **Controls** | An account holding several roles must select one; the selection must be a role it holds. Permissions come from a role matrix applied in the API pipeline and, separately, in the interface. Every command records the acting role |
| **Residual likelihood** | Low |
| 🔍 | The role-to-permission matrix is a clinical and information-governance artefact. It has not been reviewed |

### H-12 Significant pain does not receive an analgesic review

| | |
|---|---|
| **Cause** | Pain details are incomplete, the configured threshold is inappropriate, or the alert has no effective owner |
| **Effect** | Avoidable suffering or deterioration while the patient waits |
| **Severity** | Major |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** a pain score requires scale, location, character and onset. A score at or above the configured threshold creates a `SystemAlert` for a validated responding role in the same transaction as the observation. A correction of an already-high score does not create a duplicate alert. The normal acknowledgement and missed-deadline workflow applies |
| **Residual likelihood** | Medium |
| 🔍 | The default threshold of 5 and `Nurse in Charge` owner are provisional. A site clinical reviewer must approve both and decide how pain affects queue priority before real-patient use |

### H-13 A child receives care without paediatric-trained staff

| | |
|---|---|
| **Cause** | No clinician is assigned, or the assignee has no recorded paediatric competence |
| **Effect** | A paediatric observation or task is performed without the required skills |
| **Severity** | Catastrophic |
| **Initial likelihood** | Medium |
| **Controls** | **Engineering verified:** the episode records the assigned clinician's verified identity and active role plus an explicit training declaration. For a patient below the configured pathway age, an untrained assignment or first observation without a trained assignee creates one charge-nurse `SystemAlert` atomically. Duplicate observations do not flood the board, and an injected failure proves assignment and alert roll back together |
| **Residual likelihood** | Medium |
| 🔍 | The declaration is currently self-recorded rather than sourced from an approved workforce register. The pathway age, qualifying training, staff process and response owner require site approval before paediatric use |

---

## Hazards this log does not yet cover

Written down because an incomplete log that pretends to be complete is the failure mode DCB0129
exists to prevent.

- **Data quality from integrations.** HL7 and FHIR messages create and discharge episodes. A
  duplicate or mis-mapped visit identifier is a real hazard and has no entry yet.
- **Paper fallback and recovery.** What a department does when the system is down, and how
  paper records are reconciled afterwards.
- **Training and competence.** Every control above assumes the user understands what the screen
  is telling them.
- **Decommissioning.** What happens to the clinical record when a site stops using the system.

## Review

| Date | Reviewer | Outcome |
|---|---|---|
| 2026-09-17 | Developer (no clinical qualification) | Log opened. Eleven hazards recorded, seven flagged for clinical review, one (H-04) recorded as not adequately controlled |
| 2026-09-18 | Engineering documentation review (no clinical sign-off) | Separated planned, implemented and historical evidence; corrected H-03–H-08 claims. No tests rerun or residual-risk estimates clinically validated. |
| 2026-09-18 | Engineering verification follow-through (no clinical sign-off) | Added reference boundary, API retry/input/failure and SQL Server concurrency evidence in [N1 verification](N1-VERIFICATION.md). Corrected unsupported-temperature scoring and concurrent-correction error handling. Clinical eligibility and residual-risk estimates remain unapproved. |
| 2026-09-20 | Engineering template follow-through (no clinical sign-off) | Added explicit observation source, separate SBAR sections and generic breathing/circulation/mobility findings through API, SQL Server, correction history and bilingual UI. Findings deliberately carry no diagnostic rule or automatic trigger. |

**Next review**: update control status and evidence with each N1–N4 implementation PR; clinical
safety officer review and safety case remain required before any use with real patients.
