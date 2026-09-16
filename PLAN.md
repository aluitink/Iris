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
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics into this file.

## Up Next

- **141 — Collection & engagement tracking consistency (likes/shares/replies):** review, and fix, how likes, shares (announces/boosts), and replies are tracked across every collection type — user-actor AP-native collections/objects, community/group AP-native collections, and Iris-extension collections — so behavior is consistent everywhere. All referenced objects and their subsequent updates must be tracked; stored documents must be refreshed when an update to a tracked object is observed (not just captured once at initial ingest). UI interactions must reflect true persisted state: clicking like/share in the UI must persist across a page refresh, and displayed counts must reflect the actual tracked/stored state rather than only optimistic client-side state. **[141.3 DONE: UI like/boost persistence verified PASS via Playwright — state persists across refresh for local objects. 141.1 deferred (bare-link reply edge is low-value; proper fix is fetching+storing remote object). Remaining: 141.2 (O(n) sweep elimination), 141.4 (cross-collection audit), 141.5 (closeout).] [plan](docs/plans/phase-141-engagement-tracking-consistency.md)**
- **142 — Cross-cutting consistency review (topical, no fixes):** a review-only pass across the app looking for inconsistencies in design and behavior — actor pages vs. actor cards, object pages vs. object cards, and other repeated UI patterns (badges, action bars, empty/error states, spacing/typography), plus backend consistency (naming, response shapes, capability checks, error handling) across equivalent endpoints. Catalog findings only — **no bug fixing in this phase**; every finding gets logged with page/component, expected vs. actual, and severity, then routed to **Phase 143** for remediation. Deliverable: a findings doc (`docs/plans/phase-142-consistency-review.md` or similar) that Phase 143 consumes as its worklist.
- **143 — Consistency review fixes:** implement the fixes for findings logged in Phase 142. Scope and detailed worklist to be pulled from the Phase 142 findings doc once that review closes.
- **144 — General UI/UX review and improvements:** a broad Playwright-driven review pass across the app's pages focused on usability, visual polish, and general UX quality (not just cross-component consistency, which is Phase 141/142's scope) — layout, responsiveness, affordances, loading/empty/error states, accessibility basics, copy clarity, and overall feel. Log findings with page/component, expected vs. actual, and severity; implement fixes in scope where straightforward, otherwise route larger items to a follow-up slice.

## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Known flake (non-blocking)

- **Background-delivery round-trip tests flake under full-suite load:** `MutualPeeringHandshakeIntegrationTests.MutualFollow_BothFollowersCollections_ListTheOther` (138.6) and `FollowEdgeConvergenceIntegrationTests.Follow_Unfollow_Refollow_Cycle_..._StableCollections` intermittently time out when the *whole* suite runs (each builds many in-process `TestServer`s, each with a background `DeliveryWorker` pump; under load a delivery's async continuation can go unscheduled and the round trip hangs in-flight — diagnosed: dead-letter store **and** queue both empty on timeout, i.e. stuck in-flight, not failed). Both pass in isolation and on most full runs. A thread-pool min-threads initializer (200) + 120s wait window (`TestThreadPoolInitializer.cs`) made 2 of 3 consecutive full runs green and cut Iris.Server.Tests ~2m→~24s, but the residual deadlock (the worker's own pump task can still be unscheduled long enough to hang) is a genuine next-work-item: **drive these specific tests' delivery deterministically** (e.g. a test delivery-driver that pumps the `IDeliveryQueue` synchronously to completion) instead of relying on the background pump. Non-blocking: if a full-suite run hits it, re-run the suite (or the two classes) in isolation — they pass.

## Recently Completed

- **140 (inbound signature-verification investigation) — closed, root cause was infra:** the steady stream of "could not resolve public key" / "malformed Signature header" rejections was caused by a **Docker DNS alias collision**, not an Iris signature bug. Both Iris's and Lemmy's compose stacks ran on the shared `iris-web-net` network and both had a `db` service; Docker auto-aliased each by name, so `db` resolved to two Postgres instances. The app round-robined; hitting Lemmy's DB (wrong password) → `28P01`, which the inbox handler misattributed to a key-fetch failure. Fixed by renaming Lemmy's `db` → `lemmy-db` (unique alias). Verified: zero rejections post-fix, timeline renders, no 28P01. No Iris code change needed.
- **139 (platform-wide e2e review) — closed (139.1/139.2 done, 139.3–139.8 superseded by 140/144):** 139.1 federation/interop (12 scenarios) + 139.2 security/trust-boundary (14 scenarios) executed with evidence; F-3/F-5/F-7 fixed, F-6 (community-follow relay) design-decided (impl pending), 139.2-S5 (privacy/visibility) deferred. Findings routed to Phase 140–144. [phase-139 plan](docs/plans/phase-139-platform-e2e-review.md).
- **138 — Phase 138 (Lemmy community integration) DONE (138.1–138.29, 2026-09-15):** two-way Iris↔Lemmy federation is live — mutual peering + trust checks, explicit cross-post, community relay inbound, comment threading, likes/downvotes both ways, score reconciliation (separate counters + derived `iris:score`), full-thread backfill + local rebuild, edit propagation, delete vs. mod-removal, 5 new `iris:` metadata terms + client-side CW derivation, and a cross-platform consistency review + living [INTEROP_CONFORMANCE_MATRIX](docs/reference/INTEROP_CONFORMANCE_MATRIX.md). Closeout: regression green (only the two known-flaky background-delivery tests fail, pass in isolation) + live Playwright pass (Iris↔Iris/Mastodon + Iris↔Lemmy; platform-conditional UI correct — `LemmyVoteBar` for Lemmy content, `EngagementBar` for Iris content). [phase-138 plan](docs/plans/phase-138-lemmy-community-integration.md) · [13829 closeout](docs/changes/13829-phase138-closeout.md).
- **138.26 (community-level NSFW alignment) — decided + implemented:** `RequiresCw(content, community)` client helper; no server-side retro-apply. 8 tests.
- **138.25 (Lemmy metadata rendering) — implemented:** `ServeObjectDocument`/`CommunityDocumentHandler` render the new `iris:` terms. 7 tests.
- **138.24 (Lemmy metadata extension terms) — designed:** 5 new `iris:` terms; namespace doc updated; `GetBool` bug fixed; 6 client readers; 7 tests.
 ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
