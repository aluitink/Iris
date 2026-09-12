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
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first).
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

**96 - Utilize the various compatibility enhancements** - see 74.* there are various features we added to help support threads and other stuff, our UI should be compatible with this and utilize it for created content.
**97 - Perform a new user walk** - Run thought a typical onboarding scenaio, find some users to follow - like some posts, makes some posts, post content, bost and reply.
**98 - Improve collection scrolling** - An infity scroll style feed would be ideal, where a couple pages load and as we start to reach the bottom more items load.
## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **95 — Unauthenticated Public Timeline (DONE)** — fixed the public timeline's odd ordering (the feed was merging actors' outboxes grouped by actor in alphabetical IRI order, so an actor's older posts appeared before another actor's newer ones); `PublicFeedService.GetPublicFeedAsync` now sorts the merged feed newest-first by the activity's `published` date (items without a date sort last; stable within the same date). Fixed unauthenticated actor icon rendering: `UiContext.GetActorAsync` now falls back to a plain (unsigned) `HttpClient` when the session's signing client is null (signed out), so the actor document — and thus the avatar icon — resolves for logged-out visitors (remote actors still fail due to CORS, which is expected). Verified: unauthenticated users can access the public timeline + content/media (same-origin media proxy); local actors' icons render (media proxy), remote actors' icons fail (CORS, expected). **6 new unit tests** (`PublicFeedServiceTests`): newest-first ordering, stable merge within the same date, undated posts sort last, non-person actors excluded, empty outboxes. Build clean; 1874 passed / 0 failed (excl. pre-existing flaky federation tests). [changes/951](docs/changes/951-phase95-unauthenticated-public-timeline.md)

- **94 — Development mode anti-forge tokens (DONE)** — added a config toggle `Iris:Security:EnableAntiforgery` (env `IRIS_SECURITY_ENABLEANTIFORGERY`) to disable ASP.NET Core antiforgery **validation** for dev/Playwright testing (where a stale token signed with a previous Data Protection key ring would 400 the login/register forms). `WebAppFactory.IsAntiforgeryEnabled` (pure config parsing, unparseable values fail closed to enabled) + a `PermissiveAntiforgery` (no-op `IAntiforgery`, always-valid + dummy tokens) registered after `AddAntiforgery()` when the flag is off so it wins DI resolution; the `UseAntiforgery` middleware is **always** applied (the POST endpoints carry antiforgery metadata — removing it would throw), only the validator is swapped (which also keeps `/local/v1/antiforgery` working). Default = enabled (production unchanged); docker-compose + .env.example document the var with a "NEVER false in production" warning. **17 new coded tests** (14 unit `AntiforgeryToggleTests` + 3 integration `AntiforgeryDisabledIntegrationTests`: tokenless `POST /login` is 400 when enabled, 302 when disabled; token endpoint works when disabled). Live Playwright-verified (flag off → browser login succeeds with no token, 0 antiforgery console errors; reverted → 400 again). [changes/941](docs/changes/941-phase94-dev-mode-antiforgery-toggle.md)

- **93 — Profile page (DONE)** — Mastodon-style **banner + overlaid avatar** in the profile header (`ActorProfile` renders an `actor-profile-banner` strip with the enlarged avatar laid over it; `ActorIdentityHelper` gains `BannerIri`, and `IconIri`/`BannerIri` now resolve the typed-`Link { id }` wire shape Iris's server emits — the shape the old checks missed, which is why the avatar/banner never rendered). Fixed the broken **"Your posts"** tab + mis-scoped **Replies** + false-empty **Likes**: `PagedCollection` now **top-ups** pages for a filtered view (capped) so a mixed outbox (own posts interleaved with mirrored remote content, newest-first) surfaces the user's own items, and `OutboxFilter.IsOwnContentItem` resolves the actor IRI from a bare-IRI `Link` (not just `IObject.Id`); the Replies filter is now self-scoped and the Likes empty message corrected. `ActorAvatar` render now honors `IconIriOverride` (was fetch-only). Edit-profile require-approval checkbox moved into a labelled **`edit-profile-privacy` fieldset**. UI-only (client/WASM), 0 new coded web tests per WASM policy; 1852 passed / 0 failed. Live-verified (banner + avatar, own posts, self-scoped replies, real likes, fieldset, 0 console errors). Note: `Image` is not yet merged by `UpdateActivityHandler`, so banner upload is a future item. [changes/931](docs/changes/931-phase93-profile-page.md)

- **92 — Improve Directory (DONE)** — added a **This instance / All known actors** scope toggle (server `GET /ap/v1/search` gains `?local=true`, restricting the actor pass to local actors via `Handle IS NOT NULL`; `IActorStore`/`IGlobalSearchService`/3 stores take a `localOnly` flag; `SearchOptions.LocalOnly` drives it client-side). Reworked the directory card row: the follow button is pulled out of the expand button (no longer nested in the header), a **chevron** replaces the odd `+`/`−` recent-posts glyph (rotates when expanded), and CSS keeps long community descriptions from squashing the follow button. 3 new coded tests (`GlobalSearchServiceTests`); 1852 passed / 0 failed. Live-verified (toggle re-queries, follow buttons row-level, chevron expand, communities layout, 0 console errors). [changes/921](docs/changes/921-phase92-improve-directory.md)

- **91 — Improve notifications (DONE)** — user-centric notification verbs (server-side `FilterInboxByPrefs` drops server-only noise — Update/Undo/Flag/Block/Mute + remote post deletions, keeps account deletions — and sorts newest-first; both the list + unread badge pass the actor IRI). `NotificationRow` verbs: "sent you a follow request", "boosted a post mentioning you" (when @-tagged, via `NoteMentionsSelf`), "replied to your post", "mentioned you"; `Notifications.razor` keeps a client-side guard against non-account Deletes. 8 new unit tests (`NotificationFilterTests`); 1850 passed / 0 failed. Live-verified (newest-first, no noise rows, per-tab verbs correct), 0 new console errors. [changes/911](docs/changes/911-phase91-improve-notifications.md)

## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
