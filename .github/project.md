# Betsi Patient Flow Platform – Implementation Roadmap

**Status**: Specification Ready for MVP Development  
**Last Updated**: 2025-01-31  
**Reference Docs**: [design/spec.md](../design/spec.md), [design/REPORT_FINDINGS_MAPPING.md](../design/REPORT_FINDINGS_MAPPING.md)

---

## Overview

Betsi Patient Flow is a multi-tenant SaaS platform for NHS emergency departments to provide safe, auditable patient flow visibility with escalation governance. The MVP is informed by operational problems documented in the Ysbyty Glan Clwyd ED inspection report.

**Goals**:
- Eliminate blind spots in waiting and corridor areas
- Provide real-time, auditable patient visibility
- Enable safe, resilient escalation workflows
- Support multi-site, multi-tenant SaaS deployment
- Maintain DSPT compliance and clinical safety

---

## Current Phase: MVP Development (Phase 0 → Phase 1)

**Timeline**: Estimated 30 days (Phase 0 development) followed by pilot phases.

**Target Release Criteria**:
- Display-only and manual-entry workflows (no automatic escalations or AI)
- Multi-tenant SaaS with database-per-tenant isolation
- Waiting-time escalation policy (4h/6h/8h thresholds)
- Paediatric workflows and safeguarding
- Escalation visibility dashboard
- Audit logging for all actions
- DSPT compliance

---

## Repositories & Structure

- **Source**: [github.com/gwhitdev/betsi](https://github.com/gwhitdev/betsi)
- **Branch**: `main` (production-ready code)
- **Development**: Feature branches off main with PR review
- **Issue Tracking**: GitHub Issues
- **Project**: GitHub Projects (kanban board)

---

## Key Themes & Epics

### Theme 1: Core Patient Flow Engine (MVP Foundational)
**Status**: In Discovery & Architecture  
**Issues**: #MVP-001 through #MVP-010

Establish the transactional domain model, event sourcing, and command handling for patient episodes, locations, queues, and escalations.

**Key Deliverables**:
- PatientEpisode, Location, Queue, Escalation, RiskFlag aggregates
- Core commands (RegisterArrival, TriageComplete, MovePatient, TriggerEscalation, etc.)
- Domain events and audit stream
- Transactional outbox for reliable delivery

---

### Theme 2: Waiting-Time Escalation & Visibility (MVP Critical)
**Status**: In Design → Development  
**Issues**: #MVP-020 through #MVP-025

Implement automatic waiting-time escalations at configured thresholds and escalation visibility dashboard to address prolonged-wait findings from ED report.

**Key Deliverables**:
- Waiting-time escalation policy configuration (4h, 6h, 8h)
- Automatic escalation generation and manual-follow-up exception workflow
- Escalation visibility dashboard (active, pending, history, metrics)
- Escalation audit trail and status transitions

---

### Theme 3: Paediatric Safety & Safeguarding (MVP Critical)
**Status**: In Design → Development  
**Issues**: #MVP-030 through #MVP-035

Support paediatric-specific workflows, vital signs, safeguarding flags, and trained-staff tracking to address paediatric safety gaps from ED report.

**Key Deliverables**:
- Age-based workflow routing
- Paediatric vital-sign reference ranges
- Safeguarding flag and escalation workflow
- Trained-staff assignment tracking
- Unaccompanied-child alerts

---

### Theme 4: Clinical Observation & Deterioration (MVP Critical)
**Status**: In Design → Development  
**Issues**: #MVP-040 through #MVP-045

Capture structured clinical observations, enable manual deterioration flagging, and support pain assessment to address delayed-response findings.

**Key Deliverables**:
- Observation entry templates (SBAR, NEWS, pain, etc.)
- Manual deterioration flagging with escalation
- Pain assessment (0-10 scale, location, character, onset)
- Age-appropriate pain tools (FACES, FLACC)
- Episode timeline view with observation history

---

### Theme 5: Multi-Tenant SaaS Isolation & Governance (MVP Foundational)
**Status**: In Architecture → Development  
**Issues**: #MVP-050 through #MVP-055

Ensure database-per-tenant isolation, multi-site support, and configuration change control for safe SaaS operations.

**Key Deliverables**:
- Database-per-tenant provisioning and schema management
- Tenant identity resolution and routing
- Configuration versioning and approval workflow
- Site-specific escalation threshold management
- Audit logging for all tenant operations

---

### Theme 6: API, Integration, & Authentication (MVP Foundational)
**Status**: In Design → Development  
**Issues**: #MVP-060 through #MVP-070

Deliver versioned REST API, webhook contracts, OAuth2/OpenID Connect authentication, and EPR integration adapters.

**Key Deliverables**:
- REST API v1 endpoints (commands, queries, webhooks)
- OpenAPI v3 specification
- OAuth2/OpenID Connect authentication
- EPR/ambulance integration adapters (FHIR/HL7)
- API client libraries and SDK

---

### Theme 7: UI/UX – Waiting Board & Power User Editor (MVP Critical)
**Status**: In Design → Development  
**Issues**: #MVP-080 through #MVP-095

Build task-oriented, accessible UI for waiting board, escalation dashboard, and power-user configuration workspace.

**Key Deliverables**:
- Waiting board dashboard (lanes, filters, columns, thresholds)
- Escalation visibility board (active, pending, history)
- Episode detail view (patient summary, observations, escalations)
- Power-user editor for board configuration
- WCAG AA accessibility, Welsh language support

---

### Theme 8: Testing, Deployment, & Operations (MVP Foundational)
**Status**: In Architecture → Development  
**Issues**: #MVP-100 through #MVP-115

Establish end-to-end testing, CI/CD pipelines, deployment automation, and operational observability.

**Key Deliverables**:
- Unit, integration, and BDD (Gherkin) acceptance tests
- CI/CD pipeline (build, test, deploy to staging/prod)
- Blue/green or canary deployment strategy
- Database migration automation and rollback
- Monitoring, logging, tracing, metrics, alerts

---

## Phased Rollout Plan

### Phase 0 → Phase 1: MVP Development & Internal Pilot (4 weeks)
**Entrance Criteria**: Specification approved by clinical and technical stakeholders.  
**Exit Criteria**: All MVP acceptance criteria met; clinical safety officer sign-off.

- Week 1: Infrastructure setup, domain modeling, transactional event/audit.
- Week 2: Core aggregates (PatientEpisode, Escalation, Location).
- Week 3: API endpoints, waiting-time escalations, paediatric routing.
- Week 4: UI (waiting board, escalation dashboard), deployment, testing.

**Deliverables**: MVP application, runbook, training materials.

---

### Phase 1 → Phase 2: Pilot Operations & Feedback (1 week minimum)
**Entrance Criteria**: Phase 0 complete; clinical safety case approved; site champion and project lead confirm readiness.  
**Exit Criteria**: No safety incidents, critical workflows functional, staff can complete key tasks.

- Deploy to pilot site.
- Operational huddles and feedback collection.
- Bug fixes and QoL improvements.

**Measurable Outcomes**: Escalation frequency, acknowledgement latency, manual-follow-up exceptions, user adoption.

---

### Phase 2 → Phase 3: Full ED Adoption (2+ weeks)
**Entrance Criteria**: Phase 1 successful; all workflows validated; training complete.  
**Exit Criteria**: Full ED adoption; audit evidence and SLO performance acceptable; security review and DSPT submission complete.

- Roll out to additional wards/departments.
- Full staff training.
- Support model activation.

**Operational SLOs**:
- Core command availability: 99.95%
- Waiting-board freshness: 99% under 10 seconds
- Critical alert creation-to-display: 99.9% under 5 seconds
- RPO: ≤5 minutes
- RTO: ≤30 minutes

---

### Phase 3 → Continuous Deployment (Post-MVP)
**Model**: Feature flags, rolling deployments, evidence-driven prioritization.

**v1.1 Planned (4–6 weeks post-MVP)**:
- Site-specific escalation customization
- Mandatory structured risk assessments (pressure, falls, safeguarding)
- Escalation resolution tracking
- Capacity/overflow management
- Communication templates and patient notifications
- SOP versioning and compliance tracking

**v2.0 Planned (3–4 months post-v1.1)**:
- Optional AI advisory layer (risk scoring, flow prediction)
- Patient Portal addon
- Compliance Portal addon
- Wellbeing Portal addon

---

## Epic-to-Issue Mapping

See [GitHub Issues](#github-issues) below for detailed breakdown by theme.

---

## GitHub Issues

Issues are organized by theme and labeled with:
- **Priority**: P0 (CLI/blocking), P1 (high impact), P2 (medium), P3 (low/nice-to-have)
- **Type**: Feature, Bug, Tech-Debt, Documentation
- **Phase**: MVP, v1.1, v2.0, v3.0
- **Component**: Core, API, UI, Integration, Testing, Ops

### MVP-001 through MVP-010: Core Patient Flow Engine
- [ ] [MVP-001](#mvp-001-design-bounded-contexts-and-aggregates) – Design bounded contexts and aggregates
- [ ] [MVP-002](#mvp-002-implement-patientepisode-aggregate) – Implement PatientEpisode aggregate
- [ ] [MVP-003](#mvp-003-implement-location-and-queue-aggregates) – Implement Location and Queue aggregates
- [ ] [MVP-004](#mvp-004-implement-escalation-aggregate) – Implement Escalation aggregate
- [ ] [MVP-005](#mvp-005-implement-transactional-event-log-and-outbox) – Implement transactional event log and outbox
- [ ] [MVP-006](#mvp-006-design-and-implement-command-handlers) – Design and implement command handlers
- [ ] [MVP-007](#mvp-007-multi-tenant-context-and-isolation) – Multi-tenant context and isolation
- [ ] [MVP-008](#mvp-008-offline-key-validator-and-licensing) – Offline key validator and licensing module
- [ ] [MVP-009](#mvp-009-database-per-tenant-provisioning) – Database-per-tenant provisioning and migrations
- [ ] [MVP-010](#mvp-010-audit-logging-and-compliance) – Audit logging and compliance event stream

### MVP-020 through MVP-025: Waiting-Time Escalation & Visibility
- [ ] [MVP-020](#mvp-020-waiting-time-escalation-policy) – Waiting-time escalation policy configuration
- [ ] [MVP-021](#mvp-021-automatic-escalation-generation) – Automatic escalation generation and triggers
- [ ] [MVP-022](#mvp-022-manual-follow-up-exceptions) – Manual-follow-up exception workflow
- [ ] [MVP-023](#mvp-023-escalation-visibility-dashboard) – Escalation visibility dashboard (active, pending, history)
- [ ] [MVP-024](#mvp-024-escalation-audit-trail) – Escalation audit trail and status transitions
- [ ] [MVP-025](#mvp-025-escalation-acknowledgement-workflow) – Escalation acknowledgement and resolution workflow

### MVP-030 through MVP-035: Paediatric Safety & Safeguarding
- [ ] [MVP-030](#mvp-030-age-based-workflow-routing) – Age-based workflow routing
- [ ] [MVP-031](#mvp-031-paediatric-vital-signs-reference) – Paediatric vital-sign reference ranges
- [ ] [MVP-032](#mvp-032-safeguarding-flag-workflow) – Safeguarding flag workflow and escalation
- [ ] [MVP-033](#mvp-033-trained-staff-assignment-tracking) – Trained-staff assignment tracking and alerts
- [ ] [MVP-034](#mvp-034-unaccompanied-child-alerts) – Unaccompanied-child alerts
- [ ] [MVP-035](#mvp-035-paediatric-pain-assessment-tools) – Paediatric pain assessment tools (FACES, FLACC)

### MVP-040 through MVP-045: Clinical Observation & Deterioration
- [ ] [MVP-040](#mvp-040-observation-entry-templates) – Observation entry templates (SBAR, NEWS)
- [ ] [MVP-041](#mvp-041-manual-deterioration-flagging) – Manual deterioration flagging and escalation
- [ ] [MVP-042](#mvp-042-pain-assessment-capture-and-escalation) – Pain assessment capture and escalation
- [ ] [MVP-043](#mvp-043-episode-timeline-view) – Episode timeline view with observation history
- [ ] [MVP-044](#mvp-044-observation-audit-trail) – Observation audit trail and modification history
- [ ] [MVP-045](#mvp-045-risk-flag-capture) – Risk-flag capture (free-form clinical concerns)

### MVP-050 through MVP-055: Multi-Tenant SaaS & Governance
- [ ] [MVP-050](#mvp-050-database-per-tenant-provisioning) – Database-per-tenant provisioning
- [ ] [MVP-051](#mvp-051-tenant-identity-and-routing) – Tenant identity resolution and routing
- [ ] [MVP-052](#mvp-052-configuration-versioning-and-approval) – Configuration versioning and approval workflow
- [ ] [MVP-053](#mvp-053-site-specific-escalation-thresholds) – Site-specific escalation threshold management
- [ ] [MVP-054](#mvp-054-multi-site-support) – Multi-site support and regional configuration
- [ ] [MVP-055](#mvp-055-tenant-audit-logging) – Tenant-scoped audit logging and compliance events

### MVP-060 through MVP-070: API, Integration, & Authentication
- [ ] [MVP-060](#mvp-060-rest-api-design-and-openapi) – REST API v1 design and OpenAPI spec
- [ ] [MVP-061](#mvp-061-command-submission-endpoint) – Command submission endpoint (/api/v1/commands)
- [ ] [MVP-062](#mvp-062-episode-query-endpoints) – Episode query endpoints (/api/v1/episodes/{id})
- [ ] [MVP-063](#mvp-063-webhook-contract-and-inbound) – Webhook contract and inbound mapping
- [ ] [MVP-064](#mvp-064-oubound-escalation-webhooks) – Outbound escalation/event webhooks
- [ ] [MVP-065](#mvp-065-oauth2-openid-authentication) – OAuth2/OpenID Connect authentication
- [ ] [MVP-066](#mvp-066-fhir-hl7-integration-adapter) – FHIR/HL7 integration adapter (EPR/ambulance)
- [ ] [MVP-067](#mvp-067-rest-integration-adapter) – REST integration adapter for third-party systems
- [ ] [MVP-068](#mvp-068-api-error-handling-rfc9457) – API error handling (RFC 9457 problem details)
- [ ] [MVP-069](#mvp-069-api-documentation-and-sdk) – API documentation and .NET client SDK
- [ ] [MVP-070](#mvp-070-api-versioning-strategy) – API versioning strategy and backward compatibility

### MVP-080 through MVP-095: UI/UX – Waiting Board & Dashboards
- [ ] [MVP-080](#mvp-080-waiting-board-dashboard) – Waiting board dashboard (lanes, filters, columns)
- [ ] [MVP-081](#mvp-081-escalation-visibility-board) – Escalation visibility board (active, pending, history)
- [ ] [MVP-082](#mvp-082-episode-detail-view) – Episode detail view (patient summary, observations, escalations)
- [ ] [MVP-083](#mvp-083-power-user-editor) – Power-user editor for board configuration
- [ ] [MVP-084](#mvp-084-configuration-preview-and-approval) – Configuration preview and approval workflow (UI)
- [ ] [MVP-085](#mvp-085-patient-search-and-filters) – Patient search and filtering
- [ ] [MVP-086](#mvp-086-role-based-access-control) – Role-based access control (RBAC) enforcement in UI
- [ ] [MVP-087](#mvp-087-wcag-aa-accessibility) – WCAG AA accessibility compliance
- [ ] [MVP-088](#mvp-088-welsh-language-support) – Welsh language support and content
- [ ] [MVP-089](#mvp-089-responsive-and-mobile) – Responsive design for desktop and clinical devices
- [ ] [MVP-090](#mvp-090-real-time-updates) – Real-time board updates (WebSocket or polling)
- [ ] [MVP-091](#mvp-091-print-export-functionality) – Print and export functionality
- [ ] [MVP-092](#mvp-092-user-feedback-and-help) – User feedback and inline help
- [ ] [MVP-093](#mvp-093-session-timeout-and-security) – Session timeout and workstation handoff
- [ ] [MVP-094](#mvp-094-ui-theming-and-customization) – UI theming and site-specific customization
- [ ] [MVP-095](#mvp-095-ui-testing-and-qa) – UI testing and QA automation

### MVP-100 through MVP-115: Testing, Deployment, & Operations
- [ ] [MVP-100](#mvp-100-unit-testing-framework) – Unit testing framework and domain logic tests
- [ ] [MVP-101](#mvp-101-integration-testing) – Integration testing (API, persistence, outbox)
- [ ] [MVP-102](#mvp-102-bdd-acceptance-tests) – BDD acceptance tests (Gherkin scenarios)
- [ ] [MVP-103](#mvp-103-api-contract-tests) – API contract tests (webhooks, third-party integrations)
- [ ] [MVP-104](#mvp-104-database-migration-testing) – Database migration testing and rollback
- [ ] [MVP-105](#mvp-105-ci-cd-pipeline) – CI/CD pipeline (GitHub Actions)
- [ ] [MVP-106](#mvp-106-staging-deployment) – Staging environment deployment automation
- [ ] [MVP-107](#mvp-107-production-deployment) – Production deployment (blue/green or canary)
- [ ] [MVP-108](#mvp-108-database-backup-and-restore) – Database backup, restore, and point-in-time recovery
- [ ] [MVP-109](#mvp-109-observability-and-monitoring) – Observability (metrics, logs, tracing)
- [ ] [MVP-110](#mvp-110-alerting-and-incident-response) – Alerting and incident response procedures
- [ ] [MVP-111](#mvp-111-performance-testing-and-tuning) – Performance testing and tuning (p95/p99 latency)
- [ ] [MVP-112](#mvp-112-security-testing-and-penetration) – Security testing and penetration testing
- [ ] [MVP-113](#mvp-113-dspt-compliance-checklist) – DSPT compliance checklist
- [ ] [MVP-114](#mvp-114-runbook-and-operations) – Runbook and operations documentation
- [ ] [MVP-115](#mvp-115-training-materials) – Training materials and role-specific procedures

---

## Success Criteria & Acceptance Gates

### MVP Release Criteria (Phase 0 → Phase 1)
- ✅ Technical code review approved; all defects resolved
- ✅ Build succeeds; all automated tests pass
- ✅ Display-only workflows (arrival, triage, waiting, movement, discharge) with manual entry
- ✅ Identity matching and conflict resolution UI tested
- ✅ EPR integration adapter tested with sample data
- ✅ Escalation alert delivery and manual acknowledgement workflow tested end-to-end
- ✅ **Waiting-time escalation policy tested** at 4h/6h/8h thresholds; manual-follow-up exceptions working
- ✅ **Escalation visibility dashboard functional**: active, pending, history, metrics, real-time updates
- ✅ **Paediatric workflows tested**: age-based routing, vital-sign ranges, safeguarding flags, trained-staff tracking
- ✅ **Deterioration and pain escalation tested**: manual flagging, pain ≥5 escalation, age-appropriate tools
- ✅ Database-per-tenant provisioning and data isolation tested
- ✅ Audit logging of all commands, authorization decisions, escalation transitions verified
- ✅ DSPT checklist compliance items completed
- ✅ Runbook documentation and role templates provided

### Phase 1 Pilot Gate (After 1 week pilot use)
- ✅ System remains available and operational (99.9% uptime)
- ✅ No safety incidents or unrecoverable data loss
- ✅ Critical escalation workflows function as designed
- ✅ Staff can complete key tasks without excessive help requests
- ✅ Escalation procedures and role-based acknowledgement work as intended

### Phase 2 Full Adoption Gate (After pilot success)
- ✅ All workflows validated with representative patient volumes
- ✅ Training and role-specific procedures rolled out
- ✅ Support model and incident escalation procedures documented and tested
- ✅ Audit evidence and SLO performance acceptable to site operations team
- ✅ Security review and DSPT submission completed

---

## Continuous Deployment & Evidence-Driven Roadmap

Post-MVP, the platform transitions to **continuous deployment** with rolling features:
- No formal v1.1/v2.0/v3.0 gates; features merged and deployed incrementally as ready
- Operational metrics collected continuously: safety incidents, escalation frequency, alert-fatigue, adoption, data quality
- Feedback loops: weekly huddles with site champions, monthly metrics review, quarterly strategic review
- All roadmap decisions tied to evidence: measured problems or consistent clinical feedback

**v1.1 Planned (4–6 weeks post-MVP stability)**:
- Site-specific escalation authority and workflow customization
- Mandatory structured risk assessments with time-gating
- Escalation resolution tracking and feedback loop
- Capacity/overflow management and alerts
- Communication templates and patient notifications (SMS/app future)
- SOP versioning and compliance dashboard
- Staffing/skill-mix integration (pending roster system availability)

**v2.0 Planned (3–4 months post-v1.1)**:
- AI advisory layer (optional, opt-in): risk scoring, flow prediction
- Patient Portal addon (read-only access, NHS login)
- Compliance Portal addon (SAR exports, DPIA, retention)
- Wellbeing Portal addon (tasks during exceeding waits)

---

## Meeting Cadence & Governance

- **Daily**: Standup (15 min) – blockers, progress
- **Weekly**: Planning & retrospective (1 hour) – backlog grooming, demo, feedback
- **Bi-weekly**: Clinical review (1 hour) – safety, compliance, escalation policy
- **Monthly**: Steering committee (1 hour) – budget, resourcing, strategic decisions

**Stakeholders**:
- Clinical Safety Officer (approvals, risk)
- Site Champion (operational feedback, user needs)
- Project Lead (delivery, timeline)
- Product Owner (prioritization, roadmap)
- Development Team (technical delivery)

---

## References & Resources

- **Specification**: [design/spec.md](../design/spec.md) – Complete system specification
- **Report Findings Mapping**: [design/REPORT_FINDINGS_MAPPING.md](../design/REPORT_FINDINGS_MAPPING.md) – ED inspection findings to spec alignment
- **API Contract**: (TBD) – OpenAPI v3 specification
- **Architecture Decision Records**: (TBD) – Design decisions and rationale
- **.NET 10 Baseline**: Latest stable .NET 10 (servicing policy applied)
- **License**: TBD (confirm with stakeholders)

---

## How to Use This Plan

1. **Browse Issues**: Start with [MVP-001](#mvp-001) to understand core system design.
2. **Track Progress**: Use GitHub Projects kanban board to move issues through columns: Backlog → In Progress → Review → Done.
3. **Link Work**: Each issue should reference related specs, ADRs, and other issues.
4. **Report Updates**: Update this document as phases complete and roadmap priorities shift.

---

**Last Updated**: 2025-01-31  
**Next Review**: After Phase 0 MVP development complete (TBD)
