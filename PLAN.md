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

*(none — 139.3-F2 deleted-actor edge filter is complete; next: 138.25 UI surfacing of Lemmy metadata terms)*

## Up Next

1. **138.25 follow-up — UI surfacing of Lemmy metadata terms.** `iris:locked` → disable the reply composer; `iris:featured` → show a pinned indicator; `iris:communityNsfw` → CW gate for community posts. Server renders the terms (138.25 done); the UI surfacing is the follow-up. Client-only. [change doc](docs/changes/13825-phase138-lemmy-metadata-rendering.md)
2. **General UI/UX review** (recurring) — Next pass after improvements land. Pass 9 (2026-09-19): verified the 138.27-S2 vote-bar generalization (16 vote bars on Lemmy community content), boost-card parity (tinted banners + moderation + stretched link), notification full-card render (Announce shows "Boosted by" + post-only timestamp, no duplicate), and the signed-out auth bar — clean entry, authless + signed-in, 0 new defects (only external lemmy.ml 500 / proxy 401 console noise). [change doc](docs/changes/14813-phase148-votebar-generalization-and-ui-polish.md)
3. **139.2 Scenario 5 (audience/visibility) — ALL surfaces done.** The S1 read-path gap is **closed** for the public feed, global search, the follow feed, the object-document endpoint (local + federated-in), and the federation read path. The **follow-feed owner gate** (S5c) is done (403 for non-owners). The **object-document visibility gate** (S5a/s5b) is now done for **all** non-tombstone content objects — local AND federated-in. The **federation visibility policy** (S5b) is done. Remaining (deferred product decision, not a defect): a `cc`-to-follower-set model for followers-only posts (scalability optimization, not a privacy fix — the current per-follower `cc` model is correct; should be its own phase). See [change doc](docs/changes/1392-5a5b-object-document-federated-gate.md).

## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **139.3-F2 — Filter edges referencing a removed actor at read time — COMPLETE (2026-09-19).** Every edge read path now excludes edges whose source was a *locally-provisioned-then-deleted* actor, while keeping edges from remote actors and never-provisioned local actors. In-memory: `InMemoryActorStore` tracks a `_removed` set (set on `RemoveActorAsync`, cleared on `PutActorAsync`/`Clear`); `SourceSurvives(Iri)` feeds a predicate wired into all 7 in-memory edge stores via `SetSourceExistsPredicate` (follow/like/dislike/announce/moderation/relay/community). EF: `EfActorStore` tracks a `_provisionedRemoved` set; `SourceSurvives(string)` is injected into the shared `EdgeStore` (constructor arg) and applied in-memory in `InSourcesAsync`/`InSourcesDescendingAsync`/`OutTargetsAsync` when `filterDeletedActors: true` — replacing an earlier over-broad correlated-subquery that wrongly dropped remote + never-provisioned sources. Fixed two missed read paths (`InMemoryFollowStore.GetFollowersAsync`, `InMemoryCommunityStore.GetFollowersAsync`). +8 in-memory +4 EF tests; updated `AccountDeletionRetentionTests` (a deleted member is now filtered from the member list). Build clean; Iris.Server.Tests 1371 passed / 0 failed; Iris.Server.Data.Tests 20 passed / 0 failed. [change doc](docs/changes/1393-f2-deleted-actor-edge-filter.md)
- **138.27-S2 vote-bar generalization + UI polish — COMPLETE (2026-09-19).** Vote-bar gate is now data-driven (`iris:dislikedCount`/`iris:isDisliked` extension OR a Lemmy `LemmyPostScore`), not Lemmy-IRI-shaped (`LemmyVoteBar`→`VoteBar`, +2 read helpers in `IrisDocumentExtensions`). Boost-card parity (stretched link, card-hue banner, moderation, vote bar), notification Like/Announce full-card render (`SuppressTime`/`SuppressBoostTime` dedup), signed-out sticky auth bar (replaces landing hero), mobile/long-handle overflow fixes; dead CSS removed. Build clean, `dotnet test` green (LiveInterop 5 = known external Lemmy 403). Live Playwright-verified (UI/UX Pass 9): 16 vote bars on Lemmy content, boost banners + moderation, notification full-card (no duplicate timestamp), auth bar, Home hard-refresh stable; 0 new defects. [change doc](docs/changes/14813-phase148-votebar-generalization-and-ui-polish.md)
- **139.2-s5a/s5b — Object-document visibility gate for federated-in non-public content — COMPLETE (2026-09-19).** The S5 visibility filter now gates the object-document endpoint for **all** non-tombstone content objects (local AND federated-in). Removed the `IsLocalActorAsync` restriction so a federated-in non-public post (e.g., a DM from alice on A to bob on B, stored on B) returns 404 for non-recipients while the named local recipient (bob, signed) and the author (signed) still get 200. The S5b decision supersedes the original S5a "remote objects are exempt" carve-out. Also recorded the decision to **DEFER** the `cc`-to-follower-set model (scalability optimization, not a privacy fix — current per-follower `cc` model is correct; should be its own phase). +3 cross-instance integration tests. Iris.Server.Tests 1364 passed, 0 failed. Closes 139.2-s5 deferred surface #2. [change doc](docs/changes/1392-5a5b-object-document-federated-gate.md)
- **UI/UX review Pass 7 — COMPLETE (2026-09-19).** Post-S5b verification. Authless pass: all 8 authenticated routes redirect to `/login`, 404 page + register accessible signed-out, 0 console errors, no data leak. Signed-in spot-check (andrew:Password1): Home timeline, Communities list + detail (hard-refresh stable — 147.2 parallel fan-out holds), Profile (5 tabs, Your posts + Following list), Compose deep-link (pre-selects target community, hard-refresh stable). 0 new defects; only console errors were remote-instance availability (lemmy.ml 500, lemmy.luit.ink 403), not Iris bugs. [change doc](docs/changes/997-ui-ux-review.md)
- **139.2-s5b — Federation visibility policy — COMPLETE (2026-09-19).** Pinned the S5(b) decision with cross-instance integration tests (test-only slice, no production source change): a non-public post (DM / followers-only) federated to a remote instance is stored on receipt with `to`/`cc` intact (no suppression), and the S5 read-path filter hides it from the receiving instance's public feed + global search for non-recipients while the named local recipient can still see it. The same privacy boundary applies whether content originated locally or arrived by federation. +4 tests, 2 updated. Iris.Server.Tests 1361 passed, 0 failed. Closes 139.2-s5 deferred surface #3. [change doc](docs/changes/1392-5b-federation-visibility-policy.md)
- **147.2 follow-up — CommunityFeedService parallel fan-out — COMPLETE (2026-09-19).** The community feed read member + followed-actor outboxes sequentially; now both branches use `Task.WhenAll` so total latency is bounded by the slowest single contributor, not the sum. Refactored `MergeContributorOutboxAsync` to return results (thread-safe); caller merges in deterministic IRI order with dedup. A failed/slow contributor contributes an empty list. All 38 existing community feed tests pass. Iris.Server.Tests 1357 passed, 0 failed. [change doc](docs/changes/1472f-community-feed-parallel-fanout.md)
*(The 404 page error overlay fix, Pass 2 of the recurring UI/UX review, the 139.4 UI/UX & accessibility review, plus Scenarios 2, 3, 4, 6, 7, 8 of 139.3, the s6-F1/s6-F2 follow-ups, and 140.1, are in [docs/ROADMAP.md](docs/ROADMAP.md) — see the one-liner entries there.)*



  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
