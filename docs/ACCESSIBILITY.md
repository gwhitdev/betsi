# Draft accessibility statement

Prepared 2026-09-18 for the development waiting-room and escalation boards. A deployment
owner must review this draft, add its contact/feedback route and publish a site-specific
statement before public-sector deployment. It does not declare full accessibility conformance.

The boards provide labelled navigation, a skip link, visible focus, semantic tables, named
patient action buttons, English/Welsh selection and connection-status messages. Wide data
tables can be scrolled within labelled keyboard-focusable regions without scrolling the whole
page. Lost connections display a warning and disable stale board interactions until a fresh
page can be loaded.

Automated checks cover both languages, empty/populated/disconnected states, error feedback,
keyboard focus and narrow/large-text layouts. Reports and reproduction steps are in
[N2 verification](N2-VERIFICATION.md). Automated axe results cover only the rules and states
tested; they do not establish screen-reader usability or correctness of clinical terminology.

Outstanding checks, deferred at the user's request on 2026-09-18:

- A Welsh NHS terminology reviewer must review the resource files and error/status language.
  Some role labels and configured department names currently remain in English.
- A human reviewer must check NVDA or VoiceOver output, table column announcements, live
  updates, focus order and patient-specific action names.
- A device reviewer must check tablet sleep/wake, touch operation and real 200%/400% browser
  zoom. Browser freeze and text-size automation are supporting evidence only.
- The deployment owner must supply an accessibility contact, feedback/escalation process,
  review date and assessment of any remaining barriers for its site.

Record each reviewer's name, date, browser/OS/assistive technology, scenario, result and
remediation evidence in the runbook checklist. None of those human reviews has been completed
or implied by the automated checks.
