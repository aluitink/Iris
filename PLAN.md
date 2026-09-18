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
| [docs/reference/INTEROP_CONFORMANCE_MATRIX.md](docs/reference/INTEROP_CONFORMANCE_MATRIX.md) | Living peer×capability conformance matrix (the "are we consistent" artifact) | reference; update as interop slices land |
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
- If we see long running tests, check `dotnet test --help` review how blame works - set timeout for 15 seconds and mark any that timeout as skipped.
- To mark a test slow: `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`). Only mark tests that wait on real wall-clock time. Details + honest payoff note: [docs/reference/TESTING.md §Running the suite: fast vs. full](docs/reference/TESTING.md#running-the-suite-fast-vs-full).



### Loop protocol

> **CRITICAL RUNTIME RULE FOR WEBPAGE ACCESS:** You must prevent stale data or frontend caching on every action. For every new task, navigation, or data-refresh step, execute a **hard reload / cache bypass** *before* reading any data or evaluating elements — navigate with the `networkidle` lifecycle wait and do not proceed until all fresh server network requests have completely settled. Never rely on previously opened states, in-memory page references, or a cached (content-hash) Blazor WASM; when re-verifying after a rebuild, use a fresh browser context (close/reopen).

Each slice is a **Playwright-driven pass**, not a code-first slice.

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first). For pgAdmin, start the dev layer instead: `docker compose -f apps/Iris.Web/docker-compose.yml -f apps/Iris.Web/docker-compose.dev.yml up --build -d`.
3. **Clean entry (every slice, every re-verification)**: close the browser entirely, clear cookies + storage, reopen, enter the app fresh. Never carry state between slices or between a defect and its re-verification.
4. **Manual test (MCP Playwright)** at `https://iris.luit.ink`:
   - **Primary account: `andrew` / `Password1`** (has real content + external contacts — use it to evaluate every page).
   - **Secondary accounts**: `bob`, `carol`, `dave` (register as needed) for multi-account flows (follows, communities, moderation, notifications).
   - **Authless pass**: every page visited signed-out — verify gating (302 to login), no data leaks, no console errors, sensible signed-out UI.
    - **Work the page inventory, not "every page"**: the tracker's **Page coverage** table lists all 18 routes. Work it top-to-bottom; mark each route's signed-in + authless boxes as done, or `skipped(<reason>)`. Update the tracker's **Resume checkpoint** at the end of the slice so the next slice continues where this one stopped — never restart from scratch.
    - **Deep dive per page**: exercise every control, every state (empty/populated/error), deep links + hard refresh on each route.
    - Capture **console errors** (`browser_console_messages`). For screenshots, call the Playwright screenshot tool **with no `filename`** (it can't write to an arbitrary path); it auto-saves to `tmp/.playwright-mcp/page-<ts>.png` and returns that path — cite the returned path in the tracker rather than promising to attach a file.
     - **Network watch — count calls, not bytes (always on)**: the point is to catch **request spam** — duplicate/redundant calls fired when a page or control loads (same fetch twice on mount, a refetch on re-render, an N+1 fan-out). For each distinct request pattern record: method+path, status, **how many times it fired (the count is the spam signal)**, and *what triggered it* (which load/control).
     - **MCP Runs in docker** - If the MCP server seems down, stuck, or returns stale/cached page state for any reason, restart it: `bash scripts/start-playwright.sh` (recreates the `playwright-mcp-service` container with caching fully disabled, `--isolated`, on host port 8931).
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker**. Class is the routing axis (blocker → current phase's blocker slice, bug → current phase's fix slice, UX → a later UX phase, perf → a later efficiency phase); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope**: implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger. Try to not create any new tests.. we have too many.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics into this file.

## Active Slice

*(none — the like/boost count fix investigation is complete; a complete fix with remote fetching is deferred)*

## Up Next

1. **139.3 Data lifecycle & persistence review: Scenario 7 — Duplicate/replay delivery idempotency** — Redeliver the same activity IRI twice; confirm it's stored once (Phase 136.17), no duplicate rows, no duplicate UI entries. Evidence: DB row count + UI check.
2. **139.3-s6 Finding 1 (follow-up): bound the outbound connection phase** — A community feed following an *unreachable* peer (TCP accepted, TLS stalls) hangs ~60–70s because `HttpClient.Timeout` (5s) bounds the send phase but not the connection phase (falls back to the transport's 35s `ConnectTimeout`). Fix: construct the outbound transport as a `SocketsHttpHandler` with an explicit `ConnectTimeout` in `ActivityPubClientFactory.Create`, and/or parallelize the feed's per-contributor remote fetches with a per-fetch `CancellationTokenSource` timeout so one unreachable peer can't block the others.
3. **139.3-s6 Finding 2 (follow-up): foreign-IRI object pages 404 via the catch-all** — `ObjectDocumentHandler` reconstructs the lookup IRI as `baseUrl + RoutePrefix + path` (a *local* IRI), so a stored foreign object (e.g. `lemmy.luit.ink/post/1`) is unreachable by path. The UI's `/object?iri=` path already covers the user-facing case. Fix: serve stored foreign objects by an explicit `?iri=` lookup on the object endpoint, or document full-IRI addressing.
4. **General UI/UX review** (recurring) — Reviewed Home, Notifications, Profile, Compose, Communities pages via MCP Playwright as andrew:Password1. All pages look consistent and functional. No major inconsistencies or improvements found. Once improvements are made, this item will come up again for further refinement.

## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- 139.3 Data lifecycle & persistence review: Offline rebuild (scenario 6) — **PARTIAL PASS + 2 findings** (review slice, no code change). Core offline-rebuild bar holds: with `lemmy-1` stopped, the public timeline (20+ posts), the `andrew` person feed (4.2s, 5 local + 15 cached-remote, totalItems 197), and a local note object page (2.7ms) all render fully from local storage; the person feed degrades gracefully (5s per-fetch cap). Finding 1: a community feed following an *unreachable* peer (iris-dev2, TCP-accepted/TLS-stalled) is blocked ~60–70s (HttpClient.Timeout bounds send, not the connection phase). Finding 2: a foreign-IRI remote object page 404s via the object catch-all (endpoint reconstructs a local IRI; the UI's `?iri=` path covers the user-facing case). Both logged as follow-ups. [change doc](docs/changes/1393-6-offline-rebuild.md)
- 139.3 Data lifecycle & persistence review: Federated content archival completeness (scenario 5) — Found + FIXED a bug: a followed REMOTE community (Lemmy) persisted to the durable store by the remote community persister (135.1) was misrouted to its (empty) local outbox, so first-peer backfill (138.20) captured nothing. Fixed CommunityFeedService (host-locality gate via new instanceBase ctor param, wired from BaseUri in DI) in both ReadOutboxAsync + the isRemote flag. Verified live: c/technology (follows lemmy.world) 0→20 backfilled items; c/owner-test-5428 (follows lemmy.luit.ink) 0→3; lemmy.world content persisted to the local object store (14 objects). +regression test +Lemmy-shape client test. [change doc](docs/changes/1393-5-federated-content-archival-completeness.md)
- 139.3 Data lifecycle & persistence review: Tombstone permanence vs. mod-removal (scenario 4) — Author-delete permanence PASS (live: posted+deleted a note as andrew → Tombstone/formerType=Note/no removedBy; IRI resolves to marker; UI "Note post deleted"; re-animation guard covered by passing 136.19 tests). Mod-removal FINDING: a mod-removal IS over-tombstoned (same Tombstone as author delete, differing only by the iris:removedBy display marker); no restoration path exists, contrary to the 138.23 "may be restorable" note. Logged as a doc-vs-behavior gap; no code change. [change doc](docs/changes/1393-4-tombstone-permanence-vs-mod-removal.md)
- 139.3 Data lifecycle & persistence review: Backup/restore round-trip (scenario 3) — Found + FIXED a silent media backup/restore bug: the scripts used the unprefixed volume name (`iris-media-data` vs. the real `irisweb_iris-media-data`), so the backup captured 0 of 5,204 media blobs (a DR restore would lose all media). Fixed both scripts (backup via `compose exec`+tar of volume root; restore resolves real volume name + streams over stdin). Full round-trip re-verified: DB row counts identical, 5,204 blobs restored, app healthy. [change doc](docs/changes/1393-3-backup-restore-roundtrip.md)
- 139.3 Data lifecycle & persistence review: Cache-vs-store consistency (scenario 2) — Verified `?refresh=true` returns store-fresh data on all cached read paths. Definitive staleness proof: actor doc plain read stale (postsCount 488) → `?refresh=true` store-fresh (489) → plain read fresh (489, written back). Outbox/community feed/members all honor the contract. 4 documented findings (feed not page-cached, outbox inline refresh check, actor?iri ignores refresh). [change doc](docs/changes/1393-2-cache-vs-store-consistency.md)



  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
