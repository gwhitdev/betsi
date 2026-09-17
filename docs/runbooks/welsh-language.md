# Runbook: Welsh language and accessibility

How the interface is translated and kept accessible, and what is outstanding before a pilot.

---

## 1. The obligation

The Welsh Language (Wales) Measure 2011 requires public services in Wales to treat Welsh no less
favourably than English. For this system that means Welsh is not a translation pass added when
the screens are finished: every string is in a resource file from the first screen, and a screen
that cannot be rendered in Welsh is not finished.

Accessibility is WCAG 2.2 AA, which is the standard the Public Sector Bodies (Websites and
Mobile Applications) Accessibility Regulations 2018 require.

## 2. Where the strings live

| File | Holds |
|---|---|
| `Betsi/UI/Resources/Strings.resx` | English (`en-GB`), and the key set |
| `Betsi/UI/Resources/Strings.cy-GB.resx` | Welsh |

Components use `IStringLocalizer<Strings>`; no visible text is written into markup, including
the English. Two resource files with one key set cannot drift; two markups can.

A missing Welsh key falls back to English rather than showing the key. That is the right
failure — a clinician sees a usable screen — but it is silent, so §5 exists.

## 3. ⚠ The Welsh needs review

**The Welsh translations in this repository were produced by the developer, not by a Welsh
speaker or a professional translator.** They are a working placeholder so that the interface can
be built and tested bilingually from the start. Clinical terminology in particular
("escalation", "triage", "acknowledgement") carries specific meaning in a Welsh NHS setting and
the current wording has not been checked against it.

**Before any pilot**, the resource file must be reviewed by a Welsh speaker familiar with NHS
Wales terminology, ideally through the health board's own translation service. Budget for it as
a task, not as a favour.

## 4. How a person switches language

The picker in the header posts to `/language`, which sets the standard ASP.NET culture cookie.
It is a POST, not a link: a GET that changes state is something a prefetching browser does on
its own. The choice survives sign-out, because the language someone reads in is a preference,
not part of their identity.

The cookie is `IsEssential`, so it is not suppressed by consent settings — a person cannot
consent to a cookie banner they cannot read.

## 5. Checks before a screen is called finished

Per screen, by hand:

1. **Keyboard only.** Unplug the mouse. Every action reachable, focus always visible, and the
   skip link working. Focus moves to the `<h1>` on navigation.
2. **In Welsh.** Switch language and read it. Welsh runs roughly 20–30% longer than English;
   look for truncation and wrapped buttons, not only for missing translations.
3. **Zoomed to 200%** and at 400px wide. Nothing lost, nothing overlapping, no horizontal
   scrolling of the page.
4. **Colour.** Every state conveyed in words as well as colour — an overdue row says "Overdue",
   it is not merely red. Check with a colour-blindness simulator.
5. **Screen reader.** At minimum NVDA on Windows or VoiceOver on macOS: table headers announced
   with each cell, live regions announcing updates without interrupting, buttons naming the
   patient they act on.
6. **Automated.** axe or Lighthouse for the mechanical failures. It catches perhaps a third of
   what matters; it is a floor, not the test.

## 6. What is outstanding

| Item | State |
|---|---|
| Welsh reviewed by a Welsh speaker | ⛔ Not done — see §3 |
| Automated axe checks in CI | ⛔ Not built. Needs a headless browser in the pipeline |
| Screen-reader pass | ⛔ Not done |
| Accessibility statement (a legal requirement for a public-sector service) | ⛔ Not written. It must be honest about §3 and about anything found in §5 |
| Contrast, focus, target size, reduced motion, semantic tables | ✅ Built into `app.css` and the components from the first screen |
