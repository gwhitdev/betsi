### Executive Summary  
This document is a single, consolidated **Specification and Implementation Plan** for the Patient Flow Platform and addon ecosystem you described. It is engineered to be **robust, auditable, secure, low‑resource, locally deployable or cloud‑hosted, test‑driven, domain‑driven, event‑driven, and addon‑friendly**. The plan covers architecture, bounded contexts, APIs, event model, licensing, security, deployment, resilience, AI addon integration, testing, rollout, and acceptance criteria so the system works reliably every time.

---

### 1 System Goals and Success Criteria

**Initial deployment scope**
- The MVP is a multi-tenant SaaS platform. Each NHS trust or organization receives a dedicated database instance and encryption scope.
- The initial release targets display-only workflows with manual entry. No automatic escalation triggers, no AI, no algorithmic risk scoring.
- Role-based escalation authority follows the default matrix (waiting-room coordinator, triage nurse, senior clinician, resuscitation lead). Site-specific customization is deferred to v1.1.
- MVP compliance scope: DSPT annual submission and audit logging only. MHRA SaMD assessment is not pursued for MVP (the system is classified as a general IT scheduling tool, not a medical device). Post-pilot evaluation will inform whether future phases require MHRA assessment.
- Multi-site configuration and regional variants (England vs. Wales) are supported at the SaaS level. Individual site deployments select their configuration during onboarding; each site must approve their operational setup separately.

**Primary goals**
- **Eliminate blind spots** in waiting and corridor areas.  
- **Provide real‑time, auditable patient visibility** across ED and site.  
- **Enable safe, resilient concurrent read/write** at scale with minimal compute.  
- **Allow modular addons** (patient portal, wellbeing, compliance, AI) that can be enabled by license keys.  
- **Operate offline and online** with secure, auditable licensing and feature gating.  
- **Be maintainable by local power users** for UI configuration and non‑breaking upgrades.

**Success criteria**
- **Safety:** No missed critical escalation events for monitored patients in pilot wards for 30 days.  
- **Reliability:** 99.9% uptime for core flow operations during business hours.  
- **Performance:** Core API median response <150ms and p95/p99 targets are defined per endpoint under expected load.  
- **Security:** All PHI encrypted at rest and in transit; audit trail for every access.  
- **Extensibility:** Addon can be installed and enabled without core downtime.  
- **Compliance:** GDPR SAR workflow functional; license audit trail present.

---

### 2 High Level Architecture

**Architectural principles**
- **Domain Driven Design** for clear bounded contexts.  
- **Event Driven** core with an append‑only domain event and audit stream.  
- **Hexagonal Ports and Adapters** to isolate integrations.  
- **CQRS**: Commands for state changes; projections for read models.  
- **TDD** across domain, integration, and UI layers.  
- **Decoupled Addons** that subscribe to events and call public APIs.  
- **Feature gating and licensing** as a cross‑cutting domain service.

**Architecture decisions**
- **Physical deployment baseline:** the core is a modular .NET 10 monolith with strict bounded-context module boundaries. Separate worker processes handle outbox delivery, projections, critical-alert processing, integrations, and retention/compliance jobs.
- **Independent services:** optional addons and capabilities with materially different scaling, security, resource, availability, or release-lifecycle requirements may be independently deployed. A bounded context is not automatically a network service.
- **Service extraction:** extracting a module requires a recorded decision covering operational benefit, data ownership, API/event contracts, failure isolation, observability, migration, and rollback. Services own their data and must not read another service's tables directly.
- **Persistence baseline:** authoritative aggregate state is stored transactionally in the database. Each state change writes an append-only domain event and an outbox message in the same transaction. This is preferred over full event sourcing for the initial release because it reduces operational complexity while preserving auditability and reliable integration delivery.
- **Replay boundary:** projections are rebuildable from retained domain events. Full event sourcing may be introduced for a specific aggregate only after a documented decision record demonstrates the need for historical reconstruction and accepts the associated privacy and retention implications.
- **Delivery semantics:** outbox consumers and projections use at-least-once delivery with idempotent handlers, stable event IDs, and deduplication. No external side effect is performed directly inside the aggregate transaction.
- **Consistency:** aggregate commands are strongly consistent within a tenant; projections and addon integrations are eventually consistent. Every command carries an expected version and idempotency key.
- **Implementation baseline:** services target .NET 10 and must follow the supported .NET 10 servicing policy.

**Core components**
- **Core Flow Engine** (.NET 10 modular monolith) — authoritative aggregates and command handlers.  
- **Event Store / Audit Log** — append‑only domain event and audit records persisted in the database; the outbox guarantees delivery to projections and integrations.  
- **Worker processes** — outbox delivery, projections, critical alerts, integrations, and retention/compliance jobs; each is restartable and idempotent.  
- **Integration Gateway** — adapters for HL7/FHIR, REST, webhooks, file drops.  
- **API Gateway** — versioned REST API and webhook endpoints.  
- **Internal Message Bus** — lightweight in‑process bus for domain events; optional external broker for scale.  
- **Licensing Service** — validates keys, exposes feature gates, emits license events.  
- **Addons** — independent services (Patient Portal, Wellbeing Portal, Compliance Portal, AI services).  
- **UI Layer** — modern SPA with configurable boards and power‑user workspace editor.  
- **Ops Layer** — health checks, metrics, logging, backup, key management.

**Source-of-truth policy**
- The source of truth is defined per field, not per system. The EPR or identity system owns official demographics, NHS number, identity attributes, and the wider clinical record.
- Patient Flow owns operational location, queue position, waiting status, flow assignments, triage workflow status, and escalation workflow state.
- Observations, risk flags, movement events, and discharge status use an explicit tenant/site policy recorded in the source-of-truth matrix. No integration may silently overwrite a locally authoritative value.
- Conflicts preserve both source records, display a discrepancy to authorized users, and require an auditable reconciliation decision. Synchronization adapters must identify the source, version, timestamp, and correlation ID for every update.

**Governance and escalation authority (MVP role-based; site customization in v1.1)**
- Escalation decisions follow a role-based authority matrix. For MVP, default owner roles are:
  - **Waiting-room coordinator**: owns waiting-time escalations (e.g., patient waiting >4 hours).
  - **Registered nurse (triage)**: owns triage-related escalations and reassessments.
  - **Senior clinician (doctor or senior nurse)**: owns clinical escalations (deterioration, observation concerns).
  - **Resuscitation team lead**: owns resuscitation-status escalations.
- Every escalation decision and override records the decision-maker, assigned role, authority level, timestamp, reason, and outcome. Escalation audit records are immutable and include justification for policy deviation.
- Site-specific escalation customization (different role mappings, additional roles, workflow variants) is deferred to v1.1. MVP enforces the default matrix; during deployment, sites may disable escalation types that do not apply locally.

**Waiting-time escalation policy (MVP)**
- For patients in the waiting room or awaiting admission to a bed, the system automatically creates escalations based on elapsed time since arrival, using configurable thresholds per site:
  - **Coordinator threshold** (e.g., 4 hours): escalation assigned to waiting-room coordinator; action is to review clinical status, consider bed allocation, or request reassessment.
  - **Senior escalation threshold** (e.g., 6 hours): escalation assigned to senior clinician or charge nurse; action is to make clinical decision on admission, escalate to bed management, or discharge planning.
  - **Management/capacity escalation** (e.g., 8 hours): escalation assigned to bed manager or operations manager; action is to escalate to site leadership or activate capacity protocols.
- Thresholds are configurable per site during deployment and change-controlled per the Configuration and change control (MVP) section.
- Each escalation carries the elapsed-wait time, current patient state (triage acuity, arrival method, observations), and recommended action based on waiting-time policy.
- If an escalation is created but not acknowledged by a configured deadline (e.g., 30 minutes), the system creates an audited manual-follow-up exception logged to operational staff for supervisory review.

**Paediatric-specific workflows and safeguards (MVP)**
- For patients age <18 years (or site-configurable age threshold):
  - Triage assessment supports paediatric-specific vital sign reference ranges and assessment tools (e.g., PECARN triage criteria).
  - Escalation workflows include paediatric-specific concerns (e.g., non-accidental injury flags, safeguarding concerns, age-inappropriate waiting areas).
  - The system tracks whether a paediatric-trained nurse is assigned to the patient and escalates if an observation or care task requires paediatric competence but no trained staff are available.
  - Paediatric patients must not be held in non-clinical or unsupervised areas; the system alerts if a paediatric patient is in a waiting area for \>30 minutes without documented clinical action.
  - Safeguarding concerns can be flagged by any staff member and create an audited escalation to the designated safeguarding lead.
  - Carers/parents can be named and their presence/absence tracked; escalation if a young child is unaccompanied.

**Escalation visibility dashboard (MVP)**
- The system provides a configurable escalation board/dashboard visible to authorized users (based on location, role, and department):
  - **Active escalations** list: count, assigned team/person, created time, severity, patient summary, action required.
  - **Pending acknowledgements** highlights: not yet acknowledged by deadline, shows elapsed time since creation and delivery attempts.
  - **Manual-follow-up exceptions** list: escalation that was not acknowledged by deadline, responsible manager, review date, status.
  - **Escalation history**: resolved/closed escalations from the last 7 days (configurable), showing created by, assigned to, closure reason, resolved time, and responsible user.
  - **Metrics/summary** cards: total active escalations, median acknowledgement time, manual-follow-up exceptions outstanding, escalation volume by type and time.
- All view data is real-time or no >10-second staleness and includes the last-updated timestamp and data source.
- Escalation status transitions (created, acknowledged, resolved, reassigned, escalated, closed, manual-follow-up initiated) are audited and queryable.

**Deterioration signal definitions and manual observation entry (MVP)**
- The system provides structured observation templates for common clinical assessments (SBAR, NEWS scores, pain assessment, consciousness level, breathing, circulation, mobility, etc.).
- Staff manually enter observations, and the system displays them on the episode timeline in chronological order with data entry user, timestamp, and source (nursing assessment, physician review, etc.).
- Deterioration flags are not automatically generated by algorithms in MVP; instead:
  - Staff can manually flag a patient as \u0022concerning\u0022 or \u0022deteriorating\u0022 with a reason (e.g., \u0022declining consciousness\u0022, \u0022increased pain\u0022, \u0022new respiratory distress\u0022).
  - Deterioration flags trigger an escalation to the senior clinician assigned to the patient.
  - Staff may also manually request reassessment by the triage nurse or physician at any time.
- Pain assessment is captured using site-configured scale (0-10 numeric or descriptor-based) and includes location, character, and onset. Pain severity \u22655 (or site-configurable threshold) creates an escalation to assign analgesic review.
- For paediatric patients, age-appropriate pain assessment tools are used (e.g., FACES scale for 3-7 year olds, FLACC for <3 years).
- All observations are audit-logged with the entering staff member, timestamp, and modification history if amended.

**Conflict resolution and source-of-truth precedence**
- When Patient Flow data and EPR/external system data disagree, the system records both versions with source, timestamp, and correlation ID.
- A structured discrepancy alert is generated and sent to authorized staff (e.g., "Location mismatch: system shows Waiting Room, EPR shows Discharged").
- Staff reconcile by selecting the authoritative source or manually entering a correction. The outcome is recorded as an audit event with the reconciliation decision-maker and justification.
- Patient Flow state is never automatically overwritten by EPR without human confirmation in the reconciliation UI, and vice versa.
- Example: if EPR sends a discharge notification but Patient Flow still shows the patient in the waiting room, both records are preserved; staff review the discrepancy and confirm the correct status.

---

### 3 Detailed Bounded Contexts and Data Model

#### 3.1 Bounded Contexts
- **PatientFlow.Core**
  - Aggregates: **PatientEpisode**, **Location**, **Queue**, **RiskFlag**, **Escalation**.  
  - Commands: `RegisterArrival`, `TriageComplete`, `UpdateObservation`, `MovePatient`, `TriggerEscalation`, `MarkDischarge`.  
  - Domain events: `PatientArrived`, `TriageRecorded`, `ObservationRecorded`, `PatientMoved`, `EscalationRaised`, `PatientDischarged`.

- **IntegrationGateway**
  - Adapters: **FHIR/HL7**, **REST**, **WebhookReceiver**, **FileIngest**, **SQLSync**.  
  - Normaliser: JSON Schema mapping + mapping profiles to domain commands.

- **ProjectionService**
  - Read models: **WaitingBoard**, **AmbulanceQueue**, **RiskDashboard**, **PaediatricWatchlist**.  
  - Updated by event handlers; eventually consistent.

- **Licensing**
  - Entities: `LicenseKey`, `FeatureFlag`, `LicenseAssignment`, `LicenseAudit`.  
  - Services: `LicenseValidator`, `FeatureGateService`.

- **Compliance**
  - Event consumers: `DataExported`, `RecordAnonymised`, `ConsentUpdated`.  
  - Workflows: SAR, retention, DPIA logs.

- **Addons**
  - Each addon is its own bounded context and subscribes to domain events; communicates via public API.

#### 3.2 Data model highlights
- **Authoritative state** stored in aggregates; projections store denormalised read views.  
- **Optimistic concurrency** on aggregates using `RowVersion` or event sequence numbers.  
- **Tenant isolation** is mandatory on every aggregate, event, projection, command, configuration, license, and audit record. Tenant context is authenticated, validated server-side, and included in partition keys and query constraints.
- **Patient identity** is separated from episode and event data. A tenant-scoped identity service stores identifiers and matching decisions, while clinical events reference stable opaque patient and episode IDs. Duplicate, merge, unmerge, and uncertain-match workflows require explicit authorization and audit.
- **Event schema** versioned; events include metadata (tenant id, source, timestamp, correlation id, command id, user id, license id, schema version, and data classification).
- **Terminology and identifiers** are versioned per tenant/site. NHS number handling, patient matching, and external identifiers must support validation, correction, and reconciliation without rewriting clinical history.

#### 3.3 Database-per-tenant operating model
- Each tenant receives a dedicated database and encryption-key scope. Tenant databases are never selected from an untrusted request value; a trusted tenant registry resolves the authenticated tenant to its database connection.
- A control plane, separate from patient data, manages tenant provisioning, database status, schema version, region/residency, license assignment, backup status, and lifecycle state. Control-plane access cannot be used to query clinical records.
- Database provisioning, migrations, seed data, health checks, backup, restore, rotation, suspension, export, and destruction are automated, idempotent, observable, and auditable.
- Schema migrations use expand/contract compatibility. A migration must support the application versions participating in a rolling deployment and must have a tested forward recovery and rollback plan.
- Backups are encrypted, tenant-addressable, access-controlled, retention-managed, and regularly restore-tested. Tenant restore must include domain state, outbox records, audit records, projections, configuration, and addon-owned data where applicable.
- Background jobs, caches, search indexes, metrics, logs, dead-letter queues, and exports carry tenant context. A job without a valid tenant context is rejected rather than treated as global.
- Shared platform services may store only operational metadata needed to route and operate tenants. Services must not access another service's tables directly; cross-module data access uses owned APIs or versioned events.
- Database-per-tenant capacity, cost, connection limits, migration duration, and maximum supported tenant count are measured and documented. Managed database automation is preferred as tenant count grows.

---

### 4 API, Events, and Integration Contracts

#### 4.1 Public REST API (versioned)
- **Authentication**: OAuth2/OpenID Connect; JWT for service tokens.  
- **Command envelope**: commands include tenant context, command ID, idempotency key, expected aggregate version, correlation ID, actor, and submitted timestamp. The server derives tenant context from authenticated claims and rejects mismatches.
- **Errors and reads**: use RFC 9457 problem details, stable error codes, cursor pagination, explicit filtering/sorting limits, and p95/p99 latency budgets in addition to median latency.
- **Key endpoints**
  - `POST /api/v1/commands` — submit domain commands (validated).  
  - `GET /api/v1/episodes/{id}` — canonical episode view.  
  - `GET /api/v1/boards/waiting` — waiting board projection.  
  - `POST /api/v1/webhooks/register` — register webhook endpoints.  
  - `GET /api/v1/license` — license status and enabled features.  
  - `POST /api/v1/compliance/export` — SAR export request.

#### 4.2 Webhook contract
- **Inbound**: Accepts JSON payloads; supports HMAC signature verification, timestamp/replay protection, subscription authorization, and idempotency keys; maps to domain commands.  
- **Outbound**: Signed JSON payloads for events like `EscalationRaised`, `PatientMoved`, `LicenseExpired`, and `EscalationAcknowledged`. Delivery IDs, retries, dead-letter handling, endpoint verification, and secret rotation are required.

#### 4.3 Critical escalation contract
- A critical escalation is a durable domain workflow, not a transient notification. It has a unique escalation ID, severity, reason, patient/episode reference, tenant/site context, created time, current status, assigned role/team, acknowledgement deadline, and resolution history.
- The initial delivery channel is the in-application alert service. Alerts remain visible until acknowledged by an authorized user; acknowledgement records the user, role, timestamp, client, reason where required, and correlation ID.
- Alert delivery uses a durable queue, at-least-once retry with bounded exponential backoff, deduplication by escalation ID and delivery ID, and a dead-letter queue. Failure to deliver or acknowledge is itself observable and auditable.
- If a critical escalation is not acknowledged by its deadline, the system creates an audited manual-follow-up exception for the configured operational process. The exception records the deadline, delivery attempts, current owner, review outcome, and closure authority. Automated reassignment is future scope. The system must not silently close, downgrade, or suppress a critical escalation. SMS/pager delivery is a future addon and must integrate through the same escalation contract rather than bypassing the core workflow.
- Authorized systems may export escalation state through scoped APIs and signed webhooks. Exports are logged, tenant-scoped, replay-protected, and must not be treated as acknowledgement unless the receiving system returns an explicit, authenticated acknowledgement.

#### 4.4 Event model (sample)
- `PatientArrived { episodeId, patientId, arrivalTime, source }`  
- `TriageRecorded { episodeId, triageLevel, triageTime, triageBy }`  
- `ObservationRecorded { episodeId, obsSet, recordedAt, recordedBy }`  
- `EscalationRaised { episodeId, reason, severity, triggeredAt }`  
- `LicenseValidated { licenseId, siteId, features, validatedAt }`

Events include **audit metadata** and are immutable.

---

### 5 Licensing and Feature Gating

**Key properties**
- **Signed license keys** using asymmetric signatures such as RSA-PSS or Ed25519, with embedded tenant/site scope, feature set, issuer, key ID, issued time, expiry, and license version. Validators contain public keys only.
- **Offline validation**: signature verification and local policy checks.  
- **Graceful restricted mode**: on invalid/expired license, core remains read‑only with safety features preserved.  
- **Feature gates**: evaluated at runtime via `FeatureGateService`. Addons query feature state via API or subscribe to license events.  
- **Audit trail**: every license validation, change, and feature toggle emits `LicenseAuditEvent`.
- **Revocation and clock safety:** online revocation is supported when available; offline operation has an explicit grace period, monotonic validation safeguards, key rotation, and an audited emergency safety-mode policy. Licensing must never disable clinically necessary safety functions.

**License lifecycle**
- Install key → validate signature → load feature flags → emit `LicenseValidated`.  
- Renewal/rotation supported via `LicenseRenewalService`.  
- UI for local power users to view license status and audit logs.

---

### 6 Security, Privacy, and Compliance

**Clinical safety and UK governance**
- Each release has a documented clinical safety case, hazard log, safety requirements, risk controls, verification evidence, and approval by the designated Clinical Safety Officer.
- Deployments must support the NHS clinical safety standards DCB0129 and DCB0160 where applicable, including manufacturer and deployment-site responsibilities, local clinical risk assessment, change control, and incident reporting.
- The UK profile must support UK GDPR, the Data Protection Act 2018, Caldicott principles, DPIAs, DSPT evidence, records-management policy, and site-approved retention schedules. NHS DTAC and MHRA/SaMD assessments must be completed where applicable to the service or an addon.
- Clinical thresholds, escalation rules, and safety configuration require clinical ownership, versioning, approval, effective dates, test evidence, and rollback. A software default must never silently replace a local clinical policy.

**Encryption**
- **Transport**: TLS 1.2+ everywhere; mutual TLS for critical integrations.  
- **At rest**: DB TDE plus field‑level encryption for identifiers and free‑text notes.  
- **Key management**: KMS or HSM with rotation policies.

**Access control**
- **RBAC and attribute-based checks** with least privilege; DDD command authorization enforced at the application/domain boundary. Authorization evaluates tenant, organisation, site, ward, patient context, role, purpose, and data classification.
- **Service accounts** for addons with scoped tokens.  
- **Break-glass access** is supported for emergencies, requires a reason, is time-limited, and is prominently audited and reviewed.
- **Audit logging** records every read/write of PHI and every authorization decision without copying PHI into ordinary application logs. Audit records are tamper-evident, tenant-scoped, access-controlled, and retained according to the approved policy.

**GDPR and SAR**
- **Compliance Portal addon** consumes event stream to provide SAR exports, retention enforcement, and anonymisation workflows.  
- **Identity separation:** directly identifying data is stored separately from clinical event data. Events reference opaque identifiers and contain only the minimum necessary data.
- **Erasure/anonymisation:** where lawful retention requires event preservation, identity references are cryptographically erased or replaced with irreversible tenant-scoped pseudonyms. The process is authorized, versioned, audited, and tested; it must not create a way to reconstruct the erased identity from backups or derived projections after the approved purge window.
- **Consent records** stored as first‑class domain objects and enforced by `IntegrationGateway`; lawful basis, purpose, withdrawal, emergency processing, and data-sharing restrictions are recorded.
- **SAR exports** are generated from authorized identity, episode, event, projection, and addon data sources with completeness checks, redaction rules, export audit records, and expiry of generated packages.

**Operational security**
- Rate limiting, signed webhooks, IP allowlists for critical endpoints, WAF for public endpoints.

**Threat model**
- Threat assessments cover cross-tenant access, stolen clinical sessions, compromised addon credentials, webhook replay, insider misuse, bulk export abuse, ransomware, privileged database access, supply-chain compromise, license forgery, and AI data exfiltration or prompt injection.
- Each threat has a documented actor, attack path, impact, preventive control, detection signal, response procedure, and verification evidence. Threat models are reviewed for material architecture, integration, and release changes.
- Security boundaries include tenant routing, service-to-service authentication, key management, export authorization, audit integrity, dependency provenance, secrets handling, and PHI redaction from telemetry.

**Retention and records management**
- Each tenant/site configures approved retention categories and legal holds. Retention jobs are policy-driven, observable, resumable, and independently auditable.
- Backups, caches, search indexes, projections, exports, dead-letter queues, and addon stores are included in retention and erasure design. Purge completion is verified across each data copy after the approved recovery window.
- Audit records are retained for the approved period and are tamper-evident; access to audit data is itself audited.

**Integration contract requirements**
- HL7/FHIR adapters declare supported versions, profiles, terminology systems, identifier rules, and mapping versions. Invalid or ambiguous messages are quarantined for review rather than silently discarded.
- Adapters provide acknowledgements, reconciliation reports, duplicate detection, idempotent replay, correlation IDs, and a manual exception queue. External system downtime must not cause silent loss of a clinical command.

---

### 7 Concurrency, Resilience, and Data Consistency

**Concurrency model**
- **Optimistic concurrency** on aggregates; commands include expected version.  
- **Transactional aggregate state and append-only domain events** provide the authoritative command boundary; projections are rebuilt from retained events when needed.  
- **Conflict resolution**: domain rules reject conflicting commands; UI surfaces conflict resolution flows.

**Resilience**
- **Graceful degradation**: core command processing remains local even if external addons fail.  
- **Retry policies** for external calls with exponential backoff.  
- **Health checks** and circuit breakers for integrations.  
- **Backpressure**: queueing for high‑throughput bursts; bounded worker pools.
- **Critical alert durability**: escalation records and in-application alert deliveries survive process restarts and temporary database, projection, or addon failures. Unacknowledged critical alerts are reconciled by a watchdog and surfaced to operators.
- **Side-effect isolation**: projection rebuilds and event replays must suppress notification and external side effects unless an explicitly authorized replay mode is selected.

**Downtime and degraded operation**
- The application clearly indicates degraded, stale, read-only, and unavailable states; users must never infer that an unavailable feed contains current data.
- The approved initial fallback is a documented manual clinical process. Offline writes are not accepted unless a later release supplies a tested, tenant-scoped offline command queue with conflict resolution and duplicate prevention.
- Recovery reconciles local operational records with EPR and integration messages using identifiers, timestamps, source versions, and correlation IDs. Ambiguities enter a manual exception queue.
- Recovery testing verifies no silent loss of commands, duplicate patient episodes, duplicate alerts, or unauthorized cross-tenant data access.

**Low resource design**
- Lightweight projections, minimal in‑memory caching, optional external broker only for high scale.  
- AI and heavy analytics run in separate services.

**Operational objectives**
- Define and monitor capability-level SLOs, including core command availability, p95/p99 API latency, event delivery latency, projection freshness, critical alert display time, acknowledgement latency, queue depth, and maximum data staleness.
- The pilot must document RPO and RTO for each deployment profile, including tenant database restore, outbox recovery, alert recovery, projection rebuild, and manual clinical fallback.
- Initial target values are proposed as: critical command availability 99.95%, waiting-board freshness at least 99% under 10 seconds, critical alert creation-to-display at least 99.9% under 5 seconds, RPO no greater than 5 minutes, and RTO no greater than 30 minutes. Clinical and site owners must approve final values.

**Supportability and maintenance**
- All support diagnostics use correlation IDs and provide PHI-redacted diagnostic bundles. Production support access is time-limited, authorized, tenant-scoped, and audited.
- Feature flags have an owner, purpose, expiry/review date, safe default, and removal issue. Configuration drift, failed migrations, stale workers, dead-letter growth, and certificate/key expiry are observable.
- The support policy defines .NET and dependency update cadence, vulnerability remediation targets, compatibility windows, runbook ownership, incident response, disaster-recovery rehearsal frequency, and product-exit/data-portability procedures.

**Compatibility policy**
- REST APIs, events, database schemas, addon manifests, projections, configuration schemas, and license formats are versioned independently where necessary.
- Events are backward-compatible for the documented retention period; consumers tolerate unknown fields; breaking changes require a new event or API version and a migration plan.
- Rolling deployments use expand/contract database changes and compatibility tests between the versions being deployed. Projection rebuilds are versioned and side-effect suppressed.

---

### 8 Addon Model and AI Integration

**Addon characteristics**
- Independently deployable services.  
- Subscribe to domain events or call public APIs.  
- Have their own license flags.  
- Can be installed/uninstalled without core downtime.
- Each addon supplies a signed manifest declaring version, compatible API/event schema versions, required permissions, tenant data scope, health checks, migrations, configuration schema, and uninstall/data-retention behavior.
- Addon upgrades use compatibility checks, staged enablement, rollback, and explicit migration ownership. Uninstallation revokes credentials and stops delivery before data is deleted or retained under the approved policy.
- Addon failures cannot block core clinical commands or suppress core escalations. Addon data ownership, support status, and incident responsibility are recorded.

**Example addons**
- **Patient Portal**: read projections, limited write commands, NHS login integration.  
- **Wellbeing Portal**: subscribes to `WaitExceededThreshold` events, manages wellbeing tasks.  
- **Compliance Portal**: consumes audit stream for SAR and DPIA.  
- **AI Risk Scoring**: consumes events, returns `RiskFlagAdded` events.  
- **Flow Prediction**: consumes event history, emits `FlowAlertRaised`.

**AI integration pattern**
- **Sidecar or separate service** that subscribes to event stream.  
- **Model inference** performed off‑platform or in a sandboxed addon.  
- **Results** returned as domain commands (e.g., `AddRiskFlag`) and recorded as events.  
- **Explainability**: AI outputs include model/version, input snapshot reference, confidence, rationale metadata, timestamp, and originating tenant for audit.
- **Clinical governance:** every model has documented intended use, limitations, training-data provenance, validation results, bias and performance monitoring, drift detection, abstention behavior, human review/override, and rollback. AI must not autonomously make or conceal clinical decisions unless separately assessed and approved under the applicable clinical safety and regulatory process.

---

### 9 UI and Power User Configurability

**UI features**
- **Configurable boards**: lanes, filters, columns, thresholds stored as versioned JSON.  
- **Power user editor**: drag‑drop layout, threshold editors, colour rules, saved views.  
- **Accessibility**: WCAG AA compliance, multilingual support, Welsh active offer.  
- **Auditable changes**: every UI config change emits `WorkspaceConfigChanged` event.
- **Configuration safety:** threshold and escalation changes are schema-validated, previewable, versioned, subject to role-based approval and effective dates, tested against representative scenarios, and reversible. Production changes must not be applied without an audit record identifying the requester, approver, reason, and affected tenant/site.

**Configuration and change control (MVP)**
- Site administrators configure operational thresholds (e.g., escalate waiting patients after 4 hours; consider pediatric observation after 3 hours without reassessment).
- Configuration changes follow an approval workflow: admin requests → manager approves → effective date is set → change is applied with audit trail.
- Every configuration version is recorded with a version ID, applied date, applied-by identity, approver, and change reason. Rollback to a previous configuration is supported and audited.
- Configuration schema is defined per tenant and published in the system configuration API. Invalid changes are rejected server-side.
- Default thresholds are provided and require clinical input during deployment; there is no silent fallback to unsafe defaults.
- Site-specific escalation authority and workflow customization (v1.1) will extend on this configuration framework; MVP enforces the default role matrix and allows enabling/disabling escalation types.

**Clinical interaction requirements (MVP: display-only, manual entry)**
- The MVP system does not generate automatic escalations, algorithmic recommendations, or AI-triggered alerts.
- All escalations are manually created by authorized staff based on patient observations or thresholds visible on the board.
- Staff can manually enter observations, triage assessments, risk flags, and other clinical data. There is no validation that rejects data entry based on ML models or external rules; validation is structural (required fields, data type, range) only.
- The UI is task-oriented for defined personas including waiting-room staff, triage clinicians, flow coordinators, managers, and system administrators.
- Critical tasks must support keyboard-first operation, touch-screen operation on approved clinical devices, clear focus order, accessible names, screen-reader announcements, and WCAG 2.2 AA behavior.
- Every board and episode view distinguishes loading, empty, unavailable, stale, and current data. Staleness displays the last successful update time and affected source.
- Concurrent changes show a clear conflict with the changed fields, source, timestamp, and available merge or refresh action. No user change is silently discarded.
- Destructive or safety-significant actions require appropriate confirmation; reversible changes offer undo or a documented amendment workflow rather than relying on browser behavior.
- Search, filters, sorting, pagination, saved views, print, export, session timeout, workstation handoff, and Welsh-language content are specified per persona and tested on supported devices.

**Alert-fatigue controls (MVP)**
- When a patient hits a configured threshold (e.g., waiting >4 hours), the system creates an in-app alert and assigns it to the configured role/team.
- If the assigned team does not acknowledge the alert by the deadline (e.g., 30 minutes), the system creates an audited manual-follow-up exception. The exception records the escalation, deadline, delivery attempts, and requires manual review and action by a manager or supervisor.
- Automatic escalation to the next tier (e.g., from coordinator to senior nurse) is not implemented in MVP; all reassignment is manual and audited.
- Alert severity, repeat interval, acknowledgement deadline, grouping, and escalation policy are configured per site with clinical approval and effective dates.
- Critical alerts cannot be silently dismissed or suppressed without audit. Acknowledgement is distinct from clinical resolution and does not remove the resolution obligation.
- The system measures alert volume, acknowledgement latency, manual-follow-up exceptions, and suppression use. Suppression always requires an authorized reason and audit record.

**UX principles**
- Minimal cognitive load, clear escalation indicators, audible/visual alerts for critical events, mobile‑friendly for on‑floor devices.

---

### 10 Implementation Plan and Timeline (MVP focused)

**Principles**
- **TDD** for domain logic, integration tests for adapters, contract tests for APIs.  
- **Incremental delivery**: core flow → integrations → conflict resolution → incident handling.
- **Display-only MVP**: no automatic triggers, no AI, no algorithmic recommendations. All clinical actions are manual.
- Every workflow transition and error case has a traceable test result and documented acceptance.

**MVP development (0–30 days)**
- Provision dev infra, CI/CD, and database-per-tenant setup.
- Implement PatientEpisode, Location, Queue, Escalation aggregates and core commands.
- Implement transactional event/audit records and the outbox.
- Implement role-based escalation authority and authorization.
- Implement licensing module and offline key validator.
- Implement waiting board and episode detail projections; manual entry UI.
- Implement EPR/ambulance integration mapping and adapter.
- Implement identity matching and conflict-resolution UI.
- Implement alert delivery and manual acknowledgement workflow.
- Comprehensive unit, integration, and end-to-end tests for pilot workflows.
- Role-based and tenant-isolation tests.
- Documentation and runbooks for MVP workflows.

**Post-MVP (after first site deployment)**
- Customer feedback and operational monitoring inform Phase 1+ priorities.
- Phase 1 (v1.1): site-specific escalation authority customization, configuration UI enhancements, additional integrations.
- Phase 2 (v2.0): AI advisory addons, Patient Portal, Wellbeing Portal (future scope, not MVP).
- Phase 3: advanced features based on operational evidence and regulatory assessment.

**Milestones and owners**
- **Product Owner**: clinical lead.  
- **Technical Lead**: platform architect.  
- **Security Lead**: InfoSec.  
- **Delivery Manager**: deployment and rollout.  
- **Clinical Safety Officer**: clinical acceptance and safety sign‑off.

---

### 11 Testing, Validation, and Acceptance

**Testing strategy**
- **Unit tests** for domain invariants (TDD).  
- **State-machine tests** for every permitted and rejected pilot workflow transition.  
- **Integration tests** for adapters using contract stubs.  
- **Contract tests** for API, event, addon, webhook, and source-of-truth compatibility.  
- **End‑to‑end tests** for critical flows (arrival → triage → escalation).  
- **Chaos tests** for resilience (simulate addon failure).  
- **Performance tests** for expected concurrency.  
- **Security tests**: static analysis, dependency scanning, pen test.
- **Tenant-isolation tests** for APIs, jobs, events, caches, exports, backups, and database routing.  
- **Accessibility and usability tests** with supported devices, keyboard navigation, assistive technology, and representative clinical users.

**Clinical validation**
- **Scenario tests** with clinicians for corridor care, paediatric watch, ambulance handover.  
- **Acceptance criteria** defined per user story and safety checklist.

**Operational validation**
- **SLOs** monitored for latency, error rate, event lag.  
- **Audit verification**: random audit of event stream vs. clinical notes.

---

### 12 Deployment, Upgrades, and Rollback

**Deployment model**
- **Containers** for the modular core, workers, and independently deployed addons; compose supports local development and an orchestrator supports cloud deployment.  
- **Immutable releases** with versioned images.  
- **Database migrations** via versioned expand/contract migration scripts with rollback and recovery plans.

**Upgrade strategy**
- **Backward compatible event schemas**; versioned event handlers.  
- **Blue/green or canary** deployments for addons.  
- **Feature flags** to enable new features gradually.

**Rollback**
- Revert to the previous image and rebuild projections in side-effect-suppressed mode if needed. Do not replay events directly into external notification or integration side effects.
- Define and test service-level RPO/RTO, backup encryption, point-in-time restore, tenant recovery, and disaster-recovery procedures.
- Maintain backups and point-in-time restore for the database; test restoration at a documented cadence.

---

### 13 Monitoring, Observability, and Governance

**Observability**
- Metrics: command throughput, event lag, projection lag, API latency, license validation errors.  
- Tracing: correlation ids across commands and events.  
- Logs: structured, indexed, with redaction for PHI.
- Alert metrics: undelivered and unacknowledged critical escalations, retry counts, dead-letter depth, acknowledgement latency, and watchdog reconciliation failures.

**Governance**
- **Daily flow safety huddle** fed by dashboards.  
- **Weekly risk reconciliation** using compliance portal outputs.  
- **License audits** monthly.  
- **Change control** for UI workspace changes and feature toggles.

---

### 14 Acceptance Checklist for Go Live

**MVP release criteria (v1.0)**
- Technical code review approved and all defects resolved.
- Build succeeds and all automated tests pass.
- Display-only workflows (arrival, triage, waiting, movement, discharge) functional with manual entry.
- Identity matching and conflict resolution UI tested.
- EPR integration adapter tested with sample data; EPR unavailability does not block local registration.
- Escalation alert delivery and manual acknowledgement workflow tested end-to-end.
- **Waiting-time escalation policy tested**: automatic escalation triggers at configured thresholds (4h, 6h, 8h); manual-follow-up exceptions created if not acknowledged by deadline.
- **Escalation visibility dashboard functional**: active escalations, pending acknowledgements, manual-follow-up exceptions, and escalation history viewable and real-time.
- **Paediatric-specific workflows tested**: paediatric vital-sign ranges enforced, safeguarding flags and escalations working, unaccompanied-child alerts triggering, paediatric-trained staff assignment tracked.
- **Deterioration signal and pain assessment workflows tested**: structured observation entry, manual deterioration flagging, pain assessment captures (scale/location/onset), pain-based escalations to analgesic review.
- Database-per-tenant provisioning and data isolation tested.
- Audit logging of all commands, authorization decisions, and escalation transitions verified.
- DSPT checklist compliance items completed.
- Runbook documentation and role templates provided to site teams; training materials include waiting-time escalation, deterioration signal recognition, and paediatric safeguarding procedures.

**Phase 0→1 gate criteria (minimal)**
- All MVP release criteria met.
- Clinical Safety Officer reviews the safety case (hazard log, controls, residual-risk decisions) and approves for limited use.
- Site champion and project lead confirm readiness for internal testing.

**Phase 1→2 gate criteria (after one week of pilot use)**
- System remains available and operational.
- No safety incidents or unrecoverable data loss.
- Critical workflows (arrival through escalation) function as designed.
- Staff can complete key tasks without excessive help requests.
- Escalation procedures and role-based acknowledgement work as intended.

**Phase 2→3 gate criteria (full ED adoption)**
- All workflows validated with representative patient volumes.
- Training and role-specific procedures rolled out.
- Support model and incident escalation procedures documented and tested.
- Audit evidence and SLO performance acceptable to site operations team.
- Security review and DSPT submission completed.

**Traceability evidence required (MVP)**
- Pilot workflows (arrival, identity matching, triage, waiting, movement, escalation, discharge) tested end-to-end.
- **Waiting-time escalation tests**: confirm automatic escalation created at each threshold (4h, 6h, 8h); manual-follow-up exception created if not acknowledged by deadline; acknowledge action clears pending status.
- **Paediatric safeguarding tests**: confirm age-based routing, paediatric vital-sign ranges applied, safeguarding flags escalate correctly, unaccompanied-child alerts trigger for children without carer.
- **Deterioration and pain escalation tests**: manual observation entry creates audit trail; deterioration flag escalates to senior clinician; pain score \u22655 escalates to analgesic review; pain assessment captures scale, location, onset.
- **Escalation visibility dashboard tests**: verify active escalations display, pending acknowledgements highlight, manual-follow-up exceptions list, and escalation history queryable.
- Escalation tests demonstrate manual acknowledgement, audit trail, and manual-follow-up exception creation.
- Tenant-isolation tests confirm cross-tenant rejection at API and database layers.
- Privacy tests verify audit logging and absence of PHI in application logs.
- Conflict-resolution tests verify discrepancy detection and reconciliation workflow.

---

### 15 Future Roadmap and Evolution (Beyond MVP)

**Continuous deployment model**
- After MVP release, the platform transitions to continuous deployment with rolling features. There are no formal v1.1, v2.0, or v3.0 gates; instead, capabilities are merged and deployed incrementally as they are ready.
- Each feature is controlled by a feature flag. When a feature is ready for pilot sites, it is enabled for those sites via configuration; other sites remain on the stable baseline.
- Breaking changes to APIs, events, or databases are backward-compatible or clearly versioned. Database migrations use expand/contract to support multiple application versions.
- Hotfixes for safety or critical incidents bypass the normal prioritization and are deployed immediately with emergency review.

**Evidence-gathering and feedback framework**
- Operational metrics are collected continuously: safety incidents, escalation frequency, alert-fatigue metrics, workflow completion time, user adoption rate, data quality, integration failures.
- Feedback loops: weekly operational huddles with site champions, monthly review of metrics and incident patterns, quarterly strategic review.
- Feature feedback is captured through in-app feedback forms, support tickets, and ad-hoc clinical input.
- All roadmap decisions are explicitly tied to evidence: "we will prioritize X because metric Y shows a problem" or "clinical feedback consistently requests Z".
- Decision ownership is clear: the Product Owner makes prioritization decisions with input from site champions, clinical leads, and ops teams.

**v1.1 outline (future; dependent on MVP feedback)**
- **Timeline**: 4–6 weeks after MVP reaches operational stability.
- **Site-specific escalation authority and workflow customization**: allow sites to define their own role mappings, escalation tiers, and department-specific workflows (previously hardcoded to default roles).
- **Configuration UI enhancements**: self-service configuration for thresholds, escalation rules, and integration mappings; approval workflow embedded in the UI.
- **Mandatory structured risk assessments (post-MVP response to ED report findings)**: if pilot sites confirm the need, introduce time-gated risk assessment workflows for pressure-damage, falls, and safeguarding concerns. Assessments become mandatory once per shift or upon arrival, with audit enforcement.
- **Escalation resolution tracking and feedback**: extend escalation dashboard to capture what action was taken in response to each escalation (admitted to ward, discharged, reassessed, moved to higher acuity area), linking clinical decisions to escalations for outcome measurement and staff feedback.
- **Capacity and overflow management**: track bed availability by department, display capacity state on boards, and trigger escalations when overflow protocols activate (e.g., corridor care, non-clinical areas). Alert staff to dignity and safety implications of overflow situations.
- **Communication templates and patient updates**: provide standardized templates for updating patients on waiting time, progression, and next steps; integrate SMS or app notifications (future). Support Welsh language active offer and accessibility requirements.
- **Additional integrations**: secondary system adapters (e.g., pharmacy, lab) as requested by pilot sites; all follow the same conflict-resolution and source-of-truth pattern.
- **Production fixes and QoL improvements**: bug fixes, performance tuning, accessibility improvements based on pilot feedback.
- **Extended audit and reporting**: custom audit queries, export of audit logs for compliance, audit-based incident investigation tools; SOP version tracking and compliance dashboards.
- **Staffing and skill-mix integration (pending external roster system availability)**: if a roster or scheduling system is available, integrate to alert when paediatric-trained staff are unavailable or skill gaps exist; recommend workflow adjustments to address gaps.
- **Regulatory**: no MHRA assessment needed; continue DSPT compliance; consider DTAC if multi-organization data flows are needed.

**v2.0 outline (future; depends on v1.1 operational evidence and decision)**
- **Timeline**: 3–4 months after v1.1 is stable.
- **AI advisory layer (optional, opt-in)**: if pilot sites request it and regulatory assessment supports it.
  - Risk-scoring advisories (not automatic escalations) based on observation patterns.
  - Flow-prediction recommendations (e.g., "expected wait extends beyond threshold; consider early discharge").
  - All AI outputs are advisory only; clinicians must explicitly acknowledge or act; AI does not suppress or override escalations.
  - MHRA assessment required if AI is promoted from advisory to autonomous decision-making.
- **Patient Portal addon**: read-only access for patients to their waiting status, estimated wait time, and educational resources.
  - NHS login integration or alternative authentication.
  - Separate data scope; no PHI outside the portal scope.
  - Compliant with WCAG AA and multilingual support from MVP.
- **Compliance Portal addon**: unified interface for SAR exports, retention enforcement, and DPIA tracking.
  - Builds on the audit stream and event architecture.
  - Reduces operational overhead for GDPR and regulatory compliance.
- **Wellbeing Portal addon**: wellbeing tasks and interventions for patients exceeding wait thresholds.
  - Optional per site; enables patient engagement during waits.
  - Extends the escalation workflow to include wellbeing actions alongside clinical escalations.
- **Regulatory**: if AI is introduced, MHRA assessment for the applicable AI features; update DSPT; potential DTAC involvement if cross-organizational data flows are required.

**v3.0+ outline (future; speculative; depends on v2.0 learning)**
- **Autonomous clinical workflows**: if operational evidence supports it, introduce limited autonomous escalation or routing based on validated rules or AI models. Requires full MHRA assessment and clinical safety approval.
- **Cross-organizational data sharing and federated views**: if regulatory and operational barriers are resolved, enable patient tracking across multiple trusts or regions.
- **Predictive analytics and capacity planning**: aggregate anonymized operational data to provide site-level capacity forecasting and resource planning.
- **Mobile-native apps**: dedicated clinician and patient apps beyond web-based access.
- **Integration marketplace**: third-party addons published and authorized by the platform; standardized addon contracts and compatibility.

**Organizational and operational capabilities required for post-MVP growth**
- **Dedicated product management**: full-time product owner managing backlog, prioritization, and feedback loops.
- **Site support infrastructure**: dedicated support engineer(s) for pilot sites, escalation channel to development team, incident response procedures.
- **Compliance and regulatory team**: regulatory monitoring, MHRA liaison, DPIA updates, annual DSPT maintenance.
- **Customer success**: quarterly business reviews with each site, measurement of KPIs, executive reporting.
- **Community and governance**: user forums, clinical advisory board, steering committee for strategic decisions.

---

### 16 Deliverables and Next Steps

**Immediate deliverables (MVP)**
- Detailed C# project skeleton with bounded contexts and sample command handlers.  
- Event schema document and sample events.  
- License key validator reference implementation in C#.  
- API contract OpenAPI spec for core endpoints and webhooks.  
- UI wireframes for waiting board and power user editor.

**Recommended next steps**
- Approve Phase 0 budget and staffing.  
- Kick off a 4‑week sprint to deliver the licensing module, transactional event/audit records, outbox, and a minimal waiting board projection.  
- Run clinical tabletop exercises to validate escalation triggers.  
- Schedule security review and compliance DPIA.

---

### Appendix A Sample Domain Command Flow

**Sequence for patient arrival**
1. External system posts arrival webhook → `IntegrationGateway` normalises payload.  
2. Gateway issues `RegisterArrival` command to Core.  
3. Core validates and appends `PatientArrived` event to event store.  
4. ProjectionService consumes event and updates WaitingBoard.  
5. A threshold monitor evaluates the projected waiting state and submits an idempotent `RaiseWaitThresholdAlert` command; the projection does not create authoritative domain events or external side effects.  
6. Core validates the command and records `WaitExceededThreshold` as a domain event when the threshold policy is met.  
7. Wellbeing addon subscribes to the event and creates a wellbeing task; replay and duplicate delivery do not create duplicate tasks.

---

### Appendix B Minimal C# Project Layout (high level)
- `src/PatientFlow.App` — modular .NET 10 host and composition root.  
- `src/PatientFlow.Core` — domain, aggregates, commands, events, domain services.  
- `src/PatientFlow.Api` — REST endpoints, authentication, API contracts.  
- `src/PatientFlow.Integration` — adapters and mapping profiles.  
- `src/PatientFlow.Workers` — outbox, projections, alerts, integrations, retention, and compliance workers.  
- `src/PatientFlow.Licensing` — license validator, feature gate.  
- `src/PatientFlow.Persistence` — tenant database routing, migrations, repositories, outbox, and audit storage.  
- `src/Addons/PatientPortal` — example addon.  
- `tests/Unit` — domain unit tests.  
- `tests/Integration` — adapter contract tests.  
- `deploy` — docker compose and k8s manifests.

---

This specification is intentionally prescriptive so the system behaves predictably and safely every time. If you want, I will now produce one of the following as the next concrete artefact:

- **C# reference implementation** for the Licensing bounded context and sample license validator.  
- **OpenAPI spec** for the core REST API and webhook contract.  
- **Sequence diagrams** for the critical flows (arrival → triage → escalation).  
- **Sprint backlog** for Phase 0 with user stories, acceptance criteria, and test cases.

Tell me which artefact you want first and I will generate it.
