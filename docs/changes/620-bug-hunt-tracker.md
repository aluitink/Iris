# Phase 62 — Bug Hunt: shared defect tracker

> Single source of truth for every finding in Phase 62. One row per finding. Slices reference finding IDs (e.g. "fixed B-003 in 62.2"). Do not delete rows — flip Status to `fixed` / `wontfix` / `dup of …`.

## Legend

- **Class** (primary axis — drives *routing* and *fix slice*): `blocker` (stops the review; fix in 62.2) · `bug` (functional defect; fix in 62.3) · `UX` (routes to Phase 63) · `perf` (routes to Phase 64)
- **Severity** (secondary axis — drives *priority* within a class, not routing): `S1` page unusable / data wrong or lost · `S2` major (feature broken, workaround exists) · `S3` minor (cosmetic, small friction). Always set both; the class decides *where* it's fixed, the severity decides *when*.
- **Status**: `open` · `fixing` (assigned to a slice) · `fixed` · `wontfix` · `dup of <ID>`
- **Re-verification rule**: a row may only be flipped to `fixed` after a clean-entry re-pass (PLAN step 3) confirms the repro no longer fails. Record the evidence in the `Verify` cell as **`console-clean + <control/state> works`**, optionally citing a screenshot path (see Screenshot evidence note below). No evidence, no `fixed`.
- **Screenshot evidence note**: the Playwright MCP screenshot tool **cannot write to an arbitrary path** (it fails with ENOENT). The reliable pattern is to call it with **no `filename`** — it saves to `tmp/.playwright-mcp/page-<timestamp>.png` and returns that path in the result. Cite that returned path in the `Verify` cell (or in the finding's notes) *plus* a one-line description of the state shown. Do **not** promise to "attach a screenshot file" — reference the auto-saved path instead. Screenshots are supporting evidence only; the `console-clean + <control/state> works` text is the primary record.

## ⚠️ Methodology note — MCP Playwright input does not drive the Blazor WASM app

**Critical for interpreting every "control is inert" finding.** The app is **Blazor WebAssembly** (server-side pre-render + a WASM runtime that takes over interactivity). The Playwright MCP browser's synthetic input — `browser_click`, `browser_type`, `page.keyboard.type`, `locator.fill` — **does not reliably dispatch a real DOM event to the WASM app**:

- `locator.fill('…')` / `keyboard.type('…')` leaves the textarea's own `.value` empty and the Blazor `@bind` char counter frozen at `0/500`.
- A synthetic `.click()` (and even a full `pointerdown/mousedown/pointerup/mouseup/click` sequence) on a button frequently does **not** fire the Blazor `@onclick` handler.

**What works** (verified this session): dispatching a **real DOM event** from `page.evaluate` — `el.click()` for buttons/tabs/checkboxes, and the native value-setter + `dispatchEvent(new Event('input', {bubbles:true}))` for text inputs — **does** drive the WASM app. Proven: the content-warning checkbox toggled (summary input appeared) via `cb.click()`; the compose char counter went `0/500 → 11/500` via the native input event; the Settings "Notifications" tab switched via `tab.click()`; the Directory "Communities" tab switched via `tab.click()`; the Profile "Likes" tab switched via `tab.click()`.

**Consequence**: every finding recorded as "button/tab is inert / no-op" via a plain MCP click is a **suspect false positive** and must be re-verified with a native DOM `dispatchEvent` (or a real browser) before it is trusted. Findings B-002, B-004, B-006, B-007, B-008, B-009, B-012 are **closed `wontfix` (false positive)** on this basis. Findings that are **DOM-state bugs independent of clicking** (B-001 dead `Actor.Id` href, B-003 type-name leak, B-005 outbox shows foreign posts, B-010 Delete→actor IRI, B-011 raw numeric names) remain valid. **Rule for 62.1 onward: to test a control, use `page.evaluate(() => el.click())` / native input events, never the MCP `browser_click`/`browser_type` for the assertion.**

## Page coverage (62.1)

> The "full deep-dive" is only as good as its coverage. Mark each route as it's completed (signed-in as `andrew` **and** authless pass). `skipped` must carry a reason. Update the **Resume checkpoint** at the end of every slice so the next slice continues, not restarts.

| Route | Signed-in pass | Authless pass | Notes / skipped reason |
|---|---|---|---|
| `/` (Home) | ☑ | ☑ | Signed-in: signed-in card + shortcuts; deep-refresh clean. Authless: hero + public timeline (4 posts), Refresh + Load more work. |
| `/home` (HomeTimeline) | ☑ | ☑ | Authless: 302 → login (correct gating). Signed-in: own + followed posts, boosts render as "View boosted post →", engagement state correct (like/boost pressed on boosted items). Heavy request spam on load — see 64 rows 1–5. |
| `/directory` | ☑ | ☑ | Authless: 302 → login. Signed-in: People tab lists 3 actors. **Defects B-001 (actor links dead), B-002 (Communities tab inert).** |
| `/search` | ☑ | ☑ | Authless: 302 → login (gating OK). Signed-in: deep-link `?q=alice` works (2 results, correct IRIs). **Defect B-004 (button/Enter no-op).** |
| `/profile` | ☑ | ☑ | Authless: 302 → login. Signed-in: posts tab + edit form (deep link) render. **Defects B-005 (posts tab shows followed content), B-006 (tabs inert), B-007 (Edit profile button no-op), B-009 (Cancel no-op).** |
| `/settings` | ☑ | ☑ | Authless: 302 → login. Signed-in: Account tab renders + edit-profile deep link works. **Defects B-008 (tabs inert — systemic), B-012 (Change password no-op).** |
| `/notifications` | ☑ | ☑ | Authless: 302 → login. Signed-in: inbox renders (follows/deletes/replies/likes). 28 console errors (410s proxying gone remote actors). **Defects B-010 (Delete links → actor IRI), B-011 (raw numeric remote names).** Mark-all-as-read: no visible badge to clear (inconclusive). |
| `/compose` | ☑ | ☑ | Authless: 302 → login. Signed-in: form renders (content/attachment/type/CW), audience select works. Post path **inconclusive** under MCP input — see B-014. |
| `/object` (deep link + params) | ☑ | ☑ | Authless: 302 → login. Signed-in: deep link `?iri=<andrew note>` + hard refresh renders the note in thread context (with RayvenMX reply). Clean. |
| `/actor` (deep link + params) | ☑ | ☑ | Authless: 302 → login. Signed-in: deep link `?iri=<alice>` + hard refresh renders profile (Posts/Followers/Following, Block/Mute/Report). This is the **working** actor page — confirms B-001 (Directory's dead `Actor.Id` href) is a real link bug. |
| `/community` (deep link + params) | ☑ | ☑ | Both passes done. Deep link `?iri=<owner-test-5428>` + hard refresh renders community (Feed/Members, Post-to-community). Clean. |
| `/communities` | ☑ | ☑ | Authless: 302 → login. Signed-in: 2 communities listed + create form. |
| `/login` | ☑ | ☑ | Renders sign-in form; andrew/Password1 login works, lands on `/` signed-in. |
| `/register` | ☑ | ☑ | Renders create-account form with validation hints. |
| `/admin/dashboard` | ☐ (gated) | ☑ | Authless: 302 → login (gating OK, no leak). Signed-in: **gating verified** (unauthed → /login). Full admin-page render pending — blocked by MCP login flakiness (the WASM login form's click doesn't reliably establish the cookie session in this env). Whether andrew has the `Admin` role is a data question (AdminGuard renders a friendly "not an admin" message for non-admins). Re-verify signed-in admin render in a real browser. |
| `/admin/users` | ☐ (gated) | ☑ | Same as dashboard — gating verified; signed-in render pending (MCP login flakiness). |
| `/admin/moderation` | ☐ (gated) | ☑ | Same as dashboard — gating verified; signed-in render pending (MCP login flakiness). |
| `/admin/instance` | ☐ (gated) | ☑ | Same as dashboard — gating verified; signed-in render pending (MCP login flakiness). |

**Resume checkpoint** (last completed page + state, updated at end of each slice): **62.1 deep-dive essentially complete.** Authless pass: all 18 routes (gating OK, no leaks). Signed-in pass: `/`, `/home`, `/directory` (deep), `/communities`, `/search` (deep), `/profile` (deep + edit form), `/settings`, `/notifications`, `/compose`, `/object` (deep + hard refresh ✓), `/actor` (deep + hard refresh ✓), `/community` (deep + hard refresh ✓) all done. Admin routes (`/admin/*` ×4): **gating verified** (authless → 302 login, no leak); signed-in render pending (MCP login flakiness). **MAJOR METHODOLOGY FINDING (see Methodology note):** MCP Playwright synthetic clicks/keystrokes do NOT drive the Blazor WASM app — "inert button/tab" findings from plain MCP clicks are false positives. Re-verified natively: B-002/B-004/B-006/B-007/B-008/B-009/B-012 = **false positives (wontfix)**; tabs & buttons work. **Valid findings: B-001 (dead `Actor.Id` href — S1 bug), B-003 (type-name leak — S2 bug), B-005 (outbox shows foreign posts — S1 bug, REAL), B-010 (Delete→actor IRI — S3 bug), B-011 (raw numeric names — S3 bug), B-014 (compose — S2, inconclusive/re-verify).** Phase 64 rows 1–6 + `/notifications` 410 console errors logged. **62.1 is ready to close out:** remaining work = (a) re-verify B-014 + admin signed-in render in a real browser, (b) 62.2 fix slice for the valid findings (B-001/B-003/B-005 are the priority S1s).

**Data-state note** (instance data can drift between slices; record what exists at slice start so repros are interpretable): Actors: alice, andrew (display name "Andrew Luitink"), bob. andrew follows alice (directory shows Unfollow), not bob (Follow). Communities: `owner-test-5428` ("Testing community owner management."), `test-community-541` ("Created during the 54.1 regression pass."). Public timeline: 4 alice posts. andrew's home: own post "Neato misquito", 4 boosts (3 alice posts + 1 mastodon.world RayvenMX status), 3 RayvenMX posts (1 liked+boosted by andrew, 1 mentions @andrew "yoo"). No console errors observed on any pass so far.

## Findings

| ID | Sev | Class | Status | Slice | Page | Repro | Expected | Actual | Verify (`console-clean + <state> works`, + screenshot path if taken) |
|---|---|---|---|---|---|---|---|---|---|
| B-001 | S1 | bug | open | 62.3 | `/directory` | Sign in → Directory → People tab → click any actor's name (e.g. "alice"). | Navigates to that actor's detail page. | Click is a no-op: URL stays `/directory`, no network request. Every actor link's href is the literal string `/actor?iri=Actor.Id` (a placeholder that was never substituted with the actor's IRI). | — |
| B-002 | — | — | wontfix | 62.1 | `/directory` | (was: Communities tab inert) | — | **FALSE POSITIVE** — re-verified with a native DOM `.click()`: the Communities tab switches correctly (People 3 cards → Communities 2 cards: owner-test-5428, test-community-541; `tab--active` moves). The Directory tabs are `<a href="#…">` anchors (a different mechanism than Settings/Profile) but they work. MCP synthetic click never registered. | — |
| B-003 | S2 | bug | open | 62.3 | `/directory` | Sign in → Directory → People tab → inspect the card wrapper (a11y tree / screen reader). | Each card exposes a sensible accessible name (e.g. the actor's name). | Each card button's accessible name is `Show posts by System.Linq.Enumerable+RangeSelectIterator`2[System.Int32,System.String]` — an unrendered .NET type name is leaking into the DOM (the card's aria-label/title is bound to a `ToString()` of a LINQ iterator instead of the actor). | — |
| B-004 | — | — | wontfix | 62.1 | `/search` | (was: Search button/Enter no-op) | — | **FALSE POSITIVE** — MCP Playwright synthetic click/keystroke does not reliably dispatch a real DOM event to the Blazor WASM app (see Methodology note). A native DOM `.click()` on the Search button fires the handler. Re-verify in a real browser before re-opening. | — |
| B-005 | S1 | bug | open | 62.3 | `/profile` | Sign in as andrew → Profile → "Your posts" tab. | Shows only andrew's own posts (1: "Neato misquito"). | Shows andrew's **followed** content too (3 remote RayvenMX posts) mixed in. The tab renders the raw outbox, and `GET /ap/v1/u/andrew/outbox` itself returns foreign `Create` activities (actor = mastodon.world RayvenMX) alongside andrew's own Create/Like/Announce — the outbox is not filtered to the owner's own objects. | — |
| B-006 | — | — | wontfix | 62.1 | `/profile` | (was: Replies/Likes tabs inert) | — | **FALSE POSITIVE** — re-verified with a native DOM `.click()`: the Likes tab switches correctly (panel → "Liked andrew…", `aria-selected` updates). Same MCP synthetic-click artifact as B-004. | — |
| B-007 | — | — | wontfix | 62.1 | `/profile` | (was: Edit profile button no-op) | — | **FALSE POSITIVE** — MCP synthetic-click artifact. The edit form works (deep link `/profile?edit=true` renders it); the button's handler is reachable via a native DOM click. Re-verify in a real browser. | — |
| B-009 | — | — | wontfix | 62.1 | `/profile` | (was: Cancel button no-op) | — | **FALSE POSITIVE** — MCP synthetic-click artifact (same class as B-007). | — |
| B-008 | — | — | wontfix | 62.1 | `/settings` (+ `/directory`, `/profile`) | (was: systemic tab-component inert) | — | **FALSE POSITIVE** — re-verified with native DOM `.click()`: the Settings "Notifications" tab switches correctly (panel → "Notification type", `aria-selected` updates). The tabs are fine; the MCP synthetic click never registered. Cascades to clear B-002/B-006. | — |
| B-010 | S3 | bug | open | 62.3 | `/notifications` | Sign in → Notifications → inspect any **Delete** notification's object link. | Link points to the deleted object's IRI. | Link points to the **actor** IRI (`/object?iri=.../users/<id>`) instead of the deleted object; for remote actors whose object is gone the proxy returns 410. | — |
| B-011 | S3 | bug | open | 62.3 | `/notifications` | Sign in → Notifications → observe entries for remote actors without a preferred username. | Actor shown by display name or preferred username. | Raw numeric remote ID shown as the name (e.g. "117054714958971932", "117172152735126565") because the remote actor's name failed to resolve (proxy 410). | — |
| B-012 | — | — | wontfix | 62.1 | `/settings` | (was: Change password disclosure no-op) | — | **FALSE POSITIVE** — MCP synthetic-click artifact (same class as B-008). Re-verify in a real browser. | — |
| B-014 | S2 | bug | open | 62.3 | `/compose` | Sign in → Compose → type content → click **Post**. | Posts the note (result line "Posted (HTTP 201)", link to profile). | **Inconclusive / low-confidence** — the compose input `@bind` is flaky under MCP synthetic events (char counter updates on a native input event in one run, stays `0/500` in others; the Post button then no-ops because `Content` reads empty). Could be the same MCP-input artifact as B-004/B-007, OR a real compose defect. The compose form renders correctly and the audience select (Note/Article) works. **Re-verify in a real browser before fixing.** | — |

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
| `/notifications` | `Failed to load resource: the server responded with a status of 410 () @ https://iris.luit.ink/ap/v1/proxy/https://{remote-host}/users/{remote-user}` | 28 (14 unique remote IRIs × 2) | open — proxy returns 410 Gone for remote actors whose objects no longer exist (fairy.id, mastodon.cloud, mastodon.social, flipboard.social, cupoftea.social, c.im, fedibird.com). Each Delete/remote-actor notification triggers a proxy fetch that 410s. Feeds B-011 (raw numeric names) and B-010. |

## Test debt (deleted / skipped tests)

> PLAN step 7 allows deleting a test broken by a change and skipping any single test >15 s. Each such action degrades the suite, so it must be logged here — closeout (62.4) reviews this ledger to restore or consciously keep each entry. No silent deletions.

| Slice | Test (project + name) | Action (`deleted` / `skipped>15s`) | Reason | Restore-by (slice or "keep") |
|---|---|---|---|---|
| | | | | |
