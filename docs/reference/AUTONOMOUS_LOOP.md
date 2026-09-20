# Iris — Loop Instructions (redirect)

> **This file is retired.** The single autonomous loop was split into two parallel workstreams, each with its own operating instructions:

- **[DEV_LOOP.md](DEV_LOOP.md)** — the **developer** loop: owns code + tests + the **Dev Queue** (Inbox, re-verify debt, feature scope). Includes the Definition of Done, work-ordering rules, the **redeploy-error rule**, and doc-hygiene rules.
- **[QA_LOOP.md](QA_LOOP.md)** — the **QA** loop: owns the live app's behavior + the **QA Queue** (`docs/qa/`). Includes the Playwright pass protocol, **worktree isolation** (via `scripts/qa-worktree.sh`), triage-to-doc, and the fix re-verify contract.

Both loops are driven by [PLAN.md](../../PLAN.md) and share the [ownership map](DEV_LOOP.md#ownership-map-planmd) so they never write the same file.

The doc-maintenance rules ("Keeping the docs lean") now live in [DEV_LOOP.md](DEV_LOOP.md#keeping-the-docs-lean).
