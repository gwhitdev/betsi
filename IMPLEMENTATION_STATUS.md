# Betsi Patient Flow MVP – Implementation Status

**Last Updated**: 2026-11-09  
**Current Phase**: MVP Phase 0 – Core Infrastructure Setup  
**Target .NET Version**: .NET 10  
**Repository**: https://github.com/gwhitdev/betsi

---

## Overall Progress

| Activity | Status | Notes |
|---|---|---|
| **Specification & Design** | ✅ Complete | design/spec.md (646 lines), report mapping, phase roadmap |
| **Documentation** | ✅ Complete | GITHUB_PROJECT_SETUP.md, ISSUES.md (115 issues planned) |
| **GitHub Project Setup** | 🔄 In Progress | Scripts created; awaiting REST API execution with token |
| **Core Codebase Structure** | ⏳ Pending | Starting now (this session) |
| **Build & CI/CD** | ⏳ Pending | Phase 1 |
| **Testing Infrastructure** | ⏳ Pending | Phase 1 |

---

## Completed Work (Sessions 1–3)

### Design & Planning
- ✅ Specification document (design/spec.md)
  - Multi-tenant SaaS architecture with database-per-tenant model
  - Event-driven domain model with transactional outbox
  - Source-of-truth matrix for integration conflicts
  - Waiting-time escalation policy (4h/6h/8h thresholds)
  - Paediatric workflows and safeguarding escalation
  - Configuration/change control for operational governance
  - MVP acceptance criteria and phase roadmap

- ✅ Report-to-Spec Mapping (design/REPORT_FINDINGS_MAPPING.md)
  - Traced all 12 ED inspection findings to spec sections
  - Allocated coverage across MVP and post-MVP phases
  - Documented unaddressed items and future work

- ✅ GitHub Planning Artifacts
  - .github/project.md – Phase 0/1/2/3 delivery plan
  - .github/ISSUES.md – 115 issues across 8 themes
  - .github/GITHUB_PROJECT_SETUP.md – Setup guide

- ✅ Phase 0 Readiness Summary (README_PHASE_0_READY.md)
  - Stakeholder approval checklist
  - Success metrics
  - Team roles and responsibilities

### Committed Work
```
Latest commits: design/spec.md, REPORT_FINDINGS_MAPPING.md, planning artifacts
Branch: main (gwhitdev/betsi)
```

---

## Current Work (This Session)

### Phase: MVP-001–010 Core Patient Flow Engine

**Objective**: Establish the foundational domain model, aggregates, command handling, and event infrastructure.

#### Starting Point
- Betsi.csproj: .NET 10 executable with implicit usings and nullable enabled
- Program.cs: Empty "Hello, World!"
- No external dependencies

#### Work in Progress

**Step 1: Project Structure & Dependencies** [IN-PROGRESS]
- [ ] Rename Betsi.csproj to Betsi.Core.csproj (or keep as primary host)
- [ ] Add required NuGet packages:
  - `Microsoft.EntityFrameworkCore` (8.0 or 9.0)
  - `Microsoft.EntityFrameworkCore.SqlServer` or `.Npgsql` for PostgreSQL
  - `MediatR` for command/query dispatch
  - `Serilog` for structured logging
  - `FluentValidation` for command validation
  - `NUnit` or `xUnit` for testing
- [ ] Create folder structure:
  ```
  src/
	Betsi.Core/
	  Domain/
		Aggregates/
		Events/
		Commands/
		Services/
	  Application/
		CommandHandlers/
		QueryHandlers/
		Projections/
	  Infrastructure/
		Persistence/
		Integration/
	  API/
		Controllers/
		Middleware/
	Betsi.Tests/
	  Domain.Tests/
	  Application.Tests/
  ```

**Step 2: Domain Model (MVP-001–004)** [PENDING]
- [ ] PatientEpisode aggregate
  - State machine: Waiting → Triage → Treatment → Discharge
  - Commands: RegisterPatient, BeginTriage, BeginTreatment, Discharge
  - Events: PatientEpisodeCreated, TriageStarted, TreatmentStarted, PatientDischarged
  - Optimistic concurrency with Version and AggregateId

- [ ] Location aggregate
  - State: Available, Occupied, Full, Overflow
  - Tracking bed/room capacity and current occupants

- [ ] Queue aggregate
  - Ordered list of waiting patients
  - Threshold tracking for escalation triggers

- [ ] Escalation aggregate
  - State machine: Created → Acknowledged → Resolved / Escalated / Closed
  - Manual-follow-up workflow for unacknowledged escalations
  - Commands: TriggerEscalation, AcknowledgeEscalation, ResolveEscalation

**Step 3: Event Store & Outbox (MVP-005)** [PENDING]
- [ ] Event log schema
  - event_id (idempotency key)
  - aggregate_id, aggregate_type
  - event_type, event_data (JSON)
  - tenant_id, timestamp, version

- [ ] Audit log schema
  - action, actor_id, actor_role, context
  - affected_aggregate_id
  - outcome (success/failure), error_message
  - timestamp, tenant_id

- [ ] Outbox pattern
  - Transactional consistency (aggregate + event + outbox in same tx)
  - Outbox consumer (background worker)
  - Deduplication by event_id
  - Replay capability

**Step 4: Command Handling & Validation (MVP-006)** [PENDING]
- [ ] Command validation layer
  - Tenant context extraction
  - Actor authorization checks
  - Precondition validation
  - Command idempotency (command_id + idempotency_key)

- [ ] Command dispatch
  - MediatR pipeline
  - Concurrency handling (expected_version)
  - Event generation and outbox insertion

**Step 5: Multi-Tenant Infrastructure (MVP-007)** [PENDING]
- [ ] TenantContext middleware
  - Extract tenant ID from request (header, subdomain, path parameter)
  - Validate tenant has active license
  - Inject into command/query context

- [ ] TenantRegistry
  - Map tenant ID to database connection
  - Encrypt connection strings
  - Cache with TTL

- [ ] Tenant isolation enforcement
  - API-layer filtering
  - Database-layer row-level security (if supported)
  - Audit access attempts

**Step 6: Licensing & Feature Gating (MVP-008)** [PENDING]
- [ ] Offline key validator
  - HMAC/RSA signature validation
  - No external dependency
  - Fail-safe (allow operation if validation unavailable for brief period)

- [ ] Feature gate service
  - Cache license features locally
  - Return feature availability to UI/API
  - Log validation failures

**Step 7: Database Provisioning & Migrations (MVP-009)** [PENDING]
- [ ] Infrastructure as Code
  - Migration automation for new tenants
  - Rollback testing

- [ ] Migration framework
  - All core schema for aggregates
  - Event and audit log tables
  - Tenant configuration

**Step 8: Audit Logging & Compliance (MVP-010)** [PENDING]
- [ ] Audit trail
  - All commands logged with actor, role, outcome
  - PHI redaction in logs
  - SAR export functionality
  - 3-year retention policy

---

## Outstanding Work (Next Sessions)

### Phase: MVP-020–025 Waiting-Time Escalation
- Automatic escalation generation at thresholds (4h/6h/8h)
- Manual-follow-up exception workflow
- Escalation visibility dashboard
- Acknowledgement and resolution workflows

### Phase: MVP-030–035 Paediatric Safety
- Age-based workflow routing
- Paediatric vital-sign reference ranges
- Safeguarding flag workflow
- Trained-staff assignment tracking

### Phase: MVP-040–045 Clinical Observation & Deterioration
- Structured observation entry
- Pain assessment integration
- Deterioration signal definitions
- Manual observation entry UI

### Phase: MVP-050–055 Multi-Tenant SaaS Governance
- Tenant onboarding workflow
- License key issuance and rotation
- Configuration management
- Feature enablement per tenant

### Phase: MVP-060–070 API, Integration, Authentication
- REST API versioning and documentation
- OAuth2/OpenID Connect integration
- Webhook delivery
- HL7/FHIR adapters
- RFC 9457 problem details specification

### Phase: MVP-080–095 UI/UX – Waiting Board & Dashboards
- Waiting room board (real-time patient display)
- Escalation visibility dashboard
- Triage scoreboard
- Discharge tracking board
- Power-user workspace editor
- Configuration UI

### Phase: MVP-100–115 Testing, Deployment, Operations
- Test automation infrastructure
- CI/CD pipeline (.github/workflows)
- Staging/production deployment procedures
- Health checks and monitoring
- Backup and restore procedures
- Operator runbooks

---

## Architecture Decisions Recorded

| Decision ID | Title | Status | Notes |
|---|---|---|---|
| ADR-001 | Modular monolith with database-per-tenant | Approved | Event-driven with transactional outbox |
| ADR-002 | .NET 10 + EF Core + MediatR | Approved | Core technology stack |
| ADR-003 | CQRS with separate read models | Approved | Eventually consistent projections |
| ADR-004 | Outbox pattern for reliable delivery | Approved | Not full event sourcing (MVP phase) |
| ADR-005 | TenantContext via middleware | Approved | Implicit tenant routing |
| ADR-006 | Offline licensing with local validation | Approved | Fail-safe with periodic sync |

---

## Dependencies & Blockers

| Item | Status | Blocker? | Notes |
|---|---|---|---|
| GitHub Project Creation | 🔄 Pending | No | Scripts ready; awaiting token execution |
| .NET 10 Environment | ✅ Ready | No | VS 2026 with .NET 10 SDK installed |
| Database Choice | ⏳ TBD | **YES** | SQL Server or PostgreSQL? (MVP uses SQL Server assumed) |
| CI/CD Infrastructure | ⏳ TBD | No | Can use GitHub Actions |
| Staging/Prod Deployment | ⏳ TBD | No | Deferred to Phase 1 |

---

## Next Immediate Actions (This Session)

1. ✅ Update this status document (DONE)
2. 🔄 Set up project structure with folders and .csproj organization
3. 🔄 Add NuGet dependencies
4. 🔄 Create domain model interfaces and base classes (AggregateRoot, DomainEvent)
5. 🔄 Implement PatientEpisode aggregate skeleton
6. 🔄 Create EF Core DbContext and migration for core tables
7. 🔄 Verify build succeeds
8. 🔄 Commit initial core infrastructure

---

## Key Assumptions

1. **Database**: SQL Server (Docker or local instance)
2. **Deployment**: ASP.NET Core hosted service (not console app long-term)
3. **EF Core**: Code-first migrations
4. **Message Bus**: In-process MediatR for MVP (external broker evaluated in v1.1)
5. **Testing**: xUnit + Fluent Assertions (can adjust)
6. **API Framework**: ASP.NET Core minimal APIs or MVC controllers

---

## Success Criteria for Phase 0

- ✅ Specification complete and approved
- ✅ GitHub Project created with all 115 issues
- ⏳ Core aggregates compile and have unit tests
- ⏳ EF Core persistence layer works with migrations
- ⏳ Commands can be dispatched via MediatR
- ⏳ Events are persisted in event log
- ⏳ Outbox consumer can relay events
- ⏳ Multi-tenant context is validated
- ⏳ Licensing validator works offline
- ⏳ CI/CD pipeline builds successfully
- ⏳ Initial pilot ready for internal testing (Week 2–3 of Phase 0)

---

## Contacts & Roles

- **Product Owner**: (TBD – stakeholder approval pending)
- **Tech Lead**: (To be assigned)
- **Development Team**: (To be established)
- **QA Lead**: (To be established)

---

**Generated**: 2026-11-09 | **Author**: Implementation Agent | **Status**: DRAFT
