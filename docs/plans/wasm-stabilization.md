# WASM stabilization — manual test & bug hunt (Phases 45–46)

> Forward-looking scope for the post-SSR→WASM transition. The app just moved from Blazor Server (SSR/interactive) to a Blazor WebAssembly client (`Iris.Web.Client`) served by the pure-API server (`Iris.Web`). That transition is expected to have left **holes in the implementation** — the UI now talks to the server over real HTTP with cookie auth, and every screen was ported rather than re-verified. This workstream hunts those holes down.

**Policy (user-directed, binding for these phases):**

- **Manual testing only, via MCP Playwright.** No new coded tests of any kind.
- **Test accounts + test content are created manually** during the hunt (register new users, post notes/articles/replies/boosts/likes, follow, create communities, etc.).
- **Issues found are triaged into PLAN.md** (Up Next / Inbox) as fix slices, then fixed.
- **Visual inspection is a first-class step**: every page gets screenshotted; design decisions are made and built on, not just bugs fixed.
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable.** Keep what passes. If a change breaks one, **delete the test** — do not fix the app to satisfy it, and do not replace it with a new test.
- **15-second rule:** any single test taking longer than 15 s is **skipped**. If the suite stalls on timing-out tests, use **blame** (e.g. `dotnet test -v n` per-test timing, or the VSTest console output) to find the offenders and **comment them out**.

## What the transition changed (hunting map)

The WASM port replaced in-circuit behavior with cross-request HTTP behavior. Known risk seams:

1. **Auth**: WASM cannot set cookies — login/register/logout are plain `POST /login` / `POST /register` / `GET /logout` round-trips; the session is resolved via `GET /local/v1/session` + a cookie. Watch for: session lost after reload, auth state stale after logout, antiforgery token fetch (`/local/v1/antiforgery`) failing, login loop.
2. **Session accessor port**: `IActorSessionAccessor` was rewritten for the client (fetch-based, not in-process DI). Watch for: null actor, wrong actor after switching accounts, stale data across navigation.
3. **Notifications**: `NotificationService` ported to the client; the unread badge polls `GET /local/v1/notifications/unread-count` (60 s). Watch for: badge stuck, mark-all-read not updating, wrong unread count after a new notification arrives.
4. **Data round-trips**: compose (note/article/media/CW/reply/edit/delete), follow/unfollow (cross-page), join/leave community, moderation (block/mute/report + undo), settings/profile edit — every write path is now a signed outbox POST or a local endpoint; verify the write lands AND the read-back renders.
5. **Rendering**: content types (Note/Article/replies/announcements/CW-sensitives/media), HTML content (pre-rendered vs encoded), timestamps, empty states, loading states, error states (network failure, 401, 404 object).
6. **SPA fallback**: `MapFallbackToFile("index.html")` must not shadow API routes; deep links (refresh on `/object?iri=…`) must load the client and restore state.
7. **Static assets**: `_framework/` is git-ignored and rebuilt into `wwwroot/` at build time — verify the served client is the one just built (stale wasm = confusing bugs).

## Phase 45 — Manual test & bug hunt

Each slice = one Playwright-driven pass. Per pass: rebuild the app (`dotnet build` + docker rebuild), log in (or register) as the designated account, exercise the scope, **screenshot every screen**, log every defect, triage each defect into a fix slice (PLAN.md), fix what is in scope, re-verify live, commit.

| Slice | Scope |
|---|---|
| 45.1 | **Auth & session**: register new accounts (≥3: e.g. `bob`, `carol`, `dave`), log in/out, refresh on signed-in pages, session persistence across reload, logout state, login error paths (bad password, rate limit), antiforgery on the login/register forms. |
| 45.2 | **Content round-trips**: as each account — post Note, post Article, reply (context preview + `inReplyTo`), edit own post, delete own post, boost, like; verify each renders on home/profile/object detail and the counters update. Create test content with HTML, long text, Unicode/emoji. |
| 45.3 | **Media & CW**: attach an image to a post (top-level Note/Article), sensitive/CW post (reveal toggle), media IRI same-origin, image renders in feed + object detail; reject oversized file (1 MiB cap) gracefully. |
| 45.4 | **Social graph**: follow/unfollow from actor detail + directory (cross-page round-trip), follow-request queue (manuallyApprovesFollowers), join/leave community + membership request (manuallyApprovesMembers), home timeline reflects follows, profile tabs (posts/replies/likes). |
| 45.5 | **Notifications & moderation**: notifications list renders inbox (follows/likes/replies/mentions/boosts), unread badge + mark-all-read, moderation (block/mute/report + undo on posts and actor detail), admin user list (admin account). |
| 45.6 | **Edge states**: empty states (fresh account, no posts/followers), loading states, error states (delete a viewed object, visit a 404 IRI, network down), deep links + refresh on every route, responsive (375px / 1024px), console errors on every page (Playwright console capture). |
| 45.7 | **Triage closeout**: review the defect list from 45.1–45.6; anything not fixed becomes an Up Next slice; write the change doc summarizing findings + fixes. |

**Defect triage rule:** every defect found gets a one-line entry (page, repro steps, expected vs actual, severity) in the slice's change doc AND, if not fixed in that slice, a numbered item in PLAN.md Up Next.

## Phase 46 — Visual inspection & design pass

Per page (landing, home, compose, profile, directory, communities, community detail, notifications, search, actor detail, object detail, settings, admin, login/register): screenshot at 1280×800 and 375×812; compare against a polished social platform; make the design decisions; implement.

| Slice | Scope |
|---|---|
| 46.1 | **Design audit**: screenshot all pages in both light states (signed-in/signed-out, empty/populated); produce a prioritized design-decision list (spacing, typography, color, hierarchy, empty states, iconography, motion). |
| 46.2+ | **Design fixes**: implement the audit's decisions, one coherent area per slice (e.g. "card system", "nav + header", "forms", "object detail", "mobile layout"). Re-screenshot before/after; re-verify no regression in functionality (Playwright smoke of the touched page). |

## Definition of done (per slice, these phases)

- Build clean (`dotnet build`, `TreatWarningsAsErrors` on).
- **Docker rebuild + live Playwright verification** of the slice's scope (this replaces "new tests" as the evidence of done).
- Existing web tests: green, or the broken ones **deleted** (logged in the change doc).
- Any test >15 s: **skipped** (logged in the change doc).
- Screenshots saved for the change doc; defects triaged into PLAN.md.
- XML doc comments / style rules per CODING_STYLE.md for any code touched.

## Test hygiene commands

```bash
# Run the web tests, see per-test timings (find >15 s offenders)
cd /workspace && dotnet test tests/Iris.Web.Tests -c Release -v n --logger "console;verbosity=detailed"

# Blame: which test is slow (from the detailed output: "Passed X (Ys)")
# then either [Fact(Skip = "slow >15s — <date>")] or comment the method out
```
