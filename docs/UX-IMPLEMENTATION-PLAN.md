# Betsi UX/UI functionality implementation plan

**Created:** 2026-09-21
**Baseline:** `main` after [PR #4](https://github.com/gwhitdev/betsi/pull/4), merge `2b77cc7`.
**Purpose:** make existing patient-flow and escalation workflows understandable, safe to operate, and recoverable before further clinical feature work or pilot acceptance.

This is an engineering and usability plan, **not** clinical approval. Preserve existing domain rules, permissions, tenant isolation, audit events, bilingual behaviour and API contracts unless a change is explicitly reviewed. Use synthetic data only. Do not deploy to a pilot or claim WCAG/clinical acceptance from automated checks.

## Baseline and problem statement

The merged baseline passed all required CI jobs (build/.NET tests and coverage, migrations, dependency audit, Chromium browser/accessibility tests). Automated coverage does not show whether a clinician can find the right task quickly, understand the consequences of an action, or recover from a failed one.

The highest-value observed issues are:

- `Betsi/UI/Components/Pages/EpisodeDetail.razor`: patient flow, clinical safety, a long observation form and full history share one page. Starting a correction changes a form far above the clicked history item without moving focus or view. Active-looking entry controls remain visible on a discharged episode; whether late entry is allowed must be decided explicitly, not inferred from the display.
- `EpisodeDetail.razor`: an initial data-load exception sets `_error`, but the error is rendered only inside the `_episode is not null` branch, so the page can appear blank.
- `Betsi/UI/Components/Pages/PolicyEditor.razor`: a preview remains visible after the proposal fields change and may no longer describe the displayed draft.
- `Betsi/UI/Components/Pages/WaitingBoard.razor`: a live refresh reloads only page one and discards rows opened with “Show more.”
- `Betsi/UI/Components/Pages/EscalationDashboard.razor`: four long tables compete for attention; a single `_busy` blocks unrelated actions, and feedback does not identify the affected row clearly.
- `Betsi/UI/Components/Layout/MainLayout.razor`: navigation advertises links to users who do not have permission to use them.

## Delivery order

Each increment should be a reviewable PR. Do not start the next increment while the previous one has failing CI. Keep tests coupled to the behaviour they protect. Update `IMPLEMENTATION_STATUS.md` and the relevant verification notes with evidence, not just completion claims.

### UX-1 — Reliable states and safe feedback (first PR; pilot-blocking)

1. Give the episode page explicit loading, not-found, failed-load and loaded states. On failed load, show a plain-language error and Retry action; never render an empty page that resembles “no patient.” Keep internal exception details in logs, not the user-facing message.
2. Invalidate or clearly mark a policy preview as stale whenever any field affecting it changes, including add/remove tier and enabled state. Show the draft version/time used for preview. A user must never be able to mistake an old preview for the current draft. Decide with the policy owner whether a current preview is mandatory before proposal; do not silently introduce a new approval rule.
3. Define the UI behaviour for discharged/cancelled episodes against existing backend rules: hide prohibited actions, and label any legitimate late documentation or correction as such. Confirm the policy with a clinical owner before changing allowed actions.
4. Make mutation feedback specific: name the affected patient/task, distinguish validation, stale-version/conflict, permission and temporary service errors, and provide a clear recovery action. Do not erase errors on an unrelated live refresh.

**Acceptance:** browser tests cover failed initial load/retry, stale-preview invalidation for every relevant field, terminal-state controls, and a concurrent-action conflict. Errors are announced accessibly, do not expose raw exception text, and do not leave an ambiguous spinner or blank page.

### UX-2 — Episode task flow and observation entry (second PR)

1. Replace the single long page with clear task sections or routes: Overview, Patient flow, Clinical safety, Record observation, and History. Keep patient name, date of birth/NHS identifier, department and episode state visible at the point of every consequential action. Choose the exact navigation pattern after a quick keyboard/mobile prototype; avoid hidden tabs that obscure urgent status.
2. Make observation entry progressive: group vital signs, pain, SBAR and structured findings; reveal optional detail when relevant. Preserve unsaved input while moving within the entry workflow. Add field-level constraints and error summaries linked to fields, using existing validators and approved measurement ranges rather than inventing new clinical rules.
3. Make correction a distinct mode reached from the selected history record. Identify the source record and timestamp, focus the correction heading/form, highlight changed values, provide Cancel, and return focus/context to the corrected history item after save. Do not mutate or conceal the superseded record.
4. Make history scannable with current-versus-superseded status, type/date filtering and concise summaries; retain access to the complete audited record. Check performance with many observations and avoid loading an unbounded history if it becomes slow.

**Acceptance:** a clinician can complete a new entry and a correction using keyboard alone and at 400px width; identity and episode state are clear at submission; interrupted/cancelled correction cannot accidentally save; the original and corrected records remain visible. Add Playwright journeys in English and Welsh and tests for model/validation persistence across UI transitions.

### UX-3 — Operational boards (third PR)

1. Preserve waiting-board context across live updates: loaded page count, filters, sort and reading/focus position. Reconcile changed rows without duplicates; make newly arrived or changed rows discoverable without unexpectedly jumping the viewport. Keep the explicit live/offline/last-updated states.
2. Add practical waiting-board filters/search using existing authorised query capabilities (for example location, state and wait duration); validate exact filter set with staff. Keep filter state in the URL if safe, but never put patient identifiers or sensitive text into shareable URLs.
3. Reorganise escalations around actionable work: awaiting acknowledgement, active, overdue follow-up, then history. Add role/owner/tier filters only where the data model makes responsibility unambiguous. Prefer a detail panel or route for reassignment, resolution and audit history over repeated inline controls.
4. Use per-row busy/success/error state and a clear confirmation/outcome for consequential actions. Preserve the user's table position and filters after a mutation or reconnect. Ensure live changes cannot silently remove a row while its action form is open.

**Acceptance:** browser tests cover multi-page live refresh, filtering, offline/reconnect, concurrent updates, action-specific feedback and keyboard navigation; no duplicate/missing visible row after reconciliation. Re-run the 150-open-escalation performance scenario and measure interaction latency as well as render latency.

### UX-4 — Policy decisions, visual system and human validation (fourth PR/gate)

1. Show an explicit comparison of proposed policy versus in-force revision, including thresholds, responsible roles, deadlines and enabled state. Make the independent proposer/approver roles and decision history clear; do not change the underlying separation-of-duties rule.
2. Replace free-text role entry with controlled choices only if they can be derived from an approved site configuration. Otherwise add clear examples, inline validation and a review step; do not hard-code clinical ownership.
3. Apply consistent spacing, hierarchy, status labels, button language, focus treatment and responsive table/form patterns across the pages. Show permissions-appropriate navigation without weakening route/server authorization. Ensure colour is never the only status cue.
4. Conduct task-based usability sessions with representative nurses/coordinators and policy reviewers on real target devices. Run qualified Welsh terminology review, NVDA/VoiceOver checks, keyboard/zoom/reflow checks and physical tablet sleep/wake/reconnect checks. Record findings, severity, owner and retest evidence.

**Acceptance:** users can explain which policy is in force, what a proposal changes and who may approve it; all automated tests pass; high-severity usability/accessibility findings are resolved and retested. Any deferred finding has an accountable owner and explicit pilot decision.

## Common definition of done

- No regression in permissions, tenant isolation, audit trail, Welsh/English resource parity, or terminal-state safeguards.
- `dotnet build Betsi.slnx`, full `.NET` tests/coverage gate, migration-drift check, and `npm run test:ui` pass locally where available and in PR CI. Add regression tests for each fixed interaction; keep axe checks but do not treat them as manual accessibility evidence.
- Use synthetic fixtures. Do not use real patient data, change clinical thresholds, or deploy to a pilot as part of this UX tranche.
- Merge only after required CI is green. Record screenshots/task evidence and remaining risks in verification docs. Clinical, Welsh and accessibility reviewers retain their separate sign-off gates before any pilot.

## Decisions needed before implementation crosses their boundary

The engineering work can start with UX-1 loading/error/preview behaviour. Before changing permitted actions or role choices, obtain an accountable decision on: (1) late documentation/correction after discharge or cancellation, (2) whether preview is required for policy proposal, (3) the approved source of responsible-role choices, and (4) which board filters and task ordering match actual staff workflow. Until then, display the existing rules accurately and avoid introducing new clinical semantics.
