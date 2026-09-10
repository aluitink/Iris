# Phase 62 — Bug Hunt: shared defect tracker

> Single source of truth for every finding in Phase 62. One row per finding. Slices reference finding IDs (e.g. "fixed B-003 in 62.2"). Do not delete rows — flip Status to `fixed` / `wontfix` / `dup of …`.

## Legend

- **Class** (primary axis — drives *routing* and *fix slice*): `blocker` (stops the review; fix in 62.2) · `bug` (functional defect; fix in 62.3) · `UX` (routes to Phase 63) · `perf` (routes to Phase 64)
- **Severity** (secondary axis — drives *priority* within a class, not routing): `S1` page unusable / data wrong or lost · `S2` major (feature broken, workaround exists) · `S3` minor (cosmetic, small friction). Always set both; the class decides *where* it's fixed, the severity decides *when*.
- **Status**: `open` · `fixing` (assigned to a slice) · `fixed` · `wontfix` · `dup of <ID>`
- **Re-verification rule**: a row may only be flipped to `fixed` after a clean-entry re-pass (PLAN step 3) confirms the repro no longer fails. Record the evidence in the `Verify` cell as **`console-clean + <control/state> works`**, optionally citing a screenshot path (see Screenshot evidence note below). No evidence, no `fixed`.
- **Screenshot evidence note**: the Playwright MCP screenshot tool **cannot write to an arbitrary path** (it fails with ENOENT). The reliable pattern is to call it with **no `filename`** — it saves to `tmp/.playwright-mcp/page-<timestamp>.png` and returns that path in the result. Cite that returned path in the `Verify` cell (or in the finding's notes) *plus* a one-line description of the state shown. Do **not** promise to "attach a screenshot file" — reference the auto-saved path instead. Screenshots are supporting evidence only; the `console-clean + <control/state> works` text is the primary record.

## Page coverage (62.1)

> The "full deep-dive" is only as good as its coverage. Mark each route as it's completed (signed-in as `andrew` **and** authless pass). `skipped` must carry a reason. Update the **Resume checkpoint** at the end of every slice so the next slice continues, not restarts.

| Route | Signed-in pass | Authless pass | Notes / skipped reason |
|---|---|---|---|
| `/` (Home) | ☐ | ☐ | |
| `/home` (HomeTimeline) | ☐ | ☐ | |
| `/directory` | ☐ | ☐ | |
| `/search` | ☐ | ☐ | |
| `/profile` | ☐ | ☐ | |
| `/settings` | ☐ | ☐ | |
| `/notifications` | ☐ | ☐ | |
| `/compose` | ☐ | ☐ | |
| `/object` (deep link + params) | ☐ | ☐ | |
| `/actor` (deep link + params) | ☐ | ☐ | |
| `/community` (deep link + params) | ☐ | ☐ | |
| `/communities` | ☐ | ☐ | |
| `/login` | ☐ | ☐ | |
| `/register` | ☐ | ☐ | |
| `/admin/dashboard` | ☐ | ☐ | |
| `/admin/users` | ☐ | ☐ | |
| `/admin/moderation` | ☐ | ☐ | |
| `/admin/instance` | ☐ | ☐ | |

**Resume checkpoint** (last completed page + state, updated at end of each slice): `—`

**Data-state note** (instance data can drift between slices; record what exists at slice start so repros are interpretable): `—`

## Findings

| ID | Sev | Class | Status | Slice | Page | Repro | Expected | Actual | Verify (`console-clean + <state> works`, + screenshot path if taken) |
|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | |

## Phase 64 — request spam & network efficiency (draft topics)

> **Priority: reducing the *number* of calls the client makes, not raw latency tuning.** The goal is to stop redundant/duplicate requests fired when pages and controls load (e.g. the same feed fetched twice on mount, a refetch on every re-render, an N+1 fan-out where one batched call would do). Latency/size matters only secondarily.
>
> Notes go here as observed during 62.1. Use the fixed column set so 64's distillation is mechanical. **One row per *distinct* request pattern** — collapse repeated identical calls into one row with a `count` (the count *is* the spam signal). Tag the cause; `spam`/`dup`/`N+1` are the high-value tags.

**Cause tags (ordered by value for 64)**: `spam` (redundant call on load/re-render — fetch fires repeatedly or twice for the same data) · `dup` (identical request issued more than once with no new data) · `N+1` (per-item fan-out where one batched call would do) · `refetch-on-rerender` (state change re-triggers an already-fetched load) · `4xx/5xx` · `slow` (secondary) · `large-payload` (secondary) · `other`

| # | Method + path (pattern) | Status | Count (×) | Trigger (what load/control fires it) | Where (page) | Cause tag | Root-cause hypothesis |
|---|---|---|---|---|---|---|---|
| | | | | | | | |

## Console errors (raw log)

> One line per unique console error: page, message, when observed. Duplicates collapse; count noted.

| Page | Message | Count | Status |
|---|---|---|---|
| | | | |

## Test debt (deleted / skipped tests)

> PLAN step 7 allows deleting a test broken by a change and skipping any single test >15 s. Each such action degrades the suite, so it must be logged here — closeout (62.4) reviews this ledger to restore or consciously keep each entry. No silent deletions.

| Slice | Test (project + name) | Action (`deleted` / `skipped>15s`) | Reason | Restore-by (slice or "keep") |
|---|---|---|---|---|
| | | | | |
