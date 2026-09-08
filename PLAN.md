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

**Phase 45 — WASM manual test & bug hunt (COMPLETE).** 14 defects found + fixed across 5 slices; 0 deferred. [changes/340](docs/changes/340-45.7-triage-closeout.md).

**Phase 46 — Visual inspection & design pass (COMPLETE).** 6 slices (46.1–46.6): design audit, nav + brand, card system, object detail + notifications, mobile layout, forms + tabs + footer. All 14 design findings addressed. [changes/341–346](docs/changes/341-46.1-design-audit.md).

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

**51.5: Relay subscriptions UI (settings)** — list current relay subscriptions; add/remove relay by URL (F-06).

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

**Phase 51 — Feature completion & remaining gaps (in progress):**

1. **51.5: Relay subscriptions UI (settings)** — ACTIVE. List current relay subscriptions; add/remove relay by URL (F-06).

**Phase 45–50** (all COMPLETE — see [docs/ROADMAP.md](docs/ROADMAP.md)).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 51.4: **Moderation queue (admin)** (Phase 51) — `/admin/moderation` page listing all flags; `GET /local/v1/admin/flags` + `POST /local/v1/admin/flags/dismiss` (Admin role); `GetAllFlagEdgesAsync` in all 3 stores; live-verified flag→dismiss round-trip. [changes/363](docs/changes/363-51.4-moderation-queue-admin.md)
  - 51.3: **Instance metadata edit (admin)** (Phase 51) — `/admin/instance` page with name/description form; `IInstanceMetadataStore` (EF + in-memory); `GET`/`PUT /local/v1/admin/instance` (Admin role); EF migration; live-verified save + persist across restart. [changes/362](docs/changes/362-51.3-instance-metadata-edit-admin.md)
  - 51.2: **View own blocks/mutes/flags** (Phase 51) — Moderation tab in `/settings` with Blocked/Muted/Reported sections + undo buttons; outbox scan for minted activity IRIs (unblock/unflag); live-verified block→unblock round-trip. [changes/361](docs/changes/361-51.2-view-own-blocks-mutes-flags.md)
  - 51.1: **Mentions in compose** (Phase 51) — `@handle` detection in compose (regex, same-origin); `Mention` tags built in `ComposeNote.Build` + wired into all 4 post paths; fixed doubled-protocol IRI bug. [changes/360](docs/changes/360-51.1-mentions-in-compose.md)
  - 50.3: **Release checklist & documentation pass** (Phase 50) — fixed DP keys path; created `RELEASE.md`; full test suite green. [changes/359](docs/changes/359-50.3-release-checklist-documentation.md)
  - 50.2: **CHANGELOG + versioning** (Phase 50) — `CHANGELOG.md` written (v1.0.0); `Version`/`AssemblyVersion`/`FileVersion`/`AssemblyInformationalVersion` added to `Directory.Build.props`. [changes/358](docs/changes/358-50.2-changelog-versioning.md)
  - 50.1: **Security audit & dependency review** (Phase 50) — 0 vulnerable packages; fixed: cookie flags (HttpOnly/SameSite/SecurePolicy), non-root Docker user (iris uid 1001), DesignTimeDbContextFactory env-var connection string. [changes/357](docs/changes/357-50.1-security-audit-dependency-review.md)
  - 49.3: **API documentation** (Phase 49) — `Microsoft.AspNetCore.OpenApi` 10.0.11, `/openapi/v1.json` spec (44 endpoints), Swagger UI at `/api/`. [changes/356](docs/changes/356-49.3-api-documentation.md)
  - 49.2: **Load testing** (Phase 49) — load-test-iris.py (asyncio, p50/p95/p99, rps, error rate); ~62-65 rps @ 0% errors @ 10-100 concurrency; all endpoints equal; no app bottlenecks; scaling path = more containers. [changes/355](docs/changes/355-49.2-load-testing.md)
  - 48.2: **Backup & restore strategy** (Phase 48) — backup/restore scripts, DP keys volume, BACKUP.md. [changes/352](docs/changes/352-48.2-backup-restore-strategy.md)
  - 48.1: **Nginx reverse proxy config** (Phase 48) — nginx.conf, Caddyfile, deployment README. [changes/351](docs/changes/351-48.1-nginx-reverse-proxy-config.md)
  - 47.4: **Performance & polish** (Phase 47) — Cache-Control, page size, CSS vars, favicon. [changes/350](docs/changes/350-47.4-performance-polish.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
