# Iris — Completed Phase Ledger

> Append-only. This file is **not** part of the per-turn reading loop — [PLAN.md](../PLAN.md) is that document. Add one line when a phase (or a slice group within the active phase) fully closes out; never rewrite history here. If PLAN.md's "Up Next" needs replenishing, this is where to look for what comes after the current phase.

## How to add an entry

- One line per closed phase (or, for the active phase, one line per closed slice group).
- Format: `- **Phase N — <title>:** <one-sentence summary>.` Optionally link the closing change doc.
- Do not edit older entries. If a phase needs a correction, add a new line noting it — don't rewrite history.
- Detailed rationale, test counts, and design decisions belong in [changes/](changes/README.md), [decisions/](decisions/README.md), and [phase-notes/](phase-notes/README.md) — link to them, don't inline them here.

## Ledger

- **Phase -1 through 18 — platform and runtime foundation:** project reorganization, core identity/signing, server foundations, inbox/delivery, communities, proxy fallback, sample apps, deployment prep, hardening, and operational readiness (bounded concurrency, file-backed queueing, dead-letter handling, rate limits, opt-in persistence, OAuth2, bearer validation, health checks, graceful shutdown, metrics, retry, transport hardening).
- **Phase 19-21 — federation and protocol maturity:** ActivityPub write/read flows stable across local and cross-instance scenarios; outbox established as the source of truth; follow, block, flag, mute, reply, like, boost, delete, and community lifecycle flows implemented and verified in the sample stack; server-side rules for audience metadata, cache bypass, and proxy fallback landed.
- **Phase 21 — explorer maturity groundwork:** sample explorer moved from a basic demo to a functional review surface; shared components for object viewing, paged collections, raw JSON inspection, and consistent loading/error/empty states; actor/object/community/instance pages rendering in navigable form; cross-instance reads and multi-server identity/navigation working in the sample environment.
- **Phase 22 (in progress) — functional explorer rebuild:** see [PLAN.md](../PLAN.md) for current status and [plans/22-sample-ui-user-stories.md](plans/22-sample-ui-user-stories.md) for the deep-dive plan. Slices 22.1 through 22.6 (shared components, detail pages, authoring surfaces, cross-instance reads, broad story review, `PagedCollection` consolidation) are closed; remaining closeout items are tracked in [plans/phase-22-closeout.md](plans/phase-22-closeout.md).
