# DSPT evidence pack

Maps each NHS Data Security and Protection Toolkit theme to what this system does, where the
evidence is, and what is still missing (MVP-113). **This is a preparation document, not a
submission.** A submission is made by an organisation, names a person for each assertion, and
covers policies and training this repository cannot contain.

**Status**: prepared 2026-09-16 against Phase I. Not reviewed by a Caldicott Guardian, DPO or
clinical safety officer — all three are unassigned, which is the largest gap in this document.

---

## 1. Personal confidential data

| Assertion | State | Evidence |
|---|---|---|
| Data flows are documented | 🔄 Partial | `design/spec.md` §2–3; `docs/ARCHITECTURE.md`. A site-specific DPIA is still needed. |
| Data is only shared with a legal basis | 🔄 Partial | Outbound webhooks carry no patient identifiers — only ids, states and timings (`WebhookEventCatalog`, asserted in `IntegrationApiTests`). A subscriber wanting demographics calls the episode API with its own credentials, and that read is audited. |
| Minimum necessary | ✅ | Webhook payloads use an allow-list per event type, not an exclusion list; a new field is not published until someone adds it deliberately. Metrics and logs carry no patient identifiers. |
| A Caldicott Guardian is named | ⛔ Missing | No named individual. |

## 2. Staff responsibilities and access

| Assertion | State | Evidence |
|---|---|---|
| Access is role-based and least-privilege | ✅ | `Betsi/Security/Permissions.cs`; every endpoint and every command declares a permission, enforced by test. Site Administrator sees no patient data. |
| Access is authenticated against a trusted identity provider | ✅ | OIDC bearer tokens, asymmetric algorithms only. Tenant and role come from verified claims; the service refuses to start outside Development without authentication configured. |
| Privileged operations are separated | ✅ | Control-plane operations (provision, suspend, migrate, backup, restore, destroy) are a command line on the host, not an HTTP API. Host access is already a privileged boundary. |
| The role matrix has been approved | ⛔ Missing | Needs information-governance sign-off. Recorded as an open decision in `IMPLEMENTATION_STATUS.md`. |
| Staff training records | ⛔ Out of scope | Organisational. |

## 3. Audit and monitoring

| Assertion | State | Evidence |
|---|---|---|
| Every access to patient data is logged | ✅ | `AuditBehaviour` writes an audit row for every command, success or failure; patient-data reads are audited; authorisation denials are audited. |
| Audit records cannot be altered | ✅ | Event and audit tables reject UPDATE and DELETE at the database. |
| Operator actions are logged | ✅ | `control.AuditLog`: provisioning, suspension, migration, licensing, backup, restore, export, destruction — each with actor, outcome and detail. |
| Logs are monitored | 🔄 Partial | Structured JSON logs, OpenTelemetry traces and metrics, documented alert rules — [`runbooks/observability-and-incidents.md`](runbooks/observability-and-incidents.md). No aggregator or paging tool is deployed; a site must supply both. |
| Logs contain no unnecessary personal data | ✅ | The command pipeline logs command name, tenant, actor and role, never the command body. |

## 4. Managing data security risks

| Assertion | State | Evidence |
|---|---|---|
| A risk register exists | ⛔ Missing | Needs the DCB0129 hazard log, which needs the clinical safety officer. |
| Tenant isolation | ✅ | Database per tenant; the connection string is chosen per request from the resolved tenant; `TenantIsolationTests` proves tenant B cannot read tenant A's rows. |
| Cross-tenant penetration test | ⛔ Missing | Needs an external tester. |
| Dependencies are scanned | ✅ | CI fails on vulnerable packages and reports deprecated ones, on every pull request. |
| Secrets are not in source control | ✅ | `secret:` references resolve from a mounted secret store at startup and fail closed; no credential in `appsettings`; licence private keys are git-ignored. |

## 5. Process reviews and incidents

| Assertion | State | Evidence |
|---|---|---|
| An incident response plan exists | ✅ Documented | [`runbooks/observability-and-incidents.md`](runbooks/observability-and-incidents.md) §6, including breach notification within 72 hours and clinical-safety escalation during the incident. |
| Incidents are reviewed | 🔄 Documented, unexercised | Blameless postmortem within five working days. No incident has occurred, so the process has never run. |
| Business continuity has been tested | ⛔ Missing | The restore drill is defined ([`runbooks/backup-and-restore.md`](runbooks/backup-and-restore.md) §5) and has never been run against a deployed environment. |

## 6. Continuity and backup

| Assertion | State | Evidence |
|---|---|---|
| Backups are taken | ✅ Capability | `tenants backup`, full and log, verified with `RESTORE VERIFYONLY` at the time of taking. Schedule in the runbook. Proved against SQL Server in `SqlServerTenantDataTests`. |
| Backups are encrypted | 🔄 Optional | `--certificate` encrypts with AES-256. Without it the command warns and the audit entry records "unencrypted". **A site must configure the certificate.** |
| Restores are tested | ⛔ Missing | Monthly drill defined; never run outside the automated test. |
| Recovery objectives are stated | ✅ Stated | RPO ≤ 5 minutes (5-minute log backups), RTO ≤ 30 minutes. Neither has been measured against a real dataset. |
| Data can be exported and destroyed | ✅ | `tenants export` (full JSON export, audited by row count only) and `tenants destroy` (suspended + name confirmation + audit, tombstone retained). |

## 7. Unsupported systems and patching

| Assertion | State | Evidence |
|---|---|---|
| Supported platform versions | ✅ | .NET 10 (LTS), SQL Server 2022, EF Core 10. |
| Patching is routine | 🔄 Partial | CI fails on vulnerable packages, so an unpatched dependency blocks a merge. No scheduled base-image rebuild — the image should be rebuilt on a cadence, not only when the code changes. |

## 8. Network and infrastructure security

| Assertion | State | Evidence |
|---|---|---|
| Traffic is encrypted | 🔄 Partial | HTTPS redirection outside Development; TLS termination and certificate management belong to the site's infrastructure. Webhook targets must be HTTPS — plain HTTP is a Development-only setting the service refuses to start with elsewhere. |
| SSRF protection | ✅ | Webhook targets are checked at connect time; private network targets are refused outside Development. |
| SQL injection | ✅ | EF Core parameterises everything; the operator commands that must build identifiers validate them against a strict pattern and pass paths as parameters. |
| Secrets at rest | 🔄 Partial | Webhook and inbound secrets are encrypted with a Data Protection key ring shared through the control-plane database. The ring itself is encrypted only if `DataProtection:CertificatePath` is set — the service warns on every start when it is not. |
| Penetration test | ⛔ Missing | Needs an external tester: authentication and cross-tenant isolation. |

---

## What has to happen before a submission

1. Name a **clinical safety officer**, a **Caldicott Guardian** and a **DPO**. Nothing in §1, §4
   or §5 can be asserted without them.
2. Complete a **DPIA** for the pilot site.
3. Run the **restore drill** against a deployed environment and record the elapsed time.
4. Configure **backup encryption** and **key-ring encryption** certificates.
5. Commission the **penetration test**: authentication, and cross-tenant isolation.
6. Approve the **role-to-permission matrix** with information governance.
7. Deploy a **log aggregator and paging tool**, and prove one alert reaches a person.
