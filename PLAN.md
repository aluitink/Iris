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

**Phase 54 — Post-1.0 polish & hardening (IN PROGRESS).** First slice 54.1 (full manual Playwright regression pass) is Active.

*(Phases 32–53 complete — one-line ledger per phase in [docs/ROADMAP.md](docs/ROADMAP.md).)*

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

**54.1: Full manual Playwright regression pass** — Phase 54 begins. A post-1.0.0 regression sweep over all pages (signed-in + signed-out, 1280×800 + 375×812): capture every page, log defects (page, repro, expected vs actual, severity), and fix in-slice any defect found. Manual verification only (no new coded tests) per the binding WASM manual-test policy.

### Loop protocol (WASM manual-test phase)

Each slice is a **Playwright-driven pass**, not a code-first slice. Per slice:

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first).
3. **Manual test (MCP Playwright)**: create/use test accounts (`alice`/`alice-password` seeded; register more as the slice needs — `bob`, `carol`, `dave`), create test content (posts, replies, follows, communities, media, CW), exercise the slice's scope (see [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md)). Capture **console errors** (`browser_console_messages`) and **screenshots of every screen** (inline screenshot no files) visited - use public fqdn address "https://iris.luit.ink".
4. **Triage**: log every defect (page, repro, expected vs actual, severity) in the slice's change doc; any defect not fixed this slice becomes a numbered **Up Next** item.
5. **Fix in scope**: implement fixes for the defects assigned to this slice; re-verify each fix live.
6. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests.
7. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (defects first).

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. Defects triaged from test passes are prepended here (highest severity first).

**Phase 54 — Post-1.0 polish & hardening (in progress):**

1. **54.1: Full manual Playwright regression pass** — ACTIVE. Post-1.0.0 regression sweep over all pages (signed-in + signed-out, 1280×800 + 375×812); log defects, fix in-slice.
2. **54.2: Empty-state & loading-state consistency audit** — every paged collection, actor, community, and object page should have a deliberate empty + loading + error state.
3. **54.3: Accessibility & keyboard-navigation regression** — verify the 47.3 ARIA pass held; Tab order, focus visibility, aria-live on toasts/notifications.

**Phase 45–52** (all COMPLETE — see [docs/ROADMAP.md](docs/ROADMAP.md)).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 53.9: **Branded WASM loading screen** (Phase 53) — replaced the bare "Loading…" text with a branded splash (pulsing logo + spinner + wordmark) using the app's design tokens. [changes/375](docs/changes/375-53.9-wasm-loading-screen.md)
  - 53.8: **Fix logout-on-refresh** (Phase 53) — INVESTIGATED: could not reproduce. `CookieAuthenticationStateProvider` correctly calls `/local/v1/session` on WASM startup; the auth cookie is persistent (`IsPersistent = true`). Live-verified: login → navigate → full page refresh → still authenticated. The bug was likely observed before the 53.4 post-login context race fix, or is environment-specific. No code change needed.
  - 53.7: **repliedCount on nested objects** (Phase 53) — `iris:repliedCount` on nested objects in outbox/feed, computed from `IReplyStore.GetRepliesAsync`. `isReplied` skipped (ambiguous). [changes/374](docs/changes/374-53.7-replied-count.md)
  - 53.6: **isLiked/isShared on nested objects + likedCount/sharedCount** (Phase 53) — `likedCount`/`sharedCount` on nested objects in outbox (cached) and feed; `isLiked`/`isShared` on nested objects in feed (per-requester, uncached). [changes/373](docs/changes/373-53.6-nested-object-interaction-state.md)
  - 53.5: **OrderedCollection capability advertisement + feed activity-type filtering** (Phase 53) — `iris:refresh`/`iris:query`/`iris:type` capability flags on page-1 `OrderedCollection`; `?type=...` query parameter on the feed endpoint filters to activities of that type. [changes/372](docs/changes/372-53.5-ordered-collection-capabilities.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
