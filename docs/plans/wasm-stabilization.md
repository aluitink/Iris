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

### 45.0 — docker-compose re-evaluation: single inbound port for API + UI

The compose stack previously ran a single Blazor-Server app (circuit + API in one process). The transition split it: `Iris.Web` is now a **pure API host** that also serves the WASM client's static files, and `Iris.Web.Client` runs in the browser. There is **one external URL** (`https://iris.luit.ink`) forwarded by the reverse proxy to **host port 8088** — so UI requests and API requests must share that single inbound port. Re-evaluate and lock down the routing solution.

**Current design (one Kestrel, one port, path-based routing):** a single `iris-web` container binds 8080 (published on host 8088). One ASP.NET Core pipeline serves both:

- `UseStaticFiles` → the WASM client's `_framework/` + `wwwroot/` (copied into the server's `wwwroot` at build time).
- `MapAuthEndpoints` / `MapNotificationEndpoints` / `MapSessionEndpoints` / `MapAdminEndpoints` → `/register`, `/login`, `/logout`, `/local/v1/...`.
- `MapActivityPubEndpoints` → `/.well-known/webfinger`, `/ap/v1/...`.
- `MapFallbackToFile("index.html")` (mapped **last**) → any other path serves the SPA shell; the WASM client-side router then handles `/home`, `/compose`, `/profile`, deep links, etc.

The WASM client calls the API **same-origin** (`HttpClient.BaseAddress = HostEnvironment.BaseAddress` — the page's own origin), so no separate UI origin, no CORS, no cross-port calls, and cookie auth works by construction (the `iris.auth` cookie is scoped to the single origin).

**45.0 verifies and hardens exactly this.** Scope:

1. **Confirm the single-port split is clean on the live Docker app (Playwright + raw HTTP):**
   - UI: `/` (signed-out landing), `/login`, `/register`, deep links (`/object?iri=…`, `/home`), and a refresh on each → the SPA shell (`index.html`) loads and the client router restores state.
   - Static: `/index.html`, `/favicon.svg`, `/_framework/blazor.webassembly.js` (+ one wasm + `dotnet.js`) → 200 with correct `Content-Type`; confirm the served `dotnet.js` carries the build-time patches (`debugLevel: 0`, `globalizationMode: invariant`) — i.e. the served client is the one just built, not a stale copy.
   - API: `/.well-known/webfinger?resource=acct:alice@…`, `GET /ap/v1/u/alice`, `GET /ap/v1/u/alice/outbox`, `GET /ap/v1/health`, `GET /local/v1/session` (401 signed-out / 200 signed-in), `GET /ns` → correct JSON, not the SPA fallback.
   - **No shadowing:** assert the SPA fallback does NOT swallow an API/static route (every route above returns its real payload, not `index.html`), and that an unknown non-API path (e.g. `/definitely-not-a-route`) DOES fall back to `index.html` (200, the SPA shell).
2. **Validate the reverse-proxy contract for the single origin:** the proxy forwards `https://iris.luit.ink` → host 8088 with **no path rewrite** (path prefix is the router: `/ap/v1/*` + `/local/v1/*` + `/.well-known/*` + `/_framework/*` + `/index.html` + `/favicon.svg` = backend; everything else = SPA). Confirm `UseForwardedHeaders` makes the app see scheme `https` + host `iris.luit.ink` (auth cookie `Secure` flag, advertised IRIs, redirects all correct under the proxy). Document the exact Caddy/nginx config in `docs/plans/production-app-deployment-env-reference.md` (WebSocket upgrade for `/_blazor` no longer applies — WASM has no SignalR circuit; only static + JSON + media flows).
3. **Cookie + same-origin auth under the single origin:** register/login set `iris.auth` on `iris.luit.ink`; WASM `GET /local/v1/session` reads it; logout clears it; a hard refresh stays signed in; no CORS preflights appear in the network tab (same-origin). Antiforgery token fetch (`/local/v1/antiforgery`) + the login/register form POSTs succeed through the proxy.
4. **Media + body cap on the shared port:** media upload (`POST` media) + serving the media IRI works on the same origin; an oversized body (e.g. 1.5 MiB) is rejected 413 by Kestrel before the pipeline (the 1 MiB cap) — confirm the cap applies to the shared inbound port.
5. **Decision (record in the change doc):** keep **one container / one Kestrel / one port** (the status quo) — do NOT introduce a second UI container or a UI origin. Rationale: the WASM client is static files; same-origin cookie auth + no CORS + a single reverse-proxy target is strictly simpler and is what the single inbound URL requires. The only change if anything is found: ensure the `BuildAndCopyClient` output is always fresh (no stale `_framework`) and the route map order is provably correct.

**Definition of done:** the live Docker app (behind the real FQDN where possible, else `http://localhost:8088` with `Iris__AdvertiseBase` set) passes every check in 1–4 with Playwright screenshots + raw-HTTP evidence; the proxy config + the keep-one-port decision are written up in the change doc; the route-order / static-freshness findings (if any) are fixed and re-verified.

**Resolved during scoping (no open action):**

- *Redundant apps:* settled by inspection — there is exactly **one Kestrel** (`Iris.Web`), serving API + static WASM on one port; `Iris.Web.Client` is browser-only (built + copied into the server's `wwwroot` by the `BuildAndCopyClient` target), not a separate app or container. The SSR-era dead files (`apps/Iris.Web/Components/**`, `apps/Iris.Web/Ui/**`, and the leftover `apps/Iris.Web/Accounts/IActorSessionAccessor.cs`, which was unused on the server and broke the build) were deleted, and `Iris.Web.Client` was added to `Iris.slnx`. The samples (`SampleServer`, `SampleBlazorClient`, `IrisStaticHost`) were left in place per the operator's direction (only the dead SSR files were removed).
- *Stale-container SSR confusion (root-caused):* the live `iris-web` container was serving the **old Blazor Server (SSR) app** — its `wwwroot/_framework/` held `blazor.server.js` and the page ran over the `/_blazor` SignalR circuit, so the network stream showed a single base64 "blazor channel" instead of direct AP HTTP calls. A stale image silently serves the wrong UI. A clean `docker compose build --no-cache` + `up -d` fixed it: the container now serves `blazor.webassembly.js`, `dotnet.js` carries the build-time patches (`debugLevel: 0`, `globalizationMode: invariant`), and the browser makes real AP requests (`/local/v1/session`, `/ap/v1/u/{actor}/outbox`, signed outbox POSTs, media uploads). **This is why 45.0 verifies the served `_framework` is the just-built WASM output** — a rebuild must be the first step of every test pass, and a pass should assert `blazor.webassembly.js` is present (not `blazor.server.js`).

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
