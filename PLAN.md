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


## Active Slice


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

**105 - Directory imrovements** - The directory This instance and All known tabs show the same data and it seems to be just this instance, if we have cached actor information, it might be cached as a generic object? Maybe we combine the views to search out local and external actors.
**106 - Profile Improvements** - The likes tab and likes collection is a list of links that are going to point to objects, we should try to hydrate those and provide a view of the items. Our profile is also missing Following and Followers, we should be able to manage them from our profile. Following and Followers should show actor cards.
**107 - Search Improvements** - If I search out a Lemmyverse community - I find the Group actor, when I click on it - it opens in an actor view, we should have a specific commnity view that allows us to alk the outbox of a community or the featured if they have a featured collection. The group will yield out a different type of objects - I think they contain list of likes and dilikes, we need a specialized view for this similar to what the lemmy communities show: https://lemmy.world/c/technology
## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **104 — Notification Improvements (DONE)** — improved the notification list: (1) accurate verbs for likes/boosts (checks `attributedTo` + IRI prefix to determine if the note is the user's own; falls back to "liked a post"/"boosted a post" when author can't be confirmed), (2) reply notifications show "replied to {author}'s post" when the parent note's author is known, (3) card-style target rendering for embedded notes (`.notification-note-card` with author + content preview; falls back to text link for bare IRI objects). UI-only (WASM), 0 new coded web tests (policy). Live Playwright-verified: "liked a post" fallback works, 0 console errors. [changes/1041](docs/changes/1041-phase104-notification-improvements.md)

- **103 — Actor Detail Improvements (DONE)** — improved the actor detail page: (1) Followers/Following tabs now render hydrated `ActorCard` components (avatar + name) via a new `ActorListPanel` component that fetches the paged collection and hydrates each actor document via `Ui.GetActorAsync` (works both authenticated and anonymous), (2) the avatar in the actor/profile header is now a clickable link to the actor's own detail page, (3) removed the "This is you." text from the actor detail page, (4) wrapped Follow/Moderation buttons in `.actor-detail-actions` to fix the header layout when the description is long. UI-only (WASM), 0 new coded web tests (policy). Live Playwright-verified: followers/following tabs show hydrated actor cards with correct links (andrew shows "Andrew Luitink" from the hydrated doc), avatar is clickable, no "This is you.", header layout correct, 0 console errors. [changes/1031](docs/changes/1031-phase103-actor-detail-improvements.md)

- **102 — Card Improvements (DONE)** — post-card polish: (1) larger card icon (the card header avatar went 2rem→2.5rem, scoped to `.object-header .actor-avatar` so only feed cards grow, and steps back down to 2rem at ≤768px via a `.object-item`-prefixed rule that wins specificity over the later-declared base rule), (2) minimalistic moderation buttons on the right of content cards (a new `CardModerationButtons` component renders a compact block/mute/report icon group before the timestamp, revealed on hover, shown only when the viewer may moderate the author — not on their own posts; wired via fire-and-forget `CardBlockAsync`/`CardMuteAsync`/`CardReportAsync` in `ObjectView.razor.cs`). UI-only (WASM), 0 new coded web tests (policy). Live Playwright-verified: avatar 40px desktop / 32px at 375px, mod buttons render on bob's card (not alice's own), Mute→204, 0 console errors. [changes/1021](docs/changes/1021-phase102-card-improvements.md)

- **101 — Feed Improvements (DONE)** — improved the home timeline feed and post card rendering: (1) boost age display (the "Boosted by" line now shows when the boost happened), (2) reply context position (the "In reply to" context card now appears below the post body, not above), (3) removed redundant mentions/hashtags sections (they're already in the post body), (4) fixed post link hover (text color no longer changes on hover), (5) reply filtering in home feed (replies from followed actors are filtered out; only top-level posts and boosts appear; the signed-in actor's own replies are kept). **3 new unit tests** (`Feed_FollowReply_IsFilteredOut`, `Feed_OwnReply_IsKept`, `Feed_FollowAnnounce_IsKept`). Full suite **1415 passed / 0 failed**. Live-verified via Playwright (posted a reply, verified it appears with context below, no separate mentions/hashtags sections, 0 console errors). [changes/1011](docs/changes/1011-phase101-feed-improvements.md)

- **100 — Follow-request (follow-approval) queue (DONE)** — exposed the pending inbound `Follow` requests held for approval (the `manuallyApprovesFollowers` gate) as a queryable local endpoint (`GET /local/v1/u/{handle}/requests`) + accept/reject endpoints (`POST .../requests/{accept|reject}/{requesterIri}`) + a `LocalModerationClient` surface + the Profile page's Requests tab. **Critical fix discovered during live verification:** the original implementation only gated the *remote* (inbox) follow path (`FollowActivityHandler`); *local* follows (both actors on the same instance) go through the outbox-publish path (`RecordFollowLocalAsync`), which never checked the gate — so a local follower of a gated actor never appeared in the queue. Added `IsManuallyApprovingPersonAsync` + a gate check in `RecordFollowLocalAsync` to record the `FollowRequest` edge (Kind=17) for local gated follows, mirroring the inbox path. **7 new integration tests** (5 for the remote/inbox path, 2 for the local/outbox path). Also fixed the Dockerfile to clean all `src`/`apps` `bin`/`obj` before build (stale-DLL caching bug that caused the live container to run old server code). Full suite **1412 passed / 0 failed**. Live-verified via curl (queue lists bob, accept drains + confirms, reject drains + removes edge). WASM UI Requests tab renders but the local-moderation client's absolute-URL construction (actor IRI host) mismatches the dev server host — a dev-env-only issue (production host matches). [changes/1001](docs/changes/1001-phase100-follow-request-queue.md)








## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
