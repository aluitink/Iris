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
| `/` (Home) | ☑ | ☑ | Signed-in: signed-in card + shortcuts; deep-refresh clean. Authless: hero + public timeline (4 posts), Refresh + Load more work. |
| `/home` (HomeTimeline) | ☑ | ☑ | Authless: 302 → login (correct gating). Signed-in: own + followed posts, boosts render as "View boosted post →", engagement state correct (like/boost pressed on boosted items). Heavy request spam on load — see 64 rows 1–5. |
| `/directory` | ☑ | ☑ | Authless: 302 → login. Signed-in: People tab lists 3 actors. **Defects B-001 (actor links dead), B-002 (Communities tab inert).** |
| `/search` | ☐ | ☑ | Authless: 302 → login (gating OK). Signed-in pass pending. |
| `/profile` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/settings` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/notifications` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/compose` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/object` (deep link + params) | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending (deep link + hard refresh). |
| `/actor` (deep link + params) | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending (deep link + hard refresh). |
| `/community` (deep link + params) | ☐ | ☐ | Pending both passes. Communities exist: `owner-test-5428`, `test-community-541`. |
| `/communities` | ☑ | ☑ | Authless: 302 → login. Signed-in: 2 communities listed + create form. |
| `/login` | ☑ | ☑ | Renders sign-in form; andrew/Password1 login works, lands on `/` signed-in. |
| `/register` | ☑ | ☑ | Renders create-account form with validation hints. |
| `/admin/dashboard` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending (andrew may lack admin role — record outcome). |
| `/admin/users` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/admin/moderation` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |
| `/admin/instance` | ☐ | ☑ | Authless: 302 → login. Signed-in pass pending. |

**Resume checkpoint** (last completed page + state, updated at end of each slice): Authless pass complete for all 18 routes (all gating routes 302 → login, no data leaks, no console errors). Signed-in pass: `/`, `/home`, `/directory` (deep), `/communities` done; next = `/search` signed-in, then `/profile`, `/settings`, `/notifications`, `/compose`, `/object`, `/actor`, `/community` deep links, then the 4 admin routes. **Turn interrupted by Playwright MCP process death — next turn must re-establish the browser (clean entry, re-login as andrew) before continuing.**

**Data-state note** (instance data can drift between slices; record what exists at slice start so repros are interpretable): Actors: alice, andrew (display name "Andrew Luitink"), bob. andrew follows alice (directory shows Unfollow), not bob (Follow). Communities: `owner-test-5428` ("Testing community owner management."), `test-community-541` ("Created during the 54.1 regression pass."). Public timeline: 4 alice posts. andrew's home: own post "Neato misquito", 4 boosts (3 alice posts + 1 mastodon.world RayvenMX status), 3 RayvenMX posts (1 liked+boosted by andrew, 1 mentions @andrew "yoo"). No console errors observed on any pass so far.

## Findings

| ID | Sev | Class | Status | Slice | Page | Repro | Expected | Actual | Verify (`console-clean + <state> works`, + screenshot path if taken) |
|---|---|---|---|---|---|---|---|---|---|
| B-001 | S1 | bug | open | 62.3 | `/directory` | Sign in → Directory → People tab → click any actor's name (e.g. "alice"). | Navigates to that actor's detail page. | Click is a no-op: URL stays `/directory`, no network request. Every actor link's href is the literal string `/actor?iri=Actor.Id` (a placeholder that was never substituted with the actor's IRI). | — |
| B-002 | S2 | bug | open | 62.3 | `/directory` | Sign in → Directory → click the "Communities" tab. | Tab switches to list the instance's communities (2 exist: owner-test-5428, test-community-541). | Tab is inert: click fires zero network requests, `aria-selected` is never set on either tab, content stays on the People list. No error, no console message. | — |
| B-003 | S2 | bug | open | 62.3 | `/directory` | Sign in → Directory → People tab → inspect the card wrapper (a11y tree / screen reader). | Each card exposes a sensible accessible name (e.g. the actor's name). | Each card button's accessible name is `Show posts by System.Linq.Enumerable+RangeSelectIterator`2[System.Int32,System.String]` — an unrendered .NET type name is leaking into the DOM (the card's aria-label/title is bound to a `ToString()` of a LINQ iterator instead of the actor). | — |

## Phase 64 — request spam & network efficiency (draft topics)

> **Priority: reducing the *number* of calls the client makes, not raw latency tuning.** The goal is to stop redundant/duplicate requests fired when pages and controls load (e.g. the same feed fetched twice on mount, a refetch on every re-render, an N+1 fan-out where one batched call would do). Latency/size matters only secondarily.
>
> Notes go here as observed during 62.1. Use the fixed column set so 64's distillation is mechanical. **One row per *distinct* request pattern** — collapse repeated identical calls into one row with a `count` (the count *is* the spam signal). Tag the cause; `spam`/`dup`/`N+1` are the high-value tags.

**Cause tags (ordered by value for 64)**: `spam` (redundant call on load/re-render — fetch fires repeatedly or twice for the same data) · `dup` (identical request issued more than once with no new data) · `N+1` (per-item fan-out where one batched call would do) · `refetch-on-rerender` (state change re-triggers an already-fetched load) · `4xx/5xx` · `slow` (secondary) · `large-payload` (secondary) · `other`

| # | Method + path (pattern) | Status | Count (×) | Trigger (what load/control fires it) | Where (page) | Cause tag | Root-cause hypothesis |
|---|---|---|---|---|---|---|---|
| 1 | GET `/ap/v1/u/{actor}` | 200 | 5 | `/home` initial load — once per post card that shows andrew (own post + 3 own-boosts + session) | `/home` | N+1 / dup | Per-card actor fetch with no client-side actor cache; same actor (andrew) fetched 5× on one page load. A shared actor cache keyed by IRI would collapse this to 1. |
| 2 | POST `/ap/v1/proxy/https://mastodon.world/users/RayvenMX` | 200 | 4 | `/home` initial load — one per card referencing the remote actor RayvenMX (3 posts + 1 boost) | `/home` | N+1 / dup | Remote actor fetched via proxy per-card, never cached; same remote IRI proxied 4× in one load. |
| 3 | POST `/ap/v1/proxy/{remote-iri}/likes` | 200 | 2 | `/home` initial load — engagement state for the RayvenMX boosted status | `/home` | dup | Same remote likes-collection request issued twice with no new data (re-render or double-init of the engagement state loader). |
| 4 | GET `/ap/v1/u/{actor}/notes/{id}/likes` | 200 | 3 | `/home` initial load — one per alice post's card | `/home` | N+1 | Per-item likes-collection fan-out on load; 3 posts → 3 calls. Consider embedding like/share counts in the feed item (or a batch endpoint) instead of per-item fetches. |
| 5 | GET `/ap/v1/u/{actor}/notes/{id}/shares` | 200 | 3 | `/home` initial load — one per alice post's card | `/home` | N+1 | Same as #4 for shares. |
| 6 | GET `/ap/v1/public/feed?limit=20` | 200 | 2 | `/` (public timeline) initial load | `/` | dup | Same feed request fired twice on mount (likely a double component init / re-render). |

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
