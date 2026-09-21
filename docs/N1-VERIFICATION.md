# Observation recording: engineering verification

Reviewed 2026-09-18 against the current, uncommitted workspace. This is engineering evidence
for synthetic-data development, not clinical sign-off or evidence of a merged PR.

## Results (2026-09-18)

| Check | Result |
|---|---|
| Solution build, warnings as errors | Passed: 0 warnings, 0 errors |
| Final full test suite with coverage | Passed: 489/489, 0 failed, 0 skipped; 2m 07s |
| SQL Server verification | Included in the full run: competing corrections, migration application, rollback/reapply and the existing tenant suites; no Docker skips |
| Domain coverage | 93.2% (1115/1196 lines), above the 70% gate |
| Application coverage | 83.3% (1493/1793 lines), above the 70% gate |
| Tenant and control-plane model drift | Both report no pending model changes |
| OpenAPI contract | Passed in the full suite |
| Whitespace validation | `git diff --check` passed |

Coverage artifact: `TestResults/n1.cobertura.xml`. Final local run log:
`/tmp/betsi-n1-full.log` (temporary, not committed). CI now uses the same direct argument form
and an explicit coverage output path. Remote CI was not run during this verification.

## Requirements and evidence

| Requirement | Evidence |
|---|---|
| Record and retrieve observations with correction history | `PatientJourneyTests.An_observation_can_be_recorded_corrected_and_read_with_history` |
| Tenant boundaries, forbidden readers/writers, cross-episode corrections | `Observation_history_is_tenant_isolated_and_role_protected` and `Corrections_cannot_cross_episodes_or_tenants_and_writes_require_a_clinical_role` |
| Invalid input and stale versions | `Invalid_observations_are_rejected_without_creating_records` and `A_correction_with_a_stale_observation_version_is_rejected` |
| Idempotent recording and correction | `A_retried_observation_envelope_records_exactly_one_observation_and_event` checks both recording and correction retries, state, events, outbox and successful command audit; a correction replay works after the original's version advances |
| Atomicity through the API, including failure auditing | `A_database_failure_during_correction_preserves_the_original_and_audits_failure` injects a database failure while inserting the supersession event |
| Competing writers on SQL Server | `SqlServerMigrationTests.Competing_observation_corrections_commit_one_chain_with_matching_events` loads the original in two contexts before either commits, then verifies that the loser leaves no observation, event or outbox row |
| Original measurements retained | API history test, `ClinicalObservationTests`, and SQL Server rejection of measurement edits/deletion through normal persistence |
| Reference scoring boundaries | `ClinicalScoringTests`: all Scale 1 band boundaries, oxygen/consciousness, incomplete/invalid inputs and the sixteenth birthday |
| Generated schema, rollback/reapply | `AddClinicalObservations`, both model-drift checks, and `MigrationRollbackTests` |
| API contract | `OpenApiDocumentTests` and `docs/openapi/v1.json` |

## Scoring reference and limits

Reference: Royal College of Physicians, *National Early Warning Score (NEWS) 2: Standardising
the assessment of acute-illness severity in the NHS*, 2017, Chart 1 (printed page 29, PDF page
52), [original report](https://www.rcp.ac.uk/media/a4ibkkbf/news2-final-report_0_0.pdf).
The engineering tests independently enumerate the adult Scale 1 boundary examples from this
chart. RCP attribution applies to the scoring system; the tests do not establish clinical approval.

The calculator now refuses negative measurements, invalid oxygen saturation/consciousness and
temperature precision beyond one decimal place. Previously, a value such as 35.05 could miss
every band and silently score zero. The API already rejects that precision; the calculator now
also rejects it for callers that bypass API validation. Pain-scale identifiers must be defined.

The implementation supports Scale 1 only. It cannot determine whether pregnancy or a need for
Scale 2 makes that calculation inappropriate; this remains an explicit H-05 clinical-use gate.
Age checks alone do not establish eligibility. No score triggers escalation automatically.

## Transaction and retry contract

Observation corrections commit the replacement, original's supersession marker, and both
aggregates' events/outbox in one `IUnitOfWork` save. Command audit records are saved separately.
On SQL Server, either the optimistic version check or the unique correction index can detect a
competing writer. Both become HTTP 409; unrelated database failures retain their error outcome.
The failure path clears tracked changes before auditing, preventing an audit save from flushing
a failed correction. A crash after commit but before audit remains a documented gap.

Retry deduplication uses `POST /api/v1/commands` with `RecordObservation` and an idempotency key.
Direct observation POSTs do not deduplicate. The existing envelope's reservation/result storage
uses separate saves: process failure between command commit and recording its result remains
an inherited recovery limitation, not an exactly-once guarantee.

## Reproduction

Run `dotnet build Betsi.slnx --no-restore -m:1 -p:UseSharedCompilation=false`, then:

```sh
dotnet test Betsi.slnx --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output "$PWD/TestResults/n1.cobertura.xml"
python3 tools/coverage-gate.py TestResults/n1.cobertura.xml --threshold 70
dotnet dotnet-ef migrations has-pending-model-changes --project Betsi/Betsi.csproj --context BetsiDbContext --no-build
dotnet dotnet-ef migrations has-pending-model-changes --project Betsi/Betsi.csproj --context ControlPlaneDbContext --no-build
```

The .NET 10 Microsoft Testing Platform runner takes its test arguments directly; do not put
`--` before the coverage options. Docker must be available for the SQL Server evidence.
