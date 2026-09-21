# Prompt for the next Codex session

Copy the text below into a new session from the repository root.

> Continue Betsi from `main`. Read `docs/UX-IMPLEMENTATION-PLAN.md`, `IMPLEMENTATION_STATUS.md`, `docs/N2-VERIFICATION.md`, and the relevant UI source/tests. The merged baseline is PR #4 (`2b77cc7`). Prioritise UX/UI functionality before further clinical features or pilot work.
>
> Implement **UX-1 only** as the first reviewable increment: visible/retryable episode-load failure, policy-preview invalidation, accurate terminal-episode action presentation under existing rules, and specific accessible action feedback. Start by checking the current worktree and reproducing the behaviours with synthetic data. Add browser/unit regression tests, preserve permissions, tenant isolation, audit and bilingual resources, then run the relevant tests and full verification. Update status/evidence docs with what actually passed.
>
> Do not invent clinical rules or silently change which actions are allowed after discharge/cancellation. If an accountable decision is needed for late documentation or mandatory policy preview, implement the non-controversial UX-1 items first and report the exact decision needed. Do not use real patient data or claim pilot, clinical, Welsh-language or manual accessibility approval.
>
> Work in a focused branch/PR, merge only with green required CI if repository access permits, and report the result plus the next UX increment. Do not proceed to UX-2 until UX-1 is verified and reviewed.
