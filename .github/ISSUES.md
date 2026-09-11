# GitHub Issues – Betsi Patient Flow MVP

**Total Issues**: 115  
**Phases**: MVP Phase 0  
**Label Categories**: Priority (P0/P1/P2/P3), Type (Feature/Bug/Tech-Debt/Documentation), Component (Core/API/UI/Integration/Testing/Ops)

---

## Quick Reference: Issue Categories

| Issue Range | Theme | Count | Status |
|---|---|---|---|
| MVP-001–010 | Core Patient Flow Engine | 10 | Proposed |
| MVP-020–025 | Waiting-Time Escalation & Visibility | 6 | Proposed |
| MVP-030–035 | Paediatric Safety & Safeguarding | 6 | Proposed |
| MVP-040–045 | Clinical Observation & Deterioration | 6 | Proposed |
| MVP-050–055 | Multi-Tenant SaaS & Governance | 6 | Proposed |
| MVP-060–070 | API, Integration, & Authentication | 11 | Proposed |
| MVP-080–095 | UI/UX – Waiting Board & Dashboards | 16 | Proposed |
| MVP-100–115 | Testing, Deployment, & Operations | 16 | Proposed |

---

## MVP-001–010: Core Patient Flow Engine

**Epic Goal**: Establish the transactional domain model, event sourcing, and command handling for patient episodes, locations, queues, and escalations.

### MVP-001: Design bounded contexts and aggregates
- **Type**: Tech-Debt
- **Priority**: P0 (Blocking)
- **Acceptance Criteria**:
  - Bounded context diagram created
  - Aggregate responsibilities documented
  - Command/event taxonomy drafted
  - Tenant isolation strategy defined
  - Architecture decision record approved

### MVP-002: Implement PatientEpisode aggregate
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-001
- **Acceptance Criteria**:
  - PatientEpisode aggregate with state machine
  - Commands validate and append events
  - Optimistic concurrency working
  - Unit tests cover all transitions
  - Code review approved

### MVP-003: Implement Location and Queue aggregates
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-001, MVP-002
- **Acceptance Criteria**:
  - Location aggregate with state (available, occupied, full, overflow)
  - Queue aggregate with ordered patients
  - Threshold tracking for escalation
  - Unit and integration tests pass
  - Tenant isolation enforced

### MVP-004: Implement Escalation aggregate
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-001, MVP-002
- **Acceptance Criteria**:
  - Escalation state machine (created → acknowledged → resolved / escalated / closed)
  - Manual-follow-up exception workflow
  - Immutable audit trail
  - Commands: TriggerEscalation, AcknowledgeEscalation, ResolveEscalation, ReassignEscalation
  - Unit and acceptance tests pass

### MVP-005: Implement transactional event log and outbox
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-002, MVP-003, MVP-004
- **Acceptance Criteria**:
  - Event log schema with versioning
  - Audit log schema
  - Outbox pattern with transactional consistency
  - Outbox consumer (background worker)
  - Deduplication by event_id tested
  - Replay capability verified
  - Database migration automation works

### MVP-006: Design and implement command handlers
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-002, MVP-003, MVP-004, MVP-005
- **Acceptance Criteria**:
  - Command validation layer (tenant, actor, preconditions)
  - Idempotency with command_id and idempotency_key
  - Concurrency handling with expected_version
  - All core commands implemented
  - Outbox message generation verified
  - Integration tests pass

### MVP-007: Multi-tenant context and isolation
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-001, MVP-005
- **Acceptance Criteria**:
  - TenantContext extraction and validation middleware
  - TenantRegistry with encrypted connections
  - Tenant_id validation on all operations
  - Cross-tenant rejection at API and database layers
  - Audit logging for access attempts
  - Penetration test confirms no cross-tenant exposure

### MVP-008: Offline key validator and licensing module
- **Type**: Feature
- **Priority**: P1
- **Acceptance Criteria**:
  - License key validation without external dependency
  - Feature gate service with local cache
  - Audit logging for validation failures
  - License key signature verification (HMAC/RSA)
  - Documentation for key generation
  - Unit tests pass

### MVP-009: Database-per-tenant provisioning and migrations
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-007
- **Acceptance Criteria**:
  - Provisioning automation (Infrastructure as Code)
  - Migration framework chosen and documented
  - Migrations for all core aggregates
  - Rollback testing verified
  - Backup automation configured
  - Restore test procedure documented
  - Operator runbook created

### MVP-010: Audit logging and compliance event stream
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-005
- **Acceptance Criteria**:
  - Audit table schema designed
  - All commands logged with actor, role, outcome
  - PHI redaction in logs
  - SAR export functionality
  - Retention policy enforced (3 years minimum)
  - Audit query performance <500ms
  - DSPT compliance checklist updated

---

## MVP-020–025: Waiting-Time Escalation & Visibility

**Epic Goal**: Implement automatic waiting-time escalations at configured thresholds (4h/6h/8h) and escalation visibility dashboard to address prolonged-wait findings.

### MVP-020: Waiting-time escalation policy configuration
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-009
- **Acceptance Criteria**:
  - Configuration schema (waiting_threshold_hours, escalation_role, deadline_minutes)
  - Versioning with rollback support
  - Approval workflow (admin → manager → effective)
  - Audit logging for all changes
  - No unsafe defaults

### MVP-021: Automatic escalation generation at waiting-time thresholds
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004, MVP-020
- **Acceptance Criteria**:
  - WaitingTimeMonitor background worker
  - Escalation generation idempotent
  - Queries efficient (<1s for 100+ waiting patients)
  - Retry/dead-letter mechanism
  - Integration test: patient waits >threshold → escalation created
  - A/B tested with real data

### MVP-022: Manual-follow-up exception workflow
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004, MVP-021
- **Acceptance Criteria**:
  - ManualFollowUpException aggregate
  - Background job creates exceptions at deadline
  - Idempotency: one exception per escalation
  - Audit trail immutable
  - Manager dashboard shows exceptions
  - Integration test: escalation not acknowledged → exception created → reviewed

### MVP-023: Escalation visibility dashboard (active, pending, history)
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004, MVP-022
- **UI**: Yes
- **Acceptance Criteria**:
  - EscalationBoardProjection queries designed and indexed
  - Real-time updates (<10s staleness)
  - Dashboard card layout: active, pending, history, metrics
  - Mobile-responsive
  - WCAG AA accessibility
  - Performance: <500ms load with 100+ escalations
  - Integration test: escalation appears within 5s

### MVP-024: Escalation audit trail and status transitions
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004, MVP-023
- **Acceptance Criteria**:
  - Audit events for all transitions (created, acknowledged, resolved, escalated, closed)
  - Immutable audit table
  - Query API: /api/v1/escalations/{id}/audit
  - Audit export functionality
  - Query performance <500ms
  - Integration test: transitions recorded correctly

### MVP-025: Escalation acknowledgement and resolution workflow
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004, MVP-023, MVP-024
- **UI**: Yes
- **Acceptance Criteria**:
  - Commands: AcknowledgeEscalation, ResolveEscalation, ReassignEscalation
  - API endpoints created and documented
  - Events emitted and audit-logged
  - UI workflow approved by clinical staff
  - Integration test: escalation acknowledgement and resolution end-to-end
  - Idempotency verified

---

## MVP-030–035: Paediatric Safety & Safeguarding

**Epic Goal**: Support paediatric-specific workflows, vital signs, safeguarding flags, and trained-staff tracking.

### MVP-030: Age-based workflow routing
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-002 (PatientEpisode)
- **Acceptance Criteria**:
  - Patients age <18 (or site-configurable) routed to paediatric pathways
  - Paediatric-specific fields and assessment templates
  - Age-based validation rules enforced
  - Integration test: child arrival → paediatric workflow

### MVP-031: Paediatric vital-sign reference ranges
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-030
- **Acceptance Criteria**:
  - Vital sign reference ranges by age group (0-3, 3-7, 7-12, 12-18)
  - PECARN triage criteria implemented
  - Observations validated against ranges
  - Alert if vital signs outside normal range
  - Unit tests cover all age groups

### MVP-032: Safeguarding flag workflow and escalation
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-004 (Escalation)
- **Acceptance Criteria**:
  - Any staff member can flag safeguarding concern (NAI, neglect, trafficking, abuse indicators)
  - Safeguarding flag creates escalation to designated safeguarding lead
  - Escalation priority set to critical
  - Audit trail immutable
  - Integration test: staff flags concern → escalation created

### MVP-033: Trained-staff assignment tracking and alerts
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-030
- **Acceptance Criteria**:
  - Track whether paediatric-trained nurse assigned to patient
  - Alert if paediatric patient has observation/task requiring competence but no trained staff available
  - Escalate to charge nurse if skill gap detected
  - Audit log for staff assignments
  - Integration test: paeds task assigned → alert if no training

### MVP-034: Unaccompanied-child alerts
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-030, MVP-002
- **Acceptance Criteria**:
  - Track carer/parent presence (name, relationship)
  - Alert if child <14 (or site-configurable) unaccompanied
  - Escalate to safeguarding lead
  - Audit trail for carer changes
  - Integration test: young child arrives without carer → alert

### MVP-035: Paediatric pain assessment tools (FACES, FLACC)
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-030, MVP-042 (pain assessment)
- **Acceptance Criteria**:
  - FACES scale (0-5) for 3-7 year olds
  - FLACC scale for <3 years
  - Age-appropriate UI for pain entry
  - Pain severity threshold triggers escalation
  - Observations stored with tool type (FACES/FLACC/numeric)
  - Unit tests cover all scales

---

## MVP-040–045: Clinical Observation & Deterioration

**Epic Goal**: Capture structured clinical observations, enable manual deterioration flagging, and support pain assessment.

### MVP-040: Observation entry templates (SBAR, NEWS)
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-002 (PatientEpisode)
- **Acceptance Criteria**:
  - Observation templates: SBAR, NEWS (National Early Warning Score), pain, consciousness, breathing, circulation, mobility
  - Structured data entry (not free-text)
  - Validation: required fields, data type, range
  - Observation metadata: type, value, clinician_id, timestamp, source
  - Unit tests cover all templates

### MVP-041: Manual deterioration flagging and escalation
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-040, MVP-004 (Escalation)
- **Acceptance Criteria**:
  - Staff click "Deteriorating" or "Concerning" button with reason (declining consciousness, increased pain, respiratory distress, etc.)
  - Deterioration flag creates escalation to senior clinician
  - Escalation includes observation summary
  - Audit trail records who flagged and why
  - Unit test: flag created → escalation generated
  - No automatic vital-sign triggers in MVP (manual only)

### MVP-042: Pain assessment capture and escalation
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-040, MVP-004
- **Acceptance Criteria**:
  - Pain assessment template: scale (0-10 numeric or descriptor), location, character, onset
  - Pain severity ≥5 (or site-configurable) creates escalation to assign analgesic review
  - Escalation flagged as "Pain Management" type
  - Pain priority tracked for admission queue (high pain = priority consideration)
  - Unit test: pain ≥5 → escalation created

### MVP-043: Episode timeline view with observation history
- **Type**: Feature
- **Priority**: P1
- **UI**: Yes
- **Dependencies**: MVP-040, MVP-041, MVP-042
- **Acceptance Criteria**:
  - Episode detail view shows observations in chronological order
  - Each observation displays: type, value, time, clinician, source
  - Deterioration flags highlighted
  - Pain assessments highlighted
  - Timeline sortable by type and date
  - Mobile-responsive

### MVP-044: Observation audit trail and modification history
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-040
- **Acceptance Criteria**:
  - All observations audit-logged with clinician_id, timestamp
  - Modifications tracked: original value, modified value, modified_by, modified_at, reason
  - Observations not deleted (soft-delete with audit marker if needed)
  - Audit log queryable by episode_id and observation_type
  - Query performance <500ms

### MVP-045: Risk-flag capture (free-form clinical concerns)
- **Type**: Feature
- **Priority**: P2
- **Dependencies**: MVP-002, MVP-032 (safeguarding)
- **Acceptance Criteria**:
  - Staff can create free-form risk flags (e.g., "social situation concern", "mental health flag")
  - Risk flag stored with: flag_type, reason, severity, created_by, timestamp
  - Risk flags visible on episode view
  - Audit trail for all risk flag changes
  - Optional escalation if severity high

---

## MVP-050–055: Multi-Tenant SaaS & Governance

**Epic Goal**: Ensure database-per-tenant isolation, multi-site support, and configuration change control for safe SaaS operations.

### MVP-050: Database-per-tenant provisioning
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-009
- **Acceptance Criteria**: (see MVP-009)

### MVP-051: Tenant identity resolution and routing
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-007
- **Acceptance Criteria**: (see MVP-007)

### MVP-052: Configuration versioning and approval workflow
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-020 (waiting-time config)
- **Acceptance Criteria**:
  - Configuration entity: threshold values, escalation policies, UI layout, integrations
  - Versioning: config_version, applied_date, applied_by, approver, reason
  - Approval workflow: requester → approver → effective_date
  - Rollback support with audit trail
  - Reject invalid configurations server-side
  - API: /api/v1/config (GET current, POST request change, PATCH apply)

### MVP-053: Site-specific escalation threshold management
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-052
- **Acceptance Criteria**:
  - Each site configures waiting thresholds (4h, 6h, 8h defaults)
  - Thresholds versioned and change-controlled
  - Can enable/disable escalation types per site
  - Configuration import/export for migration
  - Approval workflow enforced

### MVP-054: Multi-site support and regional configuration
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-050, MVP-051, MVP-052
- **Acceptance Criteria**:
  - Multi-site organizations supported (e.g., NHS Trust with 2-3 sites)
  - Per-site data isolation (database-per-site or logical partitioning)
  - Regional configuration (England vs Wales, local language, local procedures)
  - Site administrators manage own configuration
  - Corporate dashboard shows all sites
  - Audit trail tracks site-level changes

### MVP-055: Tenant-scoped audit logging and compliance events
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-010 (audit), MVP-051 (tenant routing)
- **Acceptance Criteria**: (covered by MVP-010 with tenant isolation)

---

## MVP-060–070: API, Integration, & Authentication

**Epic Goal**: Deliver versioned REST API, webhook contracts, OAuth2/OpenID Connect authentication, and EPR integration adapters.

### MVP-060: REST API v1 design and OpenAPI spec
- **Type**: Tech-Debt
- **Priority**: P0
- **Acceptance Criteria**:
  - OpenAPI 3.0 spec created
  - Versioning strategy documented
  - Endpoint categories: commands, queries, webhooks
  - Error handling (RFC 9457 problem details)
  - Authentication (OAuth2/OpenID)
  - Spec reviewed and approved

### MVP-061: Command submission endpoint (/api/v1/commands)
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-006 (command handlers), MVP-060
- **Acceptance Criteria**:
  - POST /api/v1/commands endpoint
  - Command envelope: tenant, actor, command_id, idempotency_key, expected_version, correlation_id
  - Validation: actor authorization, preconditions
  - Response: command_id, status, result (success/conflict/error)
  - Idempotency: same idempotency_key returns same result
  - Unit and integration tests pass

### MVP-062: Episode query endpoints (/api/v1/episodes/{id})
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-002 (PatientEpisode), MVP-060
- **Acceptance Criteria**:
  - GET /api/v1/episodes/{id} returns episode detail
  - GET /api/v1/boards/waiting returns waiting board projection
  - Filtering: by location, acuity, age, time_in_waiting
  - Pagination: cursor-based, configurable page size
  - Performance: <150ms median latency, p95/p99 <500ms
  - Authorization: viewer can only see own tenant

### MVP-063: Webhook contract and inbound mapping
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-060, MVP-006
- **Acceptance Criteria**:
  - Inbound webhook endpoint: /api/v1/webhooks/inbound
  - HMAC signature verification
  - Timestamp/replay protection (±5 min window)
  - Maps external payload to domain commands (normalizer)
  - Idempotency: webhook_id for deduplication
  - Documentation: webhook schema and examples

### MVP-064: Outbound escalation/event webhooks
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-004 (Escalation), MVP-060
- **Acceptance Criteria**:
  - Webhook registration: POST /api/v1/webhooks/register
  - Outbound events: EscalationRaised, EscalationAcknowledged, PatientMoved, EscalationResolved
  - Signed JSON payloads (HMAC)
  - Delivery retry: exponential backoff, dead-letter queue
  - Subscriber can verify webhook signature
  - Integration test: escalation → webhook delivery with retry

### MVP-065: OAuth2/OpenID Connect authentication
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-060, MVP-065
- **Acceptance Criteria**:
  - Issuer configuration (e.g., Azure AD, NHS Identity Provider)
  - JWT token validation (signature, expiry, claims)
  - Claims mapping: tenant_id, user_id, roles, permissions
  - Middleware: extract tenant from JWT claims
  - Reject mismatched tenant_id in command
  - Integration test: valid token → request allowed, invalid → 401

### MVP-066: FHIR/HL7 integration adapter (EPR/ambulance)
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-063 (inbound webhooks)
- **Acceptance Criteria**:
  - FHIR resource mappings: Patient, Encounter, Observation
  - HL7 v2.5 message mappings: ADT (arrival/discharge), OBR (observations)
  - Arrival message (A01) → RegisterArrival command
  - Discharge message (A03) → MarkDischarge command
  - Error handling: invalid syntax logs and creates alert
  - Sample EPR messages provided for testing

### MVP-067: REST integration adapter for third-party systems
- **Type**: Feature
- **Priority**: P2
- **Dependencies**: MVP-063
- **Acceptance Criteria**:
  - Generic REST inbound adapter (map external endpoints)
  - Configuration: endpoint URL, polling interval, response schema, command mapping
  - Retry and error handling
  - Sample integrations documented

### MVP-068: API error handling (RFC 9457 problem details)
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-060, MVP-061
- **Acceptance Criteria**:
  - Error responses in RFC 9457 format (application/problem+json)
  - Error codes: stable, documented (e.g., TENANT_MISMATCH, VALIDATION_ERROR)
  - Message, detail, instance, extensions included
  - All 4xx/5xx responses use problem details
  - Swagger/OpenAPI documents error schemas

### MVP-069: API documentation and .NET client SDK
- **Type**: Documentation + Feature
- **Priority**: P2
- **Dependencies**: MVP-060, MVP-061, MVP-062
- **Acceptance Criteria**:
  - Swagger/OpenAPI UI (/swagger)
  - API docs in Markdown (examples, authentication, versioning)
  - .NET client library (NuGet package)
  - Client code examples
  - SDK tested with integration tests

### MVP-070: API versioning strategy and backward compatibility
- **Type**: Tech-Debt
- **Priority**: P1
- **Dependencies**: MVP-060
- **Acceptance Criteria**:
  - Versioning strategy documented (URL path vs header)
  - Backward compatibility rules (no breaking changes in v1)
  - Migration plan for v1 → v2 (if needed)
  - Database schema versioning strategy (expand/contract)
  - Event versioning for forward/backward compat

---

## MVP-080–095: UI/UX – Waiting Board & Dashboards

**Epic Goal**: Build task-oriented, accessible UI for waiting board, escalation dashboard, and power-user configuration workspace.

### MVP-080: Waiting board dashboard (lanes, filters, columns)
- **Type**: Feature + UI
- **Priority**: P0
- **Dependencies**: MVP-023 (escalation visibility)
- **Acceptance Criteria**:
  - Kanban-style board: lanes by location (waiting room, triage, resus, beds)
  - Cards per patient: name/ID, arrival time, elapsed time, acuity, escalations
  - Filters: acuity, age, escalation status, time in waiting
  - Real-time updates (<10s)
  - Mobile-responsive
  - WCAG AA accessibility
  - Print/export functionality

### MVP-081: Escalation visibility board (active, pending, history)
- **Type**: Feature + UI
- **Priority**: P0
- **Dependencies**: MVP-023 (escalation projection)
- **Acceptance Criteria**:
  - Same as MVP-023 but UI-focused
  - Cards showing active escalations (assigned, deadline)
  - "Pending Acknowledgement" column
  - History sidebar (7-day resolved escalations)
  - Metrics summary (count, avg ack time)
  - Real-time updates

### MVP-082: Episode detail view (patient summary, observations, escalations)
- **Type**: Feature + UI
- **Priority**: P0
- **Dependencies**: MVP-043 (observation timeline), MVP-025 (escalation workflow)
- **Acceptance Criteria**:
  - Modal/page showing episode detail
  - Summary: patient ID, arrival time, current location, acuity
  - Tabs: observations, escalations, risk flags
  - Observation timeline (chronological)
  - Escalation list with status and actions
  - Action buttons: acknowledge escalation, resolve, reassign, flag deterioration, add observation
  - Mobile-responsive

### MVP-083: Power-user editor for board configuration
- **Type**: Feature + UI
- **Priority**: P1
- **Dependencies**: MVP-052 (configuration)
- **Acceptance Criteria**:
  - Drag-drop board layout editor
  - Configure lanes and columns
  - Threshold editor (waiting time, pain, etc.)
  - Colour rules (e.g., pain >5 = red)
  - Save as named "view"
  - Apply globally or per-user
  - Preview before save

### MVP-084: Configuration preview and approval workflow (UI)
- **Type**: Feature + UI
- **Priority**: P1
- **Dependencies**: MVP-052 (config workflow)
- **Acceptance Criteria**:
  - Admin requests config change (UI form)
  - Shows current vs proposed config
  - Manager approves/rejects (email/in-app notification)
  - Effective date scheduler
  - Shows audit trail of changes
  - Rollback button (if authorized)

### MVP-085: Patient search and filtering
- **Type**: Feature + UI
- **Priority**: P1
- **Dependencies**: MVP-062 (query endpoints)
- **Acceptance Criteria**:
  - Search box: by NHS number, name, MRN
  - Typeahead/autocomplete
  - Advanced filters: date range, location, acuity, waiting time
  - Results paginated and sortable
  - Performance: <500ms for typical queries

### MVP-086: Role-based access control (RBAC) enforcement in UI
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-065 (OAuth), MVP-025 (escalation workflow)
- **Acceptance Criteria**:
  - UI elements hidden if user lacking permission
  - Action buttons disabled for unauthorized roles
  - Waiting-room coordinator: sees waiting escalations only
  - Triage nurse: sees triage/clinical escalations
  - Resus team lead: sees resus escalations
  - Manager: sees all escalations + exceptions
  - Audit logging for denied actions attempted

### MVP-087: WCAG AA accessibility compliance
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: All UI issues (MVP-080 onwards)
- **Acceptance Criteria**:
  - Keyboard-first operation (no mouse-only)
  - Focus order logical and visible
  - Screen-reader announcements for dynamic content
  - Colour contrasts meet WCAG AA (4.5:1 for text)
  - Form labels associated with inputs
  - Automated accessibility testing (axe, WAVE)
  - Manual accessibility audit passed

### MVP-088: Welsh language support and content
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: All UI issues
- **Acceptance Criteria**:
  - UI supports Welsh (i18n framework implementation)
  - All text strings translated to Welsh
  - Welsh legal content (terms, privacy) provided
  - Language toggle in header
  - Active offer of Welsh (not passive)
  - QA testing with Welsh-speaking staff

### MVP-089: Responsive design for desktop and clinical devices
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: All UI issues
- **Acceptance Criteria**:
  - Responsive breakpoints: desktop, tablet, mobile
  - Touch-friendly button sizes (minimum 44x44px)
  - Portrait and landscape orientations supported
  - Performance optimized for 4G networks
  - Tested on iPhone, iPad, Android, Windows tablets

### MVP-090: Real-time board updates (WebSocket or polling)
- **Type**: Feature
- **Priority**: P0
- **Dependencies**: MVP-023 (escalation projection), MVP-080 (waiting board)
- **Acceptance Criteria**:
  - Real-time updates <10s staleness
  - WebSocket connection for live updates (or fallback polling)
  - Connection loss handling (graceful degradation)
  - Reconnect logic with backoff
  - Notification badge for new escalations
  - Performance: minimal CPU/memory impact

### MVP-091: Print and export functionality
- **Type**: Feature
- **Priority**: P2
- **Dependencies**: MVP-080, MVP-081
- **Acceptance Criteria**:
  - Print board to PDF
  - Export escalations to CSV (for shift handoff)
  - Export observation history to PDF (clinician report)
  - Print-friendly layout (no PHI in headers/footers where public)

### MVP-092: User feedback and inline help
- **Type**: Feature
- **Priority**: P2
- **Dependencies**: All UI
- **Acceptance Criteria**:
  - Feedback form (in-app button → modal for user comments/bug reports)
  - Inline help tooltips on key UI elements
  - Context-sensitive help (F1 or ? button)
  - User guide link (PDF or wiki)
  - Support contact info displayed

### MVP-093: Session timeout and workstation handoff
- **Type**: Feature
- **Priority**: P1
- **Dependencies**: MVP-065 (OAuth)
- **Acceptance Criteria**:
  - Configurable session timeout (e.g., 30 min inactivity)
  - Warning modal before timeout (5 min)
  - Logout and redirect to login
  - Workstation handoff: "Log out" button → next user login
  - Audit log: who logged in/out and when

### MVP-094: UI theming and site-specific customization
- **Type**: Feature
- **Priority**: P2
- **Dependencies**: MVP-083 (board editor)
- **Acceptance Criteria**:
  - Site logo and branding (header, favicon)
  - Colour scheme customization per site (brand colours)
  - Layout customization (power-user saved views)
  - Configuration stored per tenant

### MVP-095: UI testing and QA automation
- **Type**: Feature + Testing
- **Priority**: P1
- **Dependencies**: All UI
- **Acceptance Criteria**:
  - Selenium/Playwright end-to-end tests for key workflows
  - Visual regression testing (Percy or similar)
  - Accessibility automation (axe-core CI integration)
  - Manual QA checklist (cross-browser, devices)
  - Performance testing (Lighthouse)

---

## MVP-100–115: Testing, Deployment, & Operations

**Epic Goal**: Establish end-to-end testing, CI/CD pipelines, deployment automation, and operational observability.

### MVP-100: Unit testing framework and domain logic tests
- **Type**: Testing
- **Priority**: P0
- **Dependencies**: All domain issues (MVP-001 onwards)
- **Target Coverage**: ≥80% of domain logic
- **Acceptance Criteria**:
  - xUnit or NUnit framework chosen
  - Tests for all aggregate state machines
  - Tests for all commands and validations
  - Tests for event schema versioning
  - Test doubles (fakes, mocks) documented
  - CI integration with coverage reports

### MVP-101: Integration testing (API, persistence, outbox)
- **Type**: Testing
- **Priority**: P0
- **Dependencies**: MVP-005 (event log), MVP-061 (API)
- **Acceptance Criteria**:
  - SQLite or in-memory test database
  - Integration tests for command → event → database flow
  - Tests for outbox processing and idempotency
  - Tests for tenant isolation at database layer
  - API integration tests: command submission → response validation
  - Database transaction rollback testing

### MVP-102: BDD acceptance tests (Gherkin scenarios)
- **Type**: Testing
- **Priority**: P1
- **Dependencies**: MVP-100, MVP-101
- **Framework**: SpecFlow or similar
- **Acceptance Criteria**:
  - Feature files written by QA/Product in Gherkin
  - Scenarios for waiting-time escalation workflow
  - Scenarios for paediatric flows
  - Scenarios for deterioration flagging
  - Step definitions linked to domain code
  - Automated test runs in CI

### MVP-103: API contract tests (webhooks, third-party integrations)
- **Type**: Testing
- **Priority**: P1
- **Dependencies**: MVP-063 (webhooks), MVP-066 (FHIR)
- **Acceptance Criteria**:
  - Pact or similar contract testing framework
  - Contracts for outbound webhooks defined
  - Contracts for EPR/ambulance integration
  - Mock external systems for testing
  - Contract verification in CI

### MVP-104: Database migration testing and rollback
- **Type**: Testing
- **Priority**: P0
- **Dependencies**: MVP-009 (migrations)
- **Acceptance Criteria**:
  - Automated tests: forward and rollback migrations
  - Tests on multiple database versions (if applicable)
  - Zero-downtime migration validation (if supported)
  - Data integrity checks after migration
  - Documented rollback procedures

### MVP-105: CI/CD pipeline (GitHub Actions)
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: MVP-100, MVP-101, MVP-104
- **Acceptance Criteria**:
  - GitHub Actions workflow triggered on PR and push
  - Steps: compile, test (unit + integration), build, publish
  - Artifact management (NuGet, Docker images)
  - Approval gate before production deployment
  - Notifications (Slack/email on success/failure)
  - Pipeline documentation

### MVP-106: Staging environment deployment automation
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: MVP-105
- **Acceptance Criteria**:
  - Automated deployment to staging on push to main
  - Database migrations applied automatically
  - Health checks post-deployment
  - Smoke tests run (basic API availability)
  - Rollback on health check failures
  - Documentation for manual rollback

### MVP-107: Production deployment (blue/green or canary)
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: MVP-106
- **Acceptance Criteria**:
  - Manual approval required for production deployment
  - Blue/green or canary deployment strategy documented
  - Traffic gradually shifted (0% → 100%)
  - Health checks and monitoring during rollout
  - Rollback procedure if issues detected
  - Deployment runbook and checklist

### MVP-108: Database backup, restore, and point-in-time recovery
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: MVP-009 (provisioning)
- **Acceptance Criteria**:
  - Automated backups (e.g., daily snapshots)
  - Encrypted backup storage
  - Restore testing: monthly verification of point-in-time restore
  - RPO: ≤5 minutes
  - RTO: ≤30 minutes
  - Restore runbook documented

### MVP-109: Observability (metrics, logs, tracing)
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: All components
- **Acceptance Criteria**:
  - Structured logging (JSON format)
  - Correlation IDs across requests
  - Metrics: request latency, error rate, throughput, event lag
  - Tracing: OpenTelemetry or similar
  - Log aggregation (e.g., Application Insights, ELK)
  - Dashboards for key metrics
  - PHI redaction in logs

### MVP-110: Alerting and incident response procedures
- **Type**: Ops
- **Priority**: P0
- **Dependencies**: MVP-109
- **Acceptance Criteria**:
  - Alert rules configured (error rate >1%, latency p95 >500ms, etc.)
  - Alerts sent to on-call team (PagerDuty or similar)
  - Incident runbook linked from alert
  - Escalation procedures documented
  - Postmortem process for incidents
  - Blameless culture documented

### MVP-111: Performance testing and tuning (p95/p99 latency)
- **Type**: Testing
- **Priority**: P1
- **Dependencies**: MVP-062 (API)
- **Targets**: Median <150ms, p95 <500ms, p99 <1000ms
- **Acceptance Criteria**:
  - Load testing with representative patient volumes
  - Query performance: waiting board load <500ms
  - API endpoint latency measured and tuned
  - Database indexes optimized
  - Caching strategy for read projections (if needed)
  - Performance test results documented

### MVP-112: Security testing and penetration testing
- **Type**: Security
- **Priority**: P0
- **Dependencies**: All components
- **Acceptance Criteria**:
  - OWASP Top 10 risks addressed
  - SQL injection prevention verified (parameterized queries)
  - Authentication and authorization tested
  - Cross-tenant isolation tested
  - SSL/TLS certificate management
  - Dependency scanner for vulnerable packages
  - Penetration test report completed

### MVP-113: DSPT compliance checklist
- **Type**: Compliance
- **Priority**: P0
- **Dependencies**: MVP-010 (audit), MVP-109 (monitoring)
- **Acceptance Criteria**:
  - DSPT Self-Assessment Toolkit completed
  - Evidence gathered for each requirement (policies, logs, certificates)
  - Audit logging meets NHS standards
  - Backup and recovery procedures documented
  - Incident response plan in place
  - DSPT submission prepared

### MVP-114: Runbook and operations documentation
- **Type**: Documentation
- **Priority**: P0
- **Dependencies**: MVP-107, MVP-108, MVP-109
- **Acceptance Criteria**:
  - Runbook: deployment, rollback, backup/restore, incident response
  - Role templates: waiting-room coordinator, triage nurse, manager, admin
  - Troubleshooting guide
  - Command reference for common issues
  - On-call procedures and escalation
  - Weekly rotation schedule template

### MVP-115: Training materials and role-specific procedures
- **Type**: Documentation
- **Priority**: P0
- **Dependencies**: MVP-114
- **Acceptance Criteria**:
  - Role-based training materials (clinician, coordinator, manager, admin)
  - Video walkthroughs (waiting board, escalation workflow)
  - Quick-start guides (1-page cheat sheets per role)
  - Q&A and FAQs
  - Feedback collection from pilot users
  - Training completion attestation process

---

## Issue Labels & Templates

### Standard Labels
- **Priority**: P0 (CLI blocking), P1 (high), P2 (medium), P3 (nice-to-have)
- **Type**: Feature, Bug, Tech-Debt, Documentation
- **Component**: Core, API, UI, Integration, Testing, Ops
- **Phase**: MVP, v1.1, v2.0, v3.0
- **Epic**: Core-Engine, Escalation, Paediatric, Clinical, Multi-Tenant, API, UI, Testing, Ops

### Example PR/Issue Template
```markdown
## Description
[Clear problem statement or feature description]

## Acceptance Criteria
- [ ] Criterion 1
- [ ] Criterion 2
- [ ] Criterion 3

## Technical Details
[Architecture, design decisions, dependencies]

## Related Issues
- Related #123
- Blocks #456
- Relates to design/spec.md Section X

## Refs
[ED Inspection findings, spec mapping, etc.]
```

---

## How to Create Issues in GitHub

### Option 1: Using GitHub CLI (Recommended)
```bash
# Run the script (after filling in all issues)
.\create-issues.ps1

# Or manually:
gh issue create --title "MVP-001 – ..." --body "..." --label "MVP,Core" --milestone "MVP Phase 0"
```

### Option 2: Bulk Import via CSV
[Prepare CSV with columns: title, body, labels, assignee]

### Option 3: Manual Creation in GitHub Web UI
[Create via github.com/gwhitdev/betsi/issues -> New Issue]

---

## Tracking & Kanban Workflow

Use **GitHub Projects** (Kanban board) with columns:
- **Backlog**: New issues awaiting prioritization
- **Ready**: Refined issues with clear acceptance criteria
- **In Progress**: Issues being worked (assign to team member)
- **Review**: PR submitted, awaiting code review
- **Testing**: Merged to staging, awaiting QA
- **Done**: Merged to main, released to production

---

**Last Updated**: 2025-01-31  
**Total MVP Issues**: 115  
**Estimated MVP Duration**: 30 days (4-week sprint)

For questions or modifications, contact the Betsi Patient Flow team.
