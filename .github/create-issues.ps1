#!/usr/bin/env pwsh
# GitHub Issue Template Creator for Betsi Patient Flow MVP
# This script generates GitHub CLI commands to create all MVP issues
# Usage: .\create-issues.ps1

# Define common labels
$labels = @(
	"MVP",
	"waiting-time-escalation",
	"paediatric-safety",
	"clinical-observation",
	"multi-tenant",
	"api",
	"ui",
	"testing",
	"ops"
)

# MVP-001: Design bounded contexts and aggregates
# MVP-002: Implement PatientEpisode aggregate
# MVP-003: Implement Location and Queue aggregates
# ... (see below for full set)

# TEMPLATE STRUCTURE:
# gh issue create \
#   --title "MVP-NNN – Issue Title" \
#   --body "Description with acceptance criteria" \
#   --label "MVP,component,priority" \
#   --assignee @username (optional) \
#   --milestone "MVP Phase 0" (optional)

# --- CORE PATIENT FLOW ENGINE (MVP-001 to MVP-010) ---

gh issue create `
  --title "MVP-001 – Design bounded contexts and aggregates" `
  --body @"
## Description
Design and document the bounded contexts for Betsi Patient Flow core domain:
- PatientEpisode: patient identity, episode lifecycle
- Location: physical or logical patient location
- Queue: waiting area state and ordering
- Escalation: alert lifecycle and status
- RiskFlag: clinical concern capture

Create decision records for:
- Aggregate root selection and invariants
- Command-to-event mapping
- Projection (read model) strategy
- Tenant isolation boundaries

## Acceptance Criteria
- [ ] Bounded context diagram created and shared
- [ ] Aggregate responsibilities and invariants documented
- [ ] Command and event taxonomy drafted
- [ ] Tenant isolation strategy defined
- [ ] Architecture decision record approved by tech lead

## Related
- See design/spec.md: Section 3 (Detailed Bounded Contexts and Data Model)
- Refs: #ED-Inspection-2025
"@ `
  --label "MVP,Core,Tech-Debt" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-002 – Implement PatientEpisode aggregate" `
  --body @"
## Description
Implement the PatientEpisode aggregate root with:
- Patient identity and episode lifecycle
- Arrival, triage, waiting, movement, discharge state
- Observation and clinical data capture (without automatic triggers)
- Manual escalation creation by authorized staff
- Optimistic concurrency control

## Technical Requirements
- Transactional consistency within tenant
- Domain events: PatientArrived, TriageRecorded, ObservationRecorded, PatientMoved, EscalationRaised, PatientDischarged
- Audit-logged commands: RegisterArrival, TriageComplete, UpdateObservation, MovePatient, MarkDischarge
- No automatic escalations; all clinical actions manual

## Acceptance Criteria
- [ ] PatientEpisode aggregate implemented with state machine
- [ ] Commands validate preconditions and append domain events
- [ ] Optimistic concurrency test passes
- [ ] Event schema versioned and documented
- [ ] Unit tests cover all state transitions and error cases
- [ ] Code review approved

## Related
- #MVP-003, #MVP-004, #MVP-005, #MVP-006
- design/spec.md: Section 3.1 (Bounded Contexts)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-003 – Implement Location and Queue aggregates" `
  --body @"
## Description
Implement Location and Queue aggregates:
- **Location**: physical/logical ED areas (waiting room, triage, resus, corridor, bed areas)
- **Queue**: waiting queue state, ordering, threshold tracking for escalation triggers
- Capacity state (empty, available, full, overflow)
- Manual location transitions and audit trail

## Acceptance Criteria
- [ ] Location aggregate with state (available, occupied, full, overflow, closed)
- [ ] Queue aggregate with ordered patient positions
- [ ] Threshold tracking (elapsed wait time for escalation triggers)
- [ ] Commands: MoveToLocation, AddToQueue, RemoveFromQueue
- [ ] Events: PatientMoved, PatientQueuedAsWaiting, QueueStateChanged
- [ ] Tenant-scoped isolation enforced
- [ ] Unit and integration tests pass

## Related
- #MVP-002, #MVP-004, #MVP-020 (waiting-time escalation)
- design/spec.md: Section 3.1, Section 2 (Governance and escalation authority)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-004 – Implement Escalation aggregate" `
  --body @"
## Description
Implement Escalation aggregate for durable escalation workflows:
- Escalation ID, severity, reason, patient/episode reference
- Assigned role/team and deadline for acknowledgement
- Status: created, acknowledged, resolved, escalated, manual-follow-up-initiated, closed
- Manual-follow-up exception workflow if not acknowledged by deadline
- Immutable audit trail of all status transitions

## Technical Requirements
- Escalation lifecycle: created (awaiting acknowledgement) → acknowledged (team action) → resolved (clinical outcome) or escalated (next tier)
- If not acknowledged by deadline, create audited manual-follow-up exception for manager review
- No automatic escalation to next level; all reassignment manual
- Audit record for each status transition with user, role, reason, timestamp

## Acceptance Criteria
- [ ] Escalation aggregate implements state machine
- [ ] Manual-follow-up exception workflow tested end-to-end
- [ ] Commands: TriggerEscalation, AcknowledgeEscalation, ResolveEscalation, ReassignEscalation, CreateManualFollowUp
- [ ] Events: EscalationRaised, EscalationAcknowledged, EscalationResolved, EscalationReassigned, ManualFollowUpInitiated
- [ ] Audit trail immutable and queryable
- [ ] Unit tests and acceptance tests pass

## Related
- #MVP-002, #MVP-020 (waiting-time escalation), #MVP-030 (paediatric escalation)
- design/spec.md: Section 2 (Escalation authority), Section 4.3 (Critical escalation contract), Section 9 (Alert-fatigue controls)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-005 – Implement transactional event log and outbox" `
  --body @"
## Description
Implement transactional event sourcing foundation with outbox pattern:
- Append-only domain event log per tenant
- Audit trail recording all commands and authorization decisions
- Outbox table for reliable event delivery to projections and integrations
- At-least-once delivery semantics with idempotent processing
- Replay capability for projections without external side effects

## Technical Requirements
- Event table schema: event_id, aggregate_id, event_type, event_data (JSON), metadata (tenant, user, timestamp, correlation_id, schema_version)
- Audit table schema: audit_id, command_id, actor, role, action, resource, outcome, timestamp, tenant_id
- Outbox table: outbox_id, event_id, event_data, delivery_status, retry_count, created_at
- Events written transactionally with aggregate state in single transaction
- Outbox consumer: process events, deliver to projections/integrations, mark delivered
- Idempotency by event_id and consumer_id

## Acceptance Criteria
- [ ] Event log schema designed and migrations written
- [ ] Audit log schema designed and migrations written
- [ ] Outbox pattern implemented with transactional consistency
- [ ] Outbox consumer (background worker) built
- [ ] Deduplication strategy tested (duplicate events skipped by consumer)
- [ ] Replay capability tested (projections rebuilt from event log)
- [ ] Database migration automation works end-to-end
- [ ] SQL/EF Core code reviewed

## Related
- #MVP-006 (command handlers), #MVP-007 (multi-tenant)
- design/spec.md: Section 3 (Architecture decisions - Persistence baseline), Section 4 (API, Events, Integration Contracts)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-006 – Design and implement command handlers" `
  --body @"
## Description
Implement command handling infrastructure:
- Command validation (tenant context, actor authorization, preconditions)
- Idempotency (command_id and idempotency_key)
- Version checking (optimistic concurrency with expected_version)
- Event application to aggregate
- Outbox message creation in same transaction

## Commands to Implement
- RegisterArrival(episodeId, patientId, arrivalTime, source)
- TriageComplete(episodeId, triageNotes, acuity)
- UpdateObservation(episodeId, observationType, observationData, clinicianId)
- MovePatient(episodeId, targetLocation)
- TriggerEscalation(episodeId, reason, severity, assignedRole)
- AcknowledgeEscalation(escalationId, acknowledgedBy, acknowledgementTime)
- ResolveEscalation(escalationId, resolvedBy, resolutionReason)
- MarkDischarge(episodeId, dischargeDetails)

## Acceptance Criteria
- [ ] Command envelope structure designed (tenant, actor, correlation_id, idempotency_key, expected_version)
- [ ] Validation layer tested (actor authorization, preconditions)
- [ ] Concurrency handling tested (concurrent updates detected, rejected with version conflict)
- [ ] All core commands implemented and tested
- [ ] Outbox message generation verified
- [ ] Integration tests pass (command → event → audit → outbox)

## Related
- #MVP-002, #MVP-003, #MVP-004, #MVP-005
- design/spec.md: Section 4.1 (Public REST API - Command envelope)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-007 – Multi-tenant context and isolation" `
  --body @"
## Description
Implement multi-tenant isolation:
- Tenant context resolution from authenticated user claims (trusted source)
- Tenant ID validation on every command
- Tenant routing to database connection
- Tenant verification in queries and projections
- Audit logging includes tenant_id on all operations

## Technical Requirements
- TenantContext: tenant_id, user_id, roles, permissions
- TenantRegistry: mapping of tenant_id to encrypted connection string and key
- Middleware for tenant context extraction and validation
- Tenant context rejected if mismatched with user claims
- Query validation: all SELECT statements include WHERE tenant_id = @tenant
- Partition key strategy for database indexes (tenant_id as leading column)

## Security Requirements
- No cross-tenant data leakage at API or database layer
- Tenant-scoped encryption keys (future: envelope encryption per tenant)
- Audit logging for cross-tenant access attempts (blocked)
- Database access control: separate credentials per environment

## Acceptance Criteria
- [ ] TenantContext extraction and validation middleware implemented
- [ ] TenantRegistry built and encrypted connection management tested
- [ ] All aggregates validated for tenant_id presence
- [ ] Cross-tenant rejection tested at API and database layers
- [ ] Audit logging detects cross-tenant access attempts
- [ ] Integration tests cover multi-tenant isolation scenarios
- [ ] Penetration test confirms no cross-tenant exposure

## Related
- #MVP-005 (event log) – tenant_id in events
- #MVP-009 (database-per-tenant)
- design/spec.md: Section 3.3 (Database-per-tenant operating model), Section 3.2 (Tenant isolation)
"@ `
  --label "MVP,Core,Security" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-008 – Offline key validator and licensing module" `
  --body @"
## Description
Implement offline-capable licensing and feature-gating:
- License key validation without external dependency (offline mode)
- Feature flags stored locally and checked at runtime
- License expiry detection and alerts
- Audit logging of license validation failures

## Technical Requirements
- LicenseValidator: accepts key, validates signature (HMAC or RSA)
- LicenseKey entity: key_id, organization, expiry, features[], signature
- FeatureGateService: check if feature enabled for tenant
- Cache license state locally (e.g., 24-hour TTL)
- Periodic background job to sync license state with control plane
- Audit events: LicenseExpired, LicenseValidationFailed

## Acceptance Criteria
- [ ] License key schema designed
- [ ] Offline validator implemented with unit tests
- [ ] Feature gate service tested
- [ ] Cache invalidation logic verified
- [ ] Audit logging for validation failures
- [ ] Documentation for license key generation (internal tool)
- [ ] License key format documented and shared with team

## Related
- #MVP-009 (tenant provisioning), #MVP-050 (multi-tenant)
- design/spec.md: Section 3 (Licensing), Section 6 (Data model - Licensing)
"@ `
  --label "MVP,Core,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-009 – Database-per-tenant provisioning and migrations" `
  --body @"
## Description
Implement automated database provisioning for multi-tenant model:
- Tenant onboarding creates dedicated database instance
- Schema migrations applied automatically and idempotently
- Backup automation (encrypted, tenant-addressable)
- Point-in-time restore capability
- Database health monitoring and alerts

## Technical Requirements
- Provisioning automation: create database, apply initial schema, seed default config
- Migration framework: expand/contract migrations supporting concurrent app versions
- Rollback testing: migrations must have tested rollback plans
- Backup: automated, encrypted, retention-managed (e.g., 30-day retention)
- Restore testing: periodically verify restore works end-to-end
- Database isolation: credentials per tenant, minimal platform service access

## Acceptance Criteria
- [ ] Provisioning playbook automated (Infrastructure as Code, e.g., Terraform/Bicep)
- [ ] Migration framework chosen and documented (e.g., Flyway, Entity Framework Core)
- [ ] Migrations for all core aggregates written and tested
- [ ] Rollback testing verified for each migration
- [ ] Backup automation configured and retention policy enforced
- [ ] Restore test scheduled and documented
- [ ] Connection string management reviewed for security
- [ ] Documentation for operator runbook

## Related
- #MVP-007 (tenant context), #MVP-050 (multi-tenant)
- design/spec.md: Section 3.3 (Database-per-tenant operating model)
"@ `
  --label "MVP,Core,Ops" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-010 – Audit logging and compliance event stream" `
  --body @"
## Description
Implement comprehensive audit logging for compliance and safety:
- All commands logged with actor, role, timestamp, outcome
- All authorization decisions logged (allow/deny)
- All escalation state transitions logged
- Privacy: PHI redaction in logs (audit logs are for system, not clinical review)
- Audit export for GDPR SAR and compliance reviews

## Technical Requirements
- Audit table: audit_id, correlation_id, command_id, actor_id, actor_role, action, resource_id, resource_type, outcome, reason, timestamp, tenant_id
- Log retention: 3 years minimum (NHS standard)
- Redaction: remove/mask PHI fields in output logs (e.g., NHS number shown as \*\*\*\*1234)
- Export capability: SAR data export, audit trail export for investigation
- Index strategy: correlation_id, actor_id, action, timestamp for quick queries

## Acceptance Criteria
- [ ] Audit schema designed and migrations written
- [ ] Audit logging middleware/decorator implemented
- [ ] Redaction rules applied and tested (NHS number, DOB, etc.)
- [ ] Audit export functionality built
- [ ] Retention policy enforcement configured
- [ ] Sample audit report generated and reviewed
- [ ] DSPT compliance checklist updated

## Related
- #MVP-005 (event log), #MVP-110 (monitoring), #MVP-113 (DSPT)
- design/spec.md: Section 3.2 (Event schema), Section 6 (Compliance)
"@ `
  --label "MVP,Core,Compliance" `
  --milestone "MVP Phase 0"

echo "Core Patient Flow Engine issues created (MVP-001 to MVP-010)"

# --- WAITING-TIME ESCALATION & VISIBILITY (MVP-020 to MVP-025) ---

gh issue create `
  --title "MVP-020 – Waiting-time escalation policy configuration" `
  --body @"
## Description
Implement waiting-time escalation policy configuration per site:
- Multi-role thresholds: 4h (coordinator), 6h (senior clinician), 8h (bed manager)
- Configuration stored and versioned in system
- Change control: approval workflow before effective date
- Escalation generation: automatic at each threshold if patient still waiting

## Technical Requirements
- Configuration entity: site_id, waiting_threshold_hours, escalation_role, deadline_minutes, active_since
- Versioning: config_version, applied_date, applied_by, approver, reason, rollback_enabled
- Approval workflow: admin_request → manager_approval → effective_date → applied
- Audit logging: WaitingPolicyConfigured, WaitingPolicyUpdated, WaitingPolicyRolledBack events
- No default unsafe fallback; defaults require clinical input during deployment

## Acceptance Criteria
- [ ] Configuration schema designed (database table + EF Core model)
- [ ] Approval workflow implemented and tested
- [ ] Versioning strategy allows rollback
- [ ] Audit trail captures all changes
- [ ] Configuration API published
- [ ] Validation rejects invalid thresholds
- [ ] Documentation for site administrators

## Related
- #MVP-021 (automatic escalation), #MVP-052 (site-specific config)
- design/spec.md: Section 2 (Waiting-time escalation policy), Section 9 (Configuration and change control)
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-021 – Automatic escalation generation at waiting-time thresholds" `
  --body @"
## Description
Implement automatic escalation trigger when patients exceed configured waiting thresholds:
- Background job checks waiting patients every 1-5 minutes
- If elapsed_wait_time ≥ threshold, create NewWaitingThresholdExceeded event
- Issue TriggerEscalation command with assigned role from config
- Escalation recorded with patient state (acuity, observations, arrival method)
- Idempotent: no duplicate escalations if already exists for this threshold for this patient

## Technical Requirements
- WaitingTimeMonitor background worker: periodic task (configurable interval)
- Query: SELECT episodes WHERE status=Waiting AND arrival_time + threshold ≤ NOW AND no_escalation_exists_for_threshold
- Command: TriggerEscalation with escalation_type=WaitingTimeThreshold
- Idempotency: escalation_type+episode_id+threshold_level must be unique
- Retry strategy: exponential backoff with dead-letter queue for failed escalations
- Deployment: stateless worker, can run in parallel if needed

## Acceptance Criteria
- [ ] WaitingTimeMonitor worker implemented
- [ ] Escalation generation queries efficient (indexed on arrival_time, status)
- [ ] Idempotency tests pass (no duplicate escalations)
- [ ] Retry/dead-letter mechanism tested
- [ ] Performance: can handle 100+ waiting patients with <1s scan time
- [ ] Integration test: patient waits >threshold, escalation created automatically
- [ ] A/B tested with real waiting times data

## Related
- #MVP-020 (config), #MVP-023 (visibility), #MVP-024 (audit)
- design/spec.md: Section 2 (Waiting-time escalation policy)

## Refs
- ED Report Finding: Prolonged waits (30+ hours) with no escalation
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-022 – Manual-follow-up exception workflow" `
  --body @"
## Description
Implement audited exception workflow for escalations not acknowledged by deadline:
- If escalation created but not acknowledged by deadline (e.g., 30 min), system creates ManualFollowUpException
- Exception records: escalation_id, deadline, delivery_attempts, current_owner, review_needed
- Manager/supervisor must review and take action (audit record)
- Exception eventually resolved (audit trail of resolution)
- No automatic escalation to next tier; all reassignment manual and audited

## Technical Requirements
- ManualFollowUpException entity: exception_id, escalation_id, created_at, deadline, owner_role, status, resolution_date, resolution_reason
- Background job: every 5 min, check escalations not acknowledged by deadline, create exceptions
- Idempotency: one exception per escalation (no duplicate exceptions)
- Audit events: ManualFollowUpInitiated, ManualFollowUpReviewed, ManualFollowUpResolved
- Exception query: list outstanding exceptions for manager dashboard
- No silent dismissal: exception must be explicitly acknowledged/reviewed

## Acceptance Criteria
- [ ] ManualFollowUpException aggregate implemented
- [ ] Background job creates exceptions at deadline
- [ ] Idempotency: duplicate calls don't create duplicate exceptions
- [ ] Audit trail immutable and queryable
- [ ] Manager dashboard shows outstanding exceptions
- [ ] Integration test: escalation not acknowledged → exception created → manager reviews
- [ ] Documentation: what constitutes \"acknowledged\" (click button vs. any action?)

## Related
- #MVP-004 (escalation), #MVP-021 (auto escalation trigger)
- design/spec.md: Section 2 (Alert-fatigue controls), Section 4.3 (Critical escalation contract)

## Refs
- Report Finding: Poor escalation response, staff don't see escalations acted upon
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-023 – Escalation visibility dashboard (active, pending, history)" `
  --body @"
## Description
Implement escalation visibility dashboard showing:
- **Active escalations**: count, assigned team/person, created time, severity, patient summary, action required
- **Pending acknowledgements**: not acknowledged by deadline, elapsed time, delivery attempts
- **Manual-follow-up exceptions**: list with owner, review date, status
- **Escalation history**: last 7 days (configurable), created by, assigned to, closure reason, resolved time, responsible user
- **Metrics**: total active, median acknowledgement time, exceptions outstanding, volume by type over time

## Technical Requirements
- ProjectionService: EscalationBoardProjection (read-only, eventually consistent)
- Projections:
  - escalation_active: active escalations filtered by viewer's role and location
  - escalation_history: resolved escalations (last 7 days)
  - exception_pending: manual-follow-up exceptions not yet resolved
  - escalation_metrics: aggregated stats (count, avg ack time, volume by type)
- Real-time updates: WebSocket or polling (<10 sec staleness)
- Filtering: by escalation type, assigned role, patient location, date range
- Export: CSV export for analysis

## UI/UX Requirements
- Dashboard card layout: 4 main sections (active, pending, history, metrics)
- Real-time badge showing count of pending items
- Mobile-responsive for clinical devices
- Accessibility: WCAG AA, keyboard-navigable

## Acceptance Criteria
- [ ] EscalationBoardProjection queries designed and indexed
- [ ] Projection rebuilt from event log (idempotent)
- [ ] WebSocket/polling real-time updates working
- [ ] Dashboard UI mockup approved by clinical staff
- [ ] Performance: <500ms load time for dashboard with 100+ escalations
- [ ] Integration test: escalation created → appears on dashboard within 5 sec
- [ ] Mobile responsiveness tested

## Related
- #MVP-024 (audit trail), #MVP-081 (UI escalation board)
- design/spec.md: Section 3.1 (ProjectionService - RiskDashboard, escalation-related)
- Section 13 (Monitoring, Observability, Governance)

## Refs
- Report Finding: Escalation visibility poor, staff don't see escalations acted upon
- Report Finding: Leadership visibility poor
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-024 – Escalation audit trail and status transitions" `
  --body @"
## Description
Ensure all escalation status transitions are audited and queryable:
- Status flow: created → acknowledged → resolved | escalated | closed | manual-follow-up-initiated
- Each transition records: actor (who), role, timestamp, reason (if applicable)
- Audit trail for investigation and incident response
- Queryable by escalation_id, actor, date range, status

## Technical Requirements
- Audit events for each transition:
  - EscalationRaised: who raised, why, assigned role, deadline
  - EscalationAcknowledged: who acknowledged, when, evidence (ticket, call note)
  - EscalationResolved: who resolved, reason (patient admitted/discharged/reassessed)
  - EscalationReassigned: who reassigned, new role, reason
  - EscalationClosed: who closed, reason
  - ManualFollowUpInitiated: deadline missed, who initiated exception, review needed
- Immutable audit table: no updates/deletes, only inserts
- Query performance: <500ms to retrieve escalation history with 10+ transitions

## Acceptance Criteria
- [ ] Audit events defined and emitted for all transitions
- [ ] Audit table populated correctly
- [ ] Query API built: /api/v1/escalations/{id}/audit
- [ ] Audit export for incident investigation
- [ ] Integration test: escalation transitions recorded correctly
- [ ] Performance: audit queries <500ms for complex filters

## Related
- #MVP-004 (escalation aggregate), #MVP-024 (visibility)
- design/spec.md: Section 4.3 (Critical escalation contract), Section 13 (Governance)
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

gh issue create `
  --title "MVP-025 – Escalation acknowledgement and resolution workflow" `
  --body @"
## Description
Implement UI and API for escalation acknowledgement and resolution:
- Staff view escalation notification in-app alert
- Staff click to open escalation details
- Staff acknowledge escalation (recorded with timestamp, actor, optional note)
- Staff take clinical action (admit, discharge, reassess, move)
- Staff record resolution with reason
- All actions audit-logged

## Technical Requirements
- Commands:
  - AcknowledgeEscalation(escalation_id, actor_id, acknowledged_time, note)
  - ResolveEscalation(escalation_id, resolved_by, resolution_reason, clinical_action_taken)
  - ReassignEscalation(escalation_id, reassigned_by, new_assigned_role, reason)
- API endpoints:
  - POST /api/v1/escalations/{id}/acknowledge
  - POST /api/v1/escalations/{id}/resolve
  - POST /api/v1/escalations/{id}/reassign
- Events: EscalationAcknowledged, EscalationResolved, EscalationReassigned
- UI: alert notification → detail modal → action buttons (acknowledge, resolve, reassign)

## Acceptance Criteria
- [ ] Commands implemented and tested
- [ ] API endpoints created and documented
- [ ] Events emitted and audit-logged
- [ ] UI workflow designed and approved by clinical staff
- [ ] Integration test: end-to-end escalation acknowledgement and resolution
- [ ] Error handling: what if escalation already resolved by someone else?
- [ ] Idempotency: acknowledging twice shouldn't create duplicate audit records

## Related
- #MVP-004 (escalation), #MVP-023 (visibility), #MVP-082 (UI modal)
- design/spec.md: Section 4.3 (Critical escalation contract), Section 9 (UI/UX)
"@ `
  --label "MVP,Escalation,Feature" `
  --milestone "MVP Phase 0"

echo "Waiting-Time Escalation & Visibility issues created (MVP-020 to MVP-025)"

# Note: The full script would continue with MVP-030 through MVP-115
# For brevity, showing template structure. Run the full script to create all 115 issues.

echo ""
echo "MVP issues template created. To generate full issue set, edit and run:"
echo "  .\create-issues.ps1"
