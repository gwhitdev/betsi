# N2 browser verification

Engineering evidence for the waiting-room and escalation boards. The browser job passed in
[PR #4](https://github.com/gwhitdev/betsi/pull/4) CI on 2026-09-21. Human Welsh terminology and manual
screen-reader/device reviews were explicitly deferred by the user on 2026-09-18.

## Reproduce

Requires .NET 10, Node 24, Docker and Chromium's system dependencies:

```bash
docker compose up -d --wait sqlserver keycloak
npm ci
npx playwright install --with-deps chromium
dotnet build Betsi.slnx
npm run test:ui
```

Playwright starts and stops its own host on `http://localhost:8080`; that port must be free.
The normal development application on port 5080 can remain running. The host uses the
`betsi_browser_control`, `betsi_browser_glan_clwyd` and `betsi_browser_wrexham` databases,
never the normal development tenant databases. Keycloak uses the existing development realm.
The suite creates only uniquely named synthetic patients. Cleanup cancels their episodes and
resolves their escalations through the audited APIs, including interrupted-run fixtures;
history and audit records are retained in the browser-test databases.

The test host issues two-minute cookies to exercise actual ticket expiry while allowing the
UX-1 simulated SQL outage and retry journey to complete. Normal development
sessions remain twelve hours. By default the runner does not reuse a server on port 8080,
preventing accidental tests against an unrelated application. `BETSI_UI_REUSE_SERVER=1` is an
explicit local diagnostics override.

## Coverage

| Requirement | Evidence |
|---|---|
| Sign-in visible and usable | Anonymous board → Keycloak form → authenticated waiting and escalation boards |
| Department isolation | Two real OIDC users, both boards, separate synthetic patients and live foreign-tenant changes |
| Site Administrator refused | Both boards render refusal with no patient tables |
| Acknowledge and resolve | Browser buttons change persisted state; API audit verifies events and actor role |
| Concurrent action feedback | A second caller resolves the escalation while its notification is withheld; stale action shows an error after refresh, in both languages |
| Accessible board states | axe WCAG 2/2.1/2.2 AA rules on both boards in English/Welsh, empty/populated/disconnected states, plus escalation-action errors |
| Keyboard and layout | Skip link, navigation focus, 400px layout, 200% text enlargement, scrollable labelled table regions |
| Lost connection | Offline warning, disabled stale actions, fresh authorised data after reconnect on both boards |
| Failed initial subscription | Retry and refetch after the board hub returns |
| Suspended browser | Chromium page freeze/resume with a missed change; does not establish physical tablet acceptance |
| Session expiry and logout | Actual two-minute cookie expiry closes a running circuit and removes patient data; full Keycloak logout and refusal on reopening |
| Live latency | Five command-submission-to-render samples on the waiting board; five on a dashboard starting with 150 open escalations |
| Translation completeness | .NET resource-key parity test; terminology correctness still needs human review |
| CI | `browser` job installs Chromium, starts SQL/Keycloak, builds, runs tests and uploads evidence even on failure |

Timing starts before submitting the command and ends when the browser assertion observes the
new patient. This includes the database commit, transport and render, so it is a conservative
upper bound on committed-change-to-render latency. It is not a production capacity claim or
a measure of the waiting-time monitor's detection interval. The workload is one browser,
local SQL Server/Keycloak and synthetic data, with a one-second maximum per sample.

## Fixes found by the checks

- The signed-out layout hid the login prompt (fixed before this N2 run and regression tested).
- A server-side hub client could not use the browser's cookie. The browser now connects to
  the authenticated tenant hub using a pinned, locally served SignalR client.
- The reconnect handler only displayed a warning. It now keeps stale content inert and
  requests a fresh page when the server returns, revalidating login and refetching data.
- Circuit and board hub connections now close when the authentication ticket expires.
- The language endpoint bound query parameters instead of the posted form fields.
- Logout lacked the client ID needed by Keycloak when no ID-token hint is stored.
- The skip link resolved against the root base URL and lost focus to routing.
- Narrow tables hid their headers without replacement labels; they now retain semantic
  headers inside labelled, keyboard-scrollable regions.
- Created escalations were also shown in the acknowledged section, offering an invalid
  Resolve action. The section now contains acknowledged/escalated cards.
- Refetching erased command failure feedback. Action errors now persist independently.

## Artifacts and limits

`TestResults/browser/` holds axe JSON and rendered screenshots. The HTML report is
`TestResults/browser-report/index.html`; failed tests additionally retain Playwright traces.
These artifacts contain synthetic fixtures and are gitignored. CI uploads both directories
as `browser-evidence`. Do not run this fixture suite against real patient data.

Remaining review tasks are in
[the draft accessibility statement](ACCESSIBILITY.md) and the
[Welsh/accessibility runbook](runbooks/welsh-language.md).

Deferred: qualified Welsh review, NVDA/VoiceOver review, physical tablet sleep/wake and actual
browser-zoom checks. Chromium text enlargement and freeze simulation do not replace those.
The current role and configured department names can still appear in English in Welsh mode.
No claim of complete WCAG conformance, clinical approval or final N2 acceptance is made.

## Local results — 2026-09-19

- Solution build: zero warnings and errors.
- Full .NET suite: **497 passed, zero failed, zero skipped**, including SQL Server migration,
  rollback, API contract and the new resource-parity check.
- Coverage: Domain **93.2%** (1115/1196 lines), Application **83.3%** (1493/1793 lines).
- Browser verification: **37 passed, zero failures**, in the latest complete Chromium run.
- **27 axe reports, zero violations** across the states/layouts listed above; this is not a
  substitute for the deferred human reviews.
- Waiting-board and 150-open-escalation latency checks passed: every one of the five samples
  per scenario was below one second from command submission to observed render.
- Shell/JavaScript syntax, CI YAML parsing and `git diff --check` passed. The new CI job is
  configured but has not been executed remotely.

Coverage is in `TestResults/n2.cobertura.xml` and the local runner log in
`/tmp/betsi-n2-dotnet.log` (ephemeral).

## Follow-up local results — 2026-09-21

- Full Chromium suite: **38 passed, zero failures**, including registration, observation
  correction, policy, clinical workflows, tenant isolation, recovery and accessibility checks.
- Full .NET suite: **504 passed, zero failures, zero skips**. Coverage: Domain **95.8%**,
  Application **86.6%**, both above the 70% gate.
- The browser test found a stale Blazor edit-context bug in observation correction, now fixed
  and regression-tested. Keycloak's development volume and import mounts were corrected.
- Remote CI passed on PR #4. Qualified Welsh review and manual screen-reader/device checks remain open.
