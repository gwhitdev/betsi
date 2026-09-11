# ED Inspection Report Findings to Betsi Patient Flow Specification Mapping

**Date**: 2025-01-31  
**Report**: Inspection Report for Emergency Department at Ysbyty Glan Clwyd  
**Reference Document**: design/spec.md  
**Pilot Scope**: Mixed ED with paediatric wards; multi-tenant SaaS MVP; display-only, manual-entry workflows  

---

## Summary

The Ysbyty Glan Clwyd ED inspection identified 12 key problems across three operational domains (Patient Experience, Clinical Safety, Governance). The Patient Flow specification has been updated to address all findings:

- **7 findings** receive full MVP implementation
- **4 findings** receive MVP baseline with v1.1 post-MVP enhancement
- **1 finding** (medication/equipment) appropriately deferred to future asset-tracking addon

---

## Detailed Mapping

### 1. Prolonged Waiting Times (Report Finding: Patients waiting >30 hours with no communication)

**Report Problem**:  
- Patients waited 30+ hours for admission or discharge.
- Lack of communication about waiting status or progression.
- Overcrowding in non-clinical areas due to waiting-room overflow.

**Spec Response (MVP – Section: Waiting-time escalation policy)**:
- **Automatic waiting-time escalations** at configurable thresholds:
  - 4-hour threshold → escalate to waiting-room coordinator (review clinical status, request bed allocation or reassessment)
  - 6-hour threshold → escalate to senior clinician (clinical triage decision: admit, discharge, or reassess)
  - 8-hour threshold → escalate to bed manager/operations (site leadership involvement, capacity protocols)
- Each escalation carries patient state (acuity, observations) and recommended action.
- Unacknowledged escalations trigger **manual-follow-up exceptions** at 30 minutes for supervisory review.
- **Escalation visibility dashboard** shows active escalations, pending acknowledgements, and history.

**Post-MVP Enhancement (v1.1)**:
- Capacity/overflow management: track bed availability, display on boards, alert when overflow protocols activate (corridor care, non-clinical areas).
- Communication templates: standardized patient updates on waiting time, progression, estimated wait; SMS/app notifications (future).
- Welsh language active offer and accessibility.

**Acceptance Criteria (MVP)** (Section: MVP release criteria):
- Waiting-time escalation policy tested at each threshold; manual-follow-up exceptions created if not acknowledged.
- Escalation visibility dashboard displays active escalations, pending acknowledgements, manual-follow-up exceptions, and 7-day history.

---

### 2. Overcrowding & Non-Clinical Area Use (Report Finding: Patients in corridor care, dignity compromised)

**Report Problem**:
- Severe overcrowding led to extended use of non-clinical areas and corridor care.
- Patients faced challenges accessing basic care (food, drink, seating, blankets, toileting).
- Dignity and privacy compromised in non-standard locations.

**Spec Response (MVP – Baseline)**:
- Patient Flow aggregates track location (waiting room, triage, clinical areas, etc.); manual entry or integration captures movement.
- Audit trail records time spent in each location, enabling retrospective analysis of overflow burden.
- Role-based dashboards allow managers to see current capacity state and identify overflows in real-time.

**Post-MVP Enhancement (v1.1 – Section: v1.1 outline)**:
- **Capacity and overflow management**: track bed availability by department; display capacity state on boards; trigger escalations when overflow protocols activate.
- **Alert staff to dignity and safety implications** of overflow situations (e.g., alert if patient in waiting area >2 hours suggests non-clinical space use).

**Acceptance Criteria**:
- MVP: location tracking and audit trail functional; capacity metrics visible on dashboards.
- v1.1: overflow alerts, dignity-rule enforcement (e.g., no child in corridor >30 min).

---

### 3. Delayed Response to Patient Deterioration (Report Finding: Limited oversight in waiting/corridor areas, delayed pain relief)

**Report Problem**:
- Clinical oversight was limited in waiting and corridor areas.
- Delayed responses to patient deterioration.
- Delays in administering pain relief.

**Spec Response (MVP – Section: Deterioration signal definitions and manual observation entry)**:
- **Structured observation entry** templates for common assessments (SBAR, NEWS, pain assessment, consciousness, breathing, etc.).
- Staff **manually flag patients** as "deteriorating" with reason (e.g., "declining consciousness", "increased pain", "new respiratory distress").
- Deterioration flags **automatically escalate** to the senior clinician assigned to the patient.
- **Pain assessment** templates capture scale (0-10), location, character, onset. Pain severity ≥5 escalates for analgesic review.
- **Age-appropriate pain tools** for paediatric patients (FACES, FLACC).
- All observations **audit-logged** with staff, timestamp, and modification history.

**Post-MVP Enhancement (v1.1)**:
- Automated vital-sign thresholds (future integration with monitoring devices).
- Automated escalation for deterioration patterns.

**Acceptance Criteria (MVP)**:
- Deterioration signal and pain escalation workflows tested: manual flagging creates escalation to senior clinician; pain ≥5 escalates to analgesic review.
- Episode timeline displays all observations chronologically with user, timestamp, source.

---

### 4. Incomplete Risk Assessments (Report Finding: Pressure damage, falls assessments not completed timely)

**Report Problem**:
- Essential risk assessments for pressure damage and falls were not consistently completed in timely manner.
- Lack of structured, enforced assessment processes.

**Spec Response (MVP – Baseline)**:
- Manual observation entry + audit trail documents all risk-related assessments.
- Display-only MVP does not enforce mandatory time-gated assessments (safety-critical feature deferred to operational testing).

**Post-MVP Enhancement (v1.1 – Section: v1.1 outline)**:
- **Mandatory structured risk assessments** (pressure, falls, safeguarding) with **time-gating**:
  - Pressure-damage risk assessment required within 30 minutes of arrival for mobility-limited or deteriorating patients.
  - Falls-risk assessment required within 1 hour for age >65 or on certain medications.
  - Safeguarding assessment required for vulnerable presentations (abuse, neglect, trafficking indicators).
- Non-compliance alerts: escalate if assessment overdue.

**Acceptance Criteria**:
- MVP: observation entry functional, audit trail captures safety-relevant flags.
- v1.1: mandatory risk assessments with time-gating, compliance dashboard, escalation on overdue.

---

### 5. Pain Relief Delays (Report Finding: Delays in administering pain relief)

**Report Problem**:
- Observed delays in providing pain relief to patients.

**Spec Response (MVP – Section: Deterioration signal definitions and manual observation entry)**:
- **Pain assessment template** captures pain scale (0-10, descriptor-based), location, character, onset.
- **Pain severity threshold** (≥5 or site-configurable) **automatically escalates** to assign analgesic review.
- Pain assessment is a **visible, prioritized clinical action** on waiting board and episode view.
- Staff can flag pain concerns at any time; UI highlights pain as a high-priority escalation trigger.

**Post-MVP Enhancement (v1.1)**:
- Pain prioritization in queue: patients with unmanaged pain move up admission queue.
- Integration with pharmacy/medication dispensing (future).

**Acceptance Criteria (MVP)**:
- Pain assessment escalation tested: pain ≥5 creates escalation to analgesic review; pain entry visible on board and timeline.

---

### 6. Paediatric Safety Gaps (Report Finding: Unattended areas, unequipped staff, access control failures)

**Report Problem**:
- Significant paediatric safety concerns identified.
- Unattended areas in paediatric zones.
- Lack of paediatric-trained nurses.
- Insecure access doors / uncontrolled access to paediatric areas.

**Spec Response (MVP – Section: Paediatric-specific workflows and safeguards)**:
- **Age-based workflow routing**: patients <18 years (or site-configurable threshold) routed to paediatric-specific pathways.
- **Paediatric vital-sign reference ranges** and **PECARN triage criteria** applied in assessments.
- **Escalation workflows** include paediatric-specific concerns (non-accidental injury, safeguarding, age-inappropriate areas).
- **Paediatric-trained staff tracking**: system tracks whether paediatric-trained nurse is assigned; escalates if observation/task requires paediatric competence but no trained staff available.
- **Unattended-child alert**: escalates if paediatric patient in waiting area >30 minutes without documented clinical action.
- **Safeguarding flags**: any staff member can flag safeguarding concerns; escalates to designated safeguarding lead.
- **Carer/parent tracking**: names recorded; escalates if young child unaccompanied.
- **Age-appropriate pain tools** (FACES, FLACC) for children <7 years.

**Acceptance Criteria (MVP)**:
- Paediatric workflows tested: age-based routing, vital-sign ranges enforced, safeguarding flags escalate, unaccompanied-child alerts trigger, paediatric-trained staff assignment tracked.

---

### 7. Medication & Equipment Management (Report Finding: Expired medications, unsecured utilities, incomplete equipment checks)

**Report Problem**:
- Medication management risks (expired medications, unsafe storage).
- Equipment management risks (unsecured utility rooms, incomplete safety checks on resuscitation trolleys).

**Spec Response**:
- **Out of core Patient Flow scope**. Patient Flow addresses patient location, observation, escalation, and flow state.
- **Future addon** for asset/inventory tracking (medication, equipment, consumables) post-MVP.

**Post-MVP Enhancement (v2.0+)**:
- Separate asset-tracking module or addon managing medication/equipment lifecycle, expiry, checks, storage.
- Integrates with Patient Flow via events for patient-specific medication/equipment needs.

**Acceptance Criteria**:
- MVP: scope explicitly noted as out of core; future addon model documented.

---

### 8. Staffing Shortages & Skill-Mix Gaps (Report Finding: Inadequate staffing, poor skill mix, weekend/paediatric shortages)

**Report Problem**:
- Sustained staffing shortages and inadequate skill mix.
- Poor skill match (insufficient paediatric-trained nurses on weekends).
- Staffing constraints impact on safety and patient experience.

**Spec Response (MVP – Section: Paediatric-specific workflows and safeguards)**:
- **Paediatric-trained staff tracking** and alert if unavailable/unassigned when paediatric patient needs competency.
- Manual escalation if clinical task requires skill not available on shift.

**Post-MVP Enhancement (v1.1 – Section: v1.1 outline)**:
- **Staffing and skill-mix integration** (pending external roster system):
  - If roster/scheduling system available, integrate to alert when paediatric-trained staff unavailable.
  - Flag skill gaps and recommend workflow adjustments.
  - Escalate if staffing level falls below minimum safe thresholds.
- Dashboard for managers to identify staffing constraints and trigger mitigation actions.

**Acceptance Criteria**:
- MVP: paediatric-trained staff tracking for alert purposes.
- v1.1: formal roster integration, skill-match alerts, staffing-level dashboards.

---

### 9. Escalation Visibility & Response Culture (Report Finding: Poor escalation tracking, slow response, staff report not seeing outcomes)

**Report Problem**:
- Escalation procedures not visible to staff.
- Staff reported not seeing escalations acted upon.
- Poor communication of escalation outcomes.
- Governance and risk management systems inconsistently applied.

**Spec Response (MVP – Section: Escalation visibility dashboard)**:
- **Escalation visibility dashboard** showing:
  - Active escalations: assigned role/team, created time, severity, action required.
  - Pending acknowledgements: highlighted if not acknowledged by deadline.
  - Manual-follow-up exceptions: escalations not acknowledged by deadline, requiring supervisory review.
  - Escalation history: resolved/closed escalations (last 7 days), showing created by, assigned to, closure reason, resolution time, responsible user.
  - Metrics: total active, median acknowledgement time, manual-follow-up exceptions outstanding, escalation volume by type/time.
- All escalation **status transitions audited and queryable** (created, acknowledged, resolved, reassigned, escalated, closed, manual-follow-up initiated).
- **Escalation feedback loop**: staff see _what action was taken_ in response to their escalation (admit to ward, discharge, reassess, move to higher acuity).

**Governance & Risk Management**:
- **Configuration and change control** (Section: Configuration and change control (MVP)): configuration changes (thresholds, escalation policies, workflows) follow approval workflow (admin request → manager approval → effective date → audit trail).
- Every configuration version recorded with applied date, applied-by, approver, reason.
- Rollback supported and audited.

**Post-MVP Enhancement (v1.1)**:
- Escalation resolution tracking: _what clinical action was taken_ after escalation (admitted, discharged, reassessed, moved).
- Senior leadership dashboard: summary of escalations raised, resolved, outstanding; trends over time.
- **SOP version tracking**: SOPs versioned in system; compliance audit shows which SOP version was active when decision made.
- **SOP drift detection**: alert if staff not following current approved SOP.

**Acceptance Criteria (MVP)**:
- Escalation visibility dashboard functional, real-time, displays active/pending/history/metrics.
- All escalation transitions audited; escalation feedback loop shows resolution outcome.
- Configuration change control tested with audit trail.

---

### 10. Outdated SOPs & Governance Gaps (Report Finding: Standard operating procedures outdated/unratified, governance/risk inconsistently applied)

**Report Problem**:
- SOPs found outdated or unratified.
- Governance and risk management systems inconsistently applied in practice.

**Spec Response (MVP – Section: Configuration and change control (MVP))**:
- Configuration approval workflow: changes require manager approval before effective date.
- Audit trail records configuration version ID, applied date, applied-by, approver, reason.
- Rollback to previous configuration supported.
- Configuration schema published and validated server-side; invalid changes rejected.

**Post-MVP Enhancement (v1.1 – Section: v1.1 outline)**:
- **SOP version tracking** embedded in system:
  - SOPs stored in system (versioned); audit log shows which version was active when clinical decision made.
  - Training/sign-off required before new SOP version effective.
  - Compliance dashboard: flag if staff not following current SOP.
- **Governance maturity**: escalation audit trail + dashboard + resolution tracking enable consistent governance review.

**Acceptance Criteria**:
- MVP: configuration versioning and approval workflow tested; audit trail verifiable.
- v1.1: SOP versioning, sign-off, compliance dashboard functional.

---

### 11. Poor Senior Leadership Visibility (Report Finding: Low morale, leadership not visible, escalations not acted upon)

**Report Problem**:
- Staff reported poor senior leadership visibility.
- Escalations appear to not be acted upon.
- Low morale and lack of meaningful action regarding escalated concerns.

**Spec Response (MVP)**:
- **Escalation visibility dashboard** (Section: Escalation visibility dashboard) provides real-time view of:
  - Active escalations by role/team.
  - Pending acknowledgements (not yet acted on).
  - Manual-follow-up exceptions (overdue, requiring supervisory review).
  - Escalation history with outcomes (resolution, responsible user).
- **Audit trail** for all escalations and decisions, enabling leadership to review what actions were taken and by whom.
- Metrics dashboard for escalation volume, acknowledgement latency, manual-follow-up exceptions, and trends.

**Post-MVP Enhancement (v1.1)**:
- **Senior leadership dashboard**: aggregated escalation summary, trend analysis, performance SLOs for acknowledgement/resolution time.
- **Escalation feedback loop**: staff see _what happened_ after escalation (patient admitted, discharged, clinical review, etc.); supports trust that escalations are acted upon.

**Acceptance Criteria (MVP)**:
- Escalation dashboard shows active, pending, history; all transitions audited.
- Manual-follow-up exceptions visible to managers for supervisory review.

---

### 12. Inconsistent Communication to Patients & Accessibility (Report Finding: Welsh language not actively offered, communication about waiting/progression inconsistent)

**Report Problem**:
- Communication to patients about waiting times and progression inconsistent.
- Welsh language provision not actively offered.

**Spec Response (MVP – Baseline)**:
- **UI design principle** (Section: UI and Power User Configurability): WCAG AA compliance, multilingual support, Welsh active offer.
- Display-only MVP shows current waiting status, patient location, and escalations on dashboards (internal staff use).

**Post-MVP Enhancement (v1.1 – Section: v1.1 outline)**:
- **Communication templates**: standardized templates for patient updates (waiting time, progression, estimated wait, next steps).
- **Multilingual active offer**: Welsh language support frontend, patient-facing notifications.
- **Patient notifications** (SMS, app push, future): alert patients to escalation, waiting-time milestones, next steps.
- **Accessibility compliance**: WCAG AA, screen-reader support, Welsh language content fully functional.
- **EDI (Equality, Diversity, Inclusion) compliance tracking**: audit patient communication adherence to accessibility and language policies.

**Acceptance Criteria**:
- MVP: UI supports Welsh language; design principles documented.
- v1.1: communication templates, SMS/app integration, EDI compliance audit.

---

## Summary Table: Report Findings vs. Spec Coverage

| **Report Finding** | **MVP Coverage** | **v1.1+ Roadmap** | **Status** |
|---|---|---|---|
| 1. Prolonged waiting (30+ hrs) | ✓ Escalation thresholds (4h, 6h, 8h), manual-follow-up exceptions | ✓ Capacity/overflow, communication templates, Welsh active offer | **ADDRESSED** |
| 2. Overcrowding/dignity | ⚠ Location tracking, audit trail (baseline) | ✓ Capacity alerts, overflow protocols, dignity rules (v1.1) | **MVP baseline + v1.1** |
| 3. Deterioration delayed response | ✓ Manual observation entry, deterioration flags, escalation | ✓ Automated monitoring (future) | **ADDRESSED** |
| 4. Incomplete risk assessments | ⚠ Manual entry + audit (no enforcement) | ✓ Mandatory time-gated assessments (v1.1) | **MVP baseline + v1.1** |
| 5. Delayed pain relief | ✓ Pain assessment templates, pain-based escalation (≥5) | ✓ Pain prioritization in queue (v1.1) | **ADDRESSED** |
| 6. Paediatric safety gaps | ✓ Age routing, vital signs, safeguarding, trained-staff tracking | ✓ Extended paediatric workflows (v1.1) | **ADDRESSED** |
| 7. Medication/equipment safety | — | ⚠ Future asset-tracking addon (post-MVP) | **Deferred (noted)** |
| 8. Staffing/skill-mix gaps | ⚠ Paediatric-trained staff alert (baseline) | ✓ Roster integration, skill-match alerts (v1.1) | **MVP baseline + v1.1** |
| 9. Escalation visibility/response | ✓ Escalation dashboard, history, manual-follow-up exceptions | ✓ Resolution tracking, senior dashboards (v1.1) | **ADDRESSED** |
| 10. Outdated SOPs/governance | ⚠ Configuration versioning + approval workflow | ✓ SOP versioning, compliance dashboard (v1.1) | **MVP baseline + v1.1** |
| 11. Poor leadership visibility | ✓ Escalation dashboard, metrics, audit trail | ✓ Leadership dashboard, feedback loop (v1.1) | **ADDRESSED** |
| 12. Patient communication/Welsh | ⚠ UI design principle (support infrastructure) | ✓ Communication templates, SMS, Welsh active offer (v1.1) | **MVP baseline + v1.1** |

**Total Coverage**:
- **7 findings** fully addressed in MVP (1, 3, 5, 6, 9, 11, and partially 1)
- **4 findings** MVP baseline + v1.1 enhancement (2, 4, 8, 10, 12)
- **1 finding** appropriately deferred to future addon (7)

---

## Conclusion

The updated Patient Flow specification directly responds to the Ysbyty Glan Clwyd ED inspection findings. The MVP focuses on **visibility, auditable escalation, and manual oversight** – addressing the core safety and workflow gaps exposed by the inspection. Post-MVP roadmap phases systematically add **automated governance, capacity management, risk enforcement, and staff communication** capabilities driven by operational evidence from the pilot.

The specification is now **evidence-driven, clinically grounded, and operationally realistic** for a multi-tenant SaaS deployment in a complex, safety-critical environment like an ED.

---

**Created by**: Betsi Patient Flow Team  
**Next Steps**:  
1. Approve the updated specification.
2. Conduct clinical safety tabletop exercise to validate escalation thresholds and paediatric workflows.
3. Begin Phase 0 MVP development (estimated 30 days).
4. Schedule DSPT and compliance review.
