# Iris — ActivityPub .NET Libraries

A set of .NET libraries for ActivityPub, designed to be embedded in existing apps and services.

**This file is the single live operating document.** It is the only thing an autonomous agent needs to read to know what's happening now and what's next; everything else in `docs/` is either a rarely-touched reference or a write-once archive. See [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) for the loop this file drives.

## Documentation

| File | Contents | Read cadence |
|---|---|---|
| **PLAN.md (this file)** | Now / Active Slice / Up Next / Inbox / Paused Questions / Recently Completed | every turn |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Append-only ledger of completed phases (one line each) | only to replenish "Up Next" or recall history |
| [docs/changes/](docs/changes/README.md) | One document per slice/change — the detailed build notes | write on completion; read rarely |
| [docs/decisions/](docs/decisions/README.md) | Substantial design decisions | write when a decision has real weight; read rarely |
| [docs/phase-notes/](docs/phase-notes/README.md) | Phase rationale and test-count notes | archival |
| [docs/plans/](docs/plans/) | Deep-dive scope docs for multi-turn workstreams (e.g. [phase-22-closeout.md](docs/plans/phase-22-closeout.md)) | read when picking up that workstream |
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns | reference |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details for Iris libraries | reference |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy | reference |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | Binding conventions and ActivityStreams rules | before every coding turn |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions and doc-maintenance rules | every turn |

## Short version

- Clean, focused abstractions; no framework lock-in beyond .NET.
- One client, two directions: a single `net10.0` client used by both client apps and server-to-server flows.
- Server capability added to ASP.NET Core via `IServiceCollection` and `IApplicationBuilder` extensions.
- ActivityStreams is provided by `KristofferStrube.ActivityStreams`; Iris adds identity, signing, validation, and IRI helpers on top.
- Actor-keyed auth allows a client to fetch an actor document, then sign requests using that actor's private key.
- Community-aware by default, with Group-like actors and unified feed/collection APIs.
- API access uses versioned routes under `/ap/v1/...` and `iris:`-namespaced capabilities.
- Integration-first test model with multi-instance `TestServer` harnesses, not a sprawling unit-test suite.

## Solution layout

```text
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/runtime integration for client apps
│   ├── Iris.Server/                net10.0 — ASP.NET Core endpoints, middleware, community feeds
│   ├── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
│   └── Iris.WebCrypto/             net10.0 — browser/WebCrypto signing support
├── tests/
│   ├── Iris.Testing/               shared multi-instance test harness
│   ├── Iris.Core.Tests/            ├── Iris.Client.Tests/            ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/          ├── Iris.LiveInterop.Tests/       ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
├── samples/
│   ├── SampleServer/               minimal ASP.NET Core host
│   └── SampleBlazorClient/         sample explorer using Iris.Client
└── tools/
    └── IrisSigner/
```

## Conventions

- Target framework: `net10.0` everywhere.
- `System.Text.Json` is the serialization surface; ActivityStreams/ActivityPub objects come from the third-party package model.
- Dependency flow: `Iris.Core` -> ActivityStreams + BCL; `Iris.Client` depends on `Iris.Core`; `Iris.Server` depends on `Iris.Core` + `Iris.Client` + ASP.NET Core.
- Caching is explicit: every read path that is cached exposes a `bypassCache` escape hatch.
- Versioned endpoints and `iris:` capability terms are authoritative.
- Testing is integration-first and browser-assisted where UI behavior matters.

## Test runs (fast vs. full)

- **Fast (default for the loop):** `dotnet test --filter "Category!=Slow"` — excludes the slow tests (those that wait out a real delivery backoff budget). Use this for the everyday "is it green?" check.
- **Full (source of truth):** `dotnet test` — every test including the slow ones. Use for the final green check before a phase closes.
- To mark a test slow: `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`). Only mark tests that wait on real wall-clock time. Details + honest payoff note: [docs/reference/TESTING.md §Running the suite: fast vs. full](docs/reference/TESTING.md#running-the-suite-fast-vs-full).

## Now

**Phase 44 — Post content completeness (COMPLETE).** 44.1–44.3 all COMPLETE. *(Phases 32–44 complete — one-line ledger per phase in [docs/ROADMAP.md](docs/ROADMAP.md).)*

**Phase 45 — WASM manual test & bug hunt (ACTIVE).** The app just transitioned from SSR (Blazor Server) to a Blazor WebAssembly client; the port is expected to have left implementation holes. Phases 45–46 focus on **manual testing via MCP Playwright** (test accounts + test content created by hand), **triaging defects into this file and fixing them**, and **visual inspection with design decisions** built on what exists. Scope + hunting map + per-slice definitions: [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md).

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

**45.4: Social graph pass** (next up). See [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md) for scope.

### Loop protocol (WASM manual-test phase)

Each slice is a **Playwright-driven pass**, not a code-first slice. Per slice:

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build --no-cache iris-web && docker compose up -d iris-web`
3. **Manual test (MCP Playwright)**: create/use test accounts (`alice`/`alice-password` seeded; register more as the slice needs — `bob`, `carol`, `dave`), create test content (posts, replies, follows, communities, media, CW), exercise the slice's scope (see [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md)). Capture **console errors** (`browser_console_messages`) and **screenshots of every screen** (inline screenshot no files) visited - use public fqdn address "https://iris.luit.ink".
4. **Triage**: log every defect (page, repro, expected vs actual, severity) in the slice's change doc; any defect not fixed this slice becomes a numbered **Up Next** item.
5. **Fix in scope**: implement fixes for the defects assigned to this slice; re-verify each fix live.
6. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests.
7. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (defects first).

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. Defects triaged from test passes are prepended here (highest severity first).

**Phase 45 — WASM manual test & bug hunt** (scope: [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md)):

1. **45.4: Social graph pass** — follow/unfollow cross-page, follow-request queue, community join/leave + membership requests, home timeline reflects follows, profile tabs.
2. **45.5: Notifications & moderation pass** — notifications list + unread badge + mark-all-read; block/mute/report + undo (posts + actor detail); admin user list.
3. **45.6: Edge states pass** — empty/loading/error states, deep links + refresh on every route, responsive 375px/1024px, console errors on every page.
4. **45.7: Triage closeout** — review all defects from 45.1–45.6; unfixed → Up Next; change doc summarizing findings + fixes.

**Phase 46 — Visual inspection & design pass** (after 45):

5. **46.1: Design audit** — screenshot all pages (signed-in/out, empty/populated, 1280×800 + 375×812); prioritized design-decision list.
6. **46.2+: Design fixes** — implement audit decisions, one coherent area per slice (card system, nav/header, forms, object detail, mobile); before/after screenshots.

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 45.3: **Media & CW pass** (Phase 45) — Playwright pass over image attachment (Note + Article), CW reveal toggle, same-origin media IRI, feed + object detail rendering. Defects found + fixed: (a) media upload required Basic auth which the WASM client cannot carry; added cookie-auth fallback to `LocalMediaUploadHandler` (same pattern as inbox/actor document). (b) `UploadMediaAsync` added to `IActorSessionAccessor` for cookie-auth multipart POST via `_sameOriginHttp`. (c) `ObjectView.RewriteMediaToSameOrigin` rewrites FQDN media IRIs to same-origin paths so the browser's `<img>` loads them same-origin (no CORS, no mixed-content). (d) `Compose.razor` `UploadAttachmentAsync` uses `Session.UploadMediaAsync` instead of `Session.MediaClient.UploadAsync`. Verified: Note with image + CW (202, image renders 64×64 same-origin, CW reveal toggle works), Article with image (202, image renders same-origin), oversized-file rejection (server 413 at 10 MiB cap). 951 tests green.
  - 45.2: **Content round-trips pass** (Phase 45) — Playwright pass over post/edit/delete/like/boost with Unicode/emoji/HTML content. Defects found + fixed: (a) systemic JSON casing mismatch — WASM client's `GetFromJsonAsync`/`ReadFromJsonAsync` used case-sensitive property binding while the server emits camelCase → login/register 400 + zero notification badge; fixed 5 deserialization sites to `PropertyNameCaseInsensitive = true`. (b) CORS — `SameOriginApHandler` rewrote FQDN IRIs to same-origin but was innermost (after signing); moved it outermost so the URL is rewritten BEFORE signing (signature host matches the browser's dial host). (c) `appsettings.json` added to the WASM client (`Iris:AdvertiseBase=https://iris.luit.ink`) so the handler knows the FQDN to rewrite. (d) ObjectDetail `DeleteAsync` used the Create IRI (not the note IRI) → delete was a no-op; fixed to use the `SubjectObject`'s IRI. Verified: post (202, renders on profile with Unicode/emoji/escaped HTML), like (`/likes` totalItems=1, heart lights), boost (`/shares` totalItems=1, boost lights), edit (content updated in DB + UI, like/boost counts persist), delete (note tombstoned with `formerType: Note`). 951 tests green.
  - 45.1: **Auth & session pass** (Phase 45) — Playwright pass over login/register/logout/session. Defects found + fixed: (a) WASM client's home timeline blank — `index.html` was missing the `<script src="js/WebCrypto.js">` bridge tag (the signing key never loaded → `Session.Client` null); added `wwwroot/js/WebCrypto.js` + the script tag. (b) 10 pages (Profile, Settings, Notifications, Directory, Search, Compose, Communities, CommunityDetail, ActorDetail, ObjectDetail) read `Session.*` synchronously but never called `EnsureReadyAsync()` → stuck on loading; added `await Session.EnsureReadyAsync()` to each page's init lifecycle. (c) Inbox 403 — `InboxEndpointHandler` only checked Basic auth; added the same cookie-auth `actor_iri` fallback as `ActorDocumentHandler` so the signed WASM client can read its own inbox (Notifications). Verified: register bob/carol/dave, duplicate-handle + short-password + invalid-handle errors, bad-password login error, logout ends session, session persists across reload, antiforgery 400 on login POST w/o token, session endpoint 302 unauth. 951 tests green.
  - 44.3: **Media attachment (image) on compose (F-27)** (Phase 44) — "Add image" picker on Compose (top-level posts); on post uploads via `IMediaClient` → same-origin media IRI; `ComposeNote.Build`/Article path carries a single `Image` attachment; feed's `GetMediaAttachments` already renders it. 5 unit + 4 integration tests.
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
