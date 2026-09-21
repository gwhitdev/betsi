# UX-1 engineering verification — 2026-09-21

Scope: reliable episode states, policy-preview freshness, terminal episode controls, and
action feedback. Only synthetic browser fixtures were used. This is engineering evidence,
not clinical, Welsh-terminology, manual accessibility, or pilot approval.

## Reproduction and changes

- A failed initial episode query previously left the page blank because its error lived in
  the loaded-episode branch. The isolated browser test takes only the synthetic Glan Clwyd
  test database offline, observes the failed-load alert and Retry, restores the database,
  then confirms the patient page loads. The test restores the database in `finally`.
- A policy preview previously remained visible when the draft changed. The browser test
  previews, then edits threshold, responsible role, deadline, recommended action, follow-up
  owner, reason, tier count, and enabled state. Each edit removes the old preview and announces
  that it is stale. A fresh preview shows the draft version and local preview time.
- Discharged episodes previously displayed a deterioration action that the domain rejects.
  The browser journey now checks that it disappears after discharge. It also checks the
  late-documentation notice and retained observation/carer controls, which the backend
  currently permits. The backend rules were not changed.
- A second caller can change an episode between page load and action. The browser test
  confirms the failed action identifies the synthetic patient, explains the conflict and
  recovery, and does not display raw exception text. Feedback also distinguishes validation,
  permission, and service failures; a unit test checks those mappings.
- A waiting-board patient-name click now has a browser regression that waits for the selected
  episode heading. The development host on port 5080 was still running an older build; its
  Blazor circuit failed during the click and left `Loading…` visible. After restarting that
  host with this branch's build, the same synthetic navigation loaded and remained stable.

## Verification

- `dotnet build Betsi.slnx --no-restore`: passed, zero warnings/errors.
- Full .NET suite with Cobertura: **508 passed, zero failed, zero skipped**.
  Domain coverage **95.8%** and Application coverage **86.6%**; both exceed the 70% gate.
- Both `dotnet-ef migrations has-pending-model-changes` contexts: no drift.
- Full Chromium browser suite: **42 passed**, including the new UX-1 journeys and
  existing tenant-isolation, permission, English/Welsh, recovery, and axe checks.
- Before the last cancelled-state test was added, all **5 browser tests** affected by
  the final action-label and episode-route edits passed again in English/Welsh where applicable.
- `git diff --check`: passed.

The browser fixture host now issues two-minute test cookies so the simulated database outage
can recover before the existing session-expiry check. Normal development sessions are unchanged.

## Decisions and limits

- **Clinical owner:** define which observation, correction, carer, staff, and safeguarding
  records may be added after discharge or cancellation, and how each should be described.
  Current UI reflects the existing backend allowances; only the prohibited deterioration
  action is hidden. No new clinical permission or threshold was introduced.
- **Policy owner:** decide whether a current impact preview is mandatory before proposal.
  This change marks stale previews and allows proposal under the existing rule.
- Qualified Welsh terminology review, screen-reader/device checks, clinical safety review,
  and pilot approval remain outstanding. Automated axe checks do not replace them.
