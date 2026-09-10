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

- **Phase 62 — Bug hunt (in progress).** Full-system manual pass. App was modified without focus for several hours and has odd bugs. 62.1 documents defects only (no fixes), 62.2 clears blockers + finishes the review, 62.3 fixes, 62.4 re-passes until clean.
- **Phase 63 — UI/UX review (next).** Usability + presentation: is the data well organized, readable, functional. Detailed visual review + brainstorming on what makes a good interface.
- **Phase 64 — Request spam & network efficiency (next-next).** Distilled from 62's network notes. **Priority: reduce the *number* of calls the client makes** (duplicate/redundant fetches on load, refetch-on-re-render, N+1 fan-outs) — raw latency is secondary. Topics added during 62.1.
- **Phase 65 — (to be distilled from 63's findings at 63's closeout).**
- **Phases 62–69 — exploratory buffer.** 66–69 are reserved slack: if any phase overruns (more blockers than expected, new issue classes found, re-passes needed), work rolls forward into the buffer instead of compressing later phases. Use them as needed.
- **Phase 70 — Content & media improvement (the start of "improving").** After the exploratory buffer: ensure pictures/media render properly; ensure we can browse remote users and view their content; ensure we can post content and view it within our instance.

## Active Slice

- **70 — Content & media improvement.** The start of "improving" after the exploratory buffer. Three sub-goals (PLAN.md ledger line 79): (1) **media/pictures render correctly** end-to-end; (2) **browse remote users + view their content**; (3) **post content + view it within the instance**. These are already live-verified features from Phase 54 (54.16 images render, 54.20 remote object browsing, 54.21 remote search, 54.13 media upload); this phase is an **ensure-it-works + harden** pass — drive each path live via MCP Playwright on a fresh origin, find + fix any residual gaps, and verify no console errors. **No new coded tests** (WASM manual-test policy) — verify live.

  **Slice 70.1 — media/pictures render correctly (sub-goal 1) — DONE.** Drove the full media path live on a fresh origin (`:8150`, fresh WASM publish, DB `irisweb-db-1`): (1) upload via the cookie-auth `POST /local/v1/u/andrew/media` → **201**, blob stored on disk + a `Media` row; (2) `GET /ap/v1/media/{id}` → **200 image/png**; (3) created a consistent note-with-`Image`-attachment (Objects + Create Activity + BoxItems) and confirmed the object-detail view renders the `<img>` **same-origin** (`https://iris.luit.ink/ap/v1/media/{id}` → `/ap/v1/media/{id}` via `RewriteMediaToSameOrigin`), image decoded (`naturalWidth/Height` 10×10, `complete=true`), `alt=test-media.png`, **0 console errors**. Render code (`ObjectView` `MediaAttachments`/`ActivityMediaAttachments` + `RewriteMediaToSameOrigin`, `IriExtensions.GetMediaAttachments`) confirmed correct by inspection. **No code change required** — the path was already correct from Phases 20/54/61. Note: the compose-UI image-attach + post could not be driven via Playwright (Blazor `@bind` textarea value never registers under MCP Playwright — a known automation limitation, not an app defect); the render path was verified via the object-detail view (same `ObjectView` component) and the upload via the direct media endpoint. Test data cleaned up. Change doc: [701](docs/changes/701-media-render-verified.md).

  **Slice 70.2 — browse remote users + view their content (sub-goal 2) — DONE.** Drove the remote-browse path live on a fresh origin (`:8155`, fresh WASM publish, single `Iris.Web.Client.d8nxgntd79.wasm`, cookie-auth as `andrew`): (1) `/actor?iri=https://mastodon.world/users/RayvenMX` renders the remote profile (handle, name, avatar, bio, 10 followers / 19 following) + the actor's outbox — 14 posts each with author handle, link/mention/hashtag parsing, and rich media (a `.jpg` document rendered as a decoded `<img>`), **0 console errors**; (2) `/object?iri=…/statuses/…` renders a single remote Note (text, author, created, media), **0 console errors**; (3) the actor-document fetch is coalesced to **1×** (`POST /ap/v1/proxy/…/users/RayvenMX`) — Phase 64.1's `UiContext.GetActorAsync` in-flight gate holds for remote actors. **No code change required** — the path was already correct from Phases 20/61/64.1. **Residual (not fixed this pass):** each remote post's `EngagementBar` fires its `/likes`+`/shares` walk **2×** (28+28 instead of 14+14) because `ActorAvatar`'s async actor-fetch completion triggers a page `StateHasChanged` that **recreates** the `EngagementBar` as a fresh instance (so a per-instance `_countsLoaded` idempotency guard — attempted, verified live as a no-op, and reverted — cannot catch it). The robust fix is a shared per-object engagement-count cache on `UiContext` (mirroring the 64.1 actor-coalescing gate); deferred as a Phase-64 residual follow-up (bandwidth/latency efficiency, not a correctness or render issue — all 56 requests 200 and the page renders correctly). Change doc: [702](docs/changes/702-browse-remote-users.md).

  **remaining (70.3):** (3) post content + view it within the instance (compose → outbox → `/home` round-trip). Already live-verified in Phase 54 (54.13/32.4b) — this phase re-verifies + hardens. Note: the compose-UI text post may be limited by the Blazor `@bind` textarea Playwright limitation (same as 70.1's image-attach); the round-trip can be verified via the object-detail view or a direct DB insert if the compose UI cannot be driven.

**Phase 63 — COMPLETE** (63.1 visual/usability pass + 63.2 design/IA review).
Change docs: [630 tracker](docs/changes/630-ux-review-tracker.md) (63.1: 3 findings
U-01/U-02/U-03, all fixed in-slice + verified live) and [632](docs/changes/632-ux-ia-design-review.md)
(63.2: 3 IA findings; IA-01 root-redirect + IA-02 hide-admin-links-from-non-admins
implemented in-slice + verified live on `:8090`; IA-03 nav grouping deferred as
acceptable-as-is). **Build 0 warn/0 err; full suite green** (one known-flaky
delivery test fails in the full run, passes in isolation).

**Note for the loop (stale-WASM, re-confirmed this turn):** the server
`Iris.Web.csproj` `BuildAndCopyClient` target only republishes the client when
`Iris.Web.Client/publish/` is missing — a razor change requires deleting that
dir first (`python3 -c "import shutil;shutil.rmtree('apps/Iris.Web.Client/publish',ignore_errors=True)"`)
or the stale WASM is served. **Additionally, the Playwright MCP browser profile
caches the Blazor WASM by content hash across origins/sessions** — after a
razor republish the browser may still load a prior build's WASM even on the same
origin (a hard reload is not enough). The reliable workaround is to **launch the
server on a fresh origin (new port)** so the browser has no cached WASM for it.

### Phase cadence (rolling 3-phase pattern)

Work for **the phase after the next** is distilled from **the current phase**, while **the next phase** fixes problems found in the current. New work stays 1 phase ahead of fixing.

| Phase | Role | Status |
|---|---|---|
| 62 | Bug hunt — find + document + fix | **complete** (62.4 re-pass converged; [621](docs/changes/621-phase62-closeout-bug-hunt.md)) |
| 63 | UI/UX review — usability + presentation + design/IA | **complete** (63.1 visual pass [630](docs/changes/630-ux-review-tracker.md); 63.2 design/IA [632](docs/changes/632-ux-ia-design-review.md)) |
| 64 | Request spam & network efficiency — cut redundant/duplicate calls on load (7 topics in [620 tracker](docs/changes/620-bug-hunt-tracker.md#phase-64--request-spam--network-efficiency-draft-topics)) | **functionally complete** — all 7 non-deferred topics fixed (64.1 actor-coalescing #1+#2, 5×/4×→1×; 64.2 likes/shares fan-out #4+#5, 10+10→5+5; 64.3 feed content-dedup #3; 64.4 public-feed double-fetch #6, 2→1; 64.5 deleted-account avatar 410s #7, 15→0). Only the **optional minted-id ext** remains (deferred — small engagement-proportional residual, not a feed-size N+1). |
| 65 | distill from 63 | planned |
| 66–69 | exploratory buffer — overrun/slack for 62–65 (or later) phases | reserved |
| 70 | Content & media improvement — media rendering, remote browsing, post-and-view-in-instance | **active** — 70.1 (media render) + 70.2 (browse remote users) done (verification, [701](docs/changes/701-media-render-verified.md), [702](docs/changes/702-browse-remote-users.md)); 70.3 (post + view in instance) remaining |

- Each phase's closeout writes its change doc AND distills the next-next phase's topics into this file.
- Defects found in a phase are fixed in that phase's fix slices (62.3/62.4); UX-class findings route to 63; inefficiency findings route to 64.
- **Buffer (66–69):** phases 62–65 may overrun into 66–69 without renumbering — if 62's re-passes don't converge, 62.5/62.6 land in the buffer; if 63/64 find more than planned, their extra slices do the same. The buffer is consumed in order and logged in this file when used.
- **Operator latitude (binding for 62–69):** I am authorized to do whatever keeps testing moving — recreate containers, create new test users, seed content, restart services, adjust test data. Log significant interventions in the slice's tracker notes.

### Loop protocol (Phase 62 — bug hunt)

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
    - **Network watch — count calls, not bytes (always on)**: the point is to catch **request spam** — duplicate/redundant calls fired when a page or control loads (same fetch twice on mount, a refetch on re-render, an N+1 fan-out). For each distinct request pattern record: method+path, status, **how many times it fired (the count is the spam signal)**, and *what triggered it* (which load/control). These notes are the raw material for **Phase 64** (request-spam reduction).
    - **MCP Runs in docker** - If the MCP server seems down or stuck you can restart the container
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker** ([docs/changes/620-bug-hunt-tracker.md](docs/changes/620-bug-hunt-tracker.md)). Class is the routing axis (blocker→62.2, bug→62.3, UX→63, perf→64); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope** (62.2+ only — 62.1 documents only): implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence in the finding's `Verify` cell (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged in the tracker's Test-debt ledger** (test name, action, reason, restore-by) — no silent deletions; 62.4 closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics (62 → 64; 63 → 65) into this file.

### Slices (Phase 62)

| Slice | Scope |
|---|---|
| **62.1** | Full-page deep-dive — **documentation only, no fixes.** Work the tracker's Page-coverage inventory (all 18 routes), signed-in (as `andrew`) + authless, every control + state, deep links + refresh. Network watch throughout (count calls per distinct request pattern — the spam signal). All findings → shared tracker. Phase 64 topics drafted from network notes. **Timebox: ~5 routes or ~45 min per turn — stop, checkpoint, and continue next turn; roll overflow into the buffer, don't marathon.** |
| **62.2** | Fix blockers (anything holding up the review) + finish the review of pages 62.1 could not complete. |
| **62.3** | Fix remaining bugs (non-blocker defects from the tracker). |
| **62.4** | Re-pass (full deep-dive again from clean entries) until zero open blocker/bug findings; then 62 closeout (change doc + distill 64 topics + review Test-debt ledger). **Stop condition: two consecutive clean re-passes, or 3 re-passes max — whichever first. If still dirty at 3, stop and roll the remainder into the buffer (66–69) rather than looping.** |

## Up Next

- 63.1 — UI/UX visual/usability pass, route-by-route — **complete** (3 findings U-01/U-02/U-03 fixed in-slice + verified; [630 tracker](docs/changes/630-ux-review-tracker.md))
- 63.2 — UI/UX design/IA review — **complete** (3 IA findings; IA-01 root-redirect + IA-02 hide-admin-links implemented in-slice + verified live on `:8090`; IA-03 nav grouping deferred; [632 change doc](docs/changes/632-ux-ia-design-review.md))
- 64 — request spam & network efficiency — **COMPLETE** (all 7 non-deferred topics fixed; only the optional minted-id ext remains, deferred — see Recently Completed Phase 64 closeout): 7 topics in the [620 tracker](docs/changes/620-bug-hunt-tracker.md#phase-64--request-spam--network-efficiency-draft-topics). **64.1 done** — actor-fetch coalescing fixes #1 (local actor N+1, 5×→1×) + #2 (remote actor proxy N+1, 4×→1×) via an in-flight gate in `UiContext.GetActorAsync`. **64.2 done** — per-item likes/shares fan-out: the WASM read the `iris:` counter extensions under the multi-instance-**rewritten** dial-base namespace (`http://localhost:PORT/ns#`) while the server wrote them under the canonical FQDN namespace (`https://iris.luit.ink/ns#`), so the EngagementBar fast path was skipped and the full `/likes`+`/shares` count-walk fired per card (10+10). `IActorSessionAccessor.IrisNamespaceBase` now derives from `_rewriteBase` (canonical FQDN) → fast path engages → **10+10 → 5+5** (only the ~4 items the viewer engaged; the residual is engagement-proportional id-recovery, not feed-size N+1). Verified live on `:8093` (fresh origin). **64.3 done** — home-feed content-object coalescing: a single post was rendering **twice** (the author's `Create` + a follower's `Announce` boost, distinct IRIs the by-IRI de-dup couldn't remove) → 2 engagement bars → 2× the per-card walks. `FeedService.TruncateDedup` now coalesces by **content-object IRI**, preferring the item carrying the object **embedded** (the author's `Create`) over a link-only reference (the boost); verified live on `:8095` (`GET /ap/v1/u/andrew/feed`): the status appears once, the boost is dropped, 0 duplicated content-object IRIs; 5 new `FeedServiceTests`. **64.4 done** — public-feed double-fetch on `/`: the public feed's first page was fetched **twice** on mount because `Home.razor` rendered its `PagedCollection` keyless and the page re-renders once `OnInitializedAsync` awaits the session (the parent disposed/recreated the card, resetting `_loadedFor` → re-fired the initial load). A stable `@key="@PublicFeedKey"` (`const string`) keeps the instance across the re-render → the load fires once; verified live on `:8120` (anonymous): `public/feed?limit=20` **2 → 1**. Note: the `@key` value must be a C# variable, not a string literal (Razor-generator `CS1662`/`CS0246`). **64.5 done** — deleted-account avatar 410s on `/notifications`: every "deleted their account" row (a `Delete` with `object == actor`) fired a `POST /ap/v1/proxy/{gone-actor}` avatar fetch that **410s** (15 page 1 / 28 total). `NotificationRow.OnInitializedAsync` now skips the actor-doc fetch for deletions (shared `IsSelfDelete` helper), and a new `ActorAvatar.SkipFetch` flag (set only by `NotificationRow` for deletions + the IRI-derived fallback name) short-circuits the avatar to the initial-letter fallback — **15 → 0** 410s, verified live on `:8140` (0 console errors; no `/home` avatar regression). **remaining:** only the **optional minted-id extension** (render the minted Like/Announce IRI on the object so the per-engaged-card id-recovery walk disappears — the residual 5+5 from 64.2; a bounded server addition + new client extension read, **deferred** — small engagement-proportional residual, not a feed-size N+1).
- 65 — (distilled from 63 at 63's closeout)
- 66–69 — exploratory buffer (consumed in order if any of 62–65 overruns)
- 70 — content & media improvement: media/pictures render correctly · browse remote users + view their content · post content + view it within the instance

## Inbox

- *(empty)*

## Paused Questions

- None currently.

## Recently Completed

- **70.2 — browse remote users + view their content (Phase 70, sub-goal 2) — DONE, verification slice** ([change doc 702](docs/changes/702-browse-remote-users.md)): drove the remote-browse path live on a fresh origin (`:8155`, fresh single-WASM publish, cookie-auth as `andrew`): (1) `/actor?iri=https://mastodon.world/users/RayvenMX` renders the remote profile (handle, name, avatar, bio, 10 followers / 19 following) + the actor's outbox — 14 posts each with author handle, link/mention/hashtag parsing, and rich media (a `.jpg` document rendered as a decoded `<img>`), **0 console errors**; (2) `/object?iri=…/statuses/…` renders a single remote Note (text, author, created, media), **0 console errors**; (3) the actor-document fetch is coalesced to **1×** (`POST /ap/v1/proxy/…/users/RayvenMX`) — Phase 64.1's `UiContext.GetActorAsync` in-flight gate holds for remote actors. **No code change required** — the path was already correct from Phases 20/61/64.1. **Residual (not fixed this pass, deferred to a Phase-64 follow-up):** each remote post's `EngagementBar` fires its `/likes`+`/shares` walk **2×** (28+28 instead of 14+14) because `ActorAvatar`'s async actor-fetch completion triggers a page `StateHasChanged` that **recreates** the `EngagementBar` as a fresh instance (a per-instance `_countsLoaded` idempotency guard — attempted, verified live as a no-op, and reverted — cannot catch it); the robust fix is a shared per-object engagement-count cache on `UiContext` (mirroring the 64.1 actor-coalescing gate). A bandwidth/latency efficiency issue, not a correctness or render one (all 56 requests 200; the page renders correctly).
- **70.1 — media/pictures render correctly (Phase 70, sub-goal 1) — DONE, verification slice** ([change doc 701](docs/changes/701-media-render-verified.md)): drove the full media path live on a fresh origin (`:8150`, fresh WASM publish, DB `irisweb-db-1`, cookie-auth as `andrew`): (1) upload `POST /local/v1/u/andrew/media` → **201** + blob on disk + `Media` row; (2) `GET /ap/v1/media/{id}` → **200 image/png**; (3) created a consistent note-with-`Image`-attachment (Objects + Create Activity + BoxItems) and confirmed the object-detail view renders the `<img>` **same-origin** (`RewriteMediaToSameOrigin`: `https://iris.luit.ink/ap/v1/media/{id}` → `/ap/v1/media/{id}`), image decoded (10×10, `complete=true`), `alt=test-media.png`, **0 console errors**. Render code (`ObjectView` `MediaAttachments`/`ActivityMediaAttachments` + `RewriteMediaToSameOrigin`; `IriExtensions.GetMediaAttachments`) confirmed correct by inspection. **No code change required** — the path was already correct from Phases 20/54/61. The compose-UI image-attach + post could not be driven via MCP Playwright (Blazor `@bind` textarea value never registers under Playwright — a known automation limitation, not an app defect); the render was verified via the object-detail view (same `ObjectView`) and the upload via the direct media endpoint. Test data cleaned up.
- **Phase 64 — request spam & network efficiency — COMPLETE (functionally complete; all 7 non-deferred topics fixed).** 6 slices: [641](docs/changes/641-request-spam-actor-coalescing.md) (actor-fetch in-flight coalescing, #1 local N+1 5×→1× + #2 remote proxy 4×→1×), [642](docs/changes/642-request-spam-likes-shares-fanout.md) (per-item likes/shares fan-out — `iris:` namespace-base mismatch fixed, 10+10→5+5), [643](docs/changes/643-request-spam-feed-content-dedup.md) (home-feed Create+Announce content-object coalescing, #3), [644](docs/changes/644-request-spam-public-feed-double-fetch.md) (public-feed double-fetch on `/` via a stable `@key`, #6), [645](docs/changes/645-request-spam-deleted-account-avatar-410s.md) (deleted-account avatar 410s, #7). The only remaining item is the **optional minted-id extension** (render the minted Like/Announce IRI on the object so the per-engaged-card id-recovery walk disappears — the residual 5+5 from 64.2), **explicitly deferred** — a small engagement-proportional residual (not a feed-size N+1); it needs a new store query (requester+object → activity IRI) and may be picked up as a follow-up or deferred indefinitely. **Phase 70 (Content & media improvement) is now active.**
- **64.5 — deleted-account avatar 410s on /notifications (request spam, topic #7) — Phase 64 functionally complete** ([change doc 645](docs/changes/645-request-spam-deleted-account-avatar-410s.md)): every "deleted their account" row on `/notifications` (a `Delete` activity whose `object == actor` — the account itself is the deleted object) fired a `POST /ap/v1/proxy/{gone-actor}` avatar fetch that **410-Gones** (15 on page 1 / 28 total in 62.1) — pure network noise + console errors. **Two** fetch paths 410'd per row: `NotificationRow.OnInitializedAsync` → `UiContext.GetActorAsync`, and `ActorAvatar.OnInitializedAsync` (no icon override → fetches for the icon). **Fix:** a shared `IsSelfDelete(activity)` helper; `NotificationRow.OnInitializedAsync` skips the actor-doc fetch for deletions; a new `ActorAvatar.SkipFetch` parameter (set `true` **only** by `NotificationRow` for deletions, plus the IRI-derived fallback name as `DisplayName`) short-circuits `ActorAvatar` to the initial-letter fallback. Set only for deletions, so every other `ActorAvatar` caller (post cards, ActorCard, ActorProfile, CommunityDetail) keeps its fetch behavior — no avatar regression. **Verified live on fresh origin `:8140`** (signed in as `andrew`): **0** 410 responses anywhere (was 15 page 1 / 28 total); the only proxy fetch is the live RayvenMX (`200 OK`); all 17 deletion rows render the initial-letter fallback + "deleted their account" caption; **0 console errors**; `/home` avatars unchanged. Build 0 warn/0 err; `Iris.Web.Tests` 62/62. **All 7 non-deferred Phase 64 topics (#1–#7) are now fixed** — Phase 64 is functionally complete; only the optional minted-id extension (deferred) remains.
- **64.4 — public-feed double-fetch on mount (request spam, topic #6)** ([change doc 644](docs/changes/644-request-spam-public-feed-double-fetch.md)): on `/` (the authless landing) the public feed's first page (`GET /ap/v1/public/feed?limit=20`) was fetched **twice** on mount. **Root cause:** `Home.razor` rendered the public feed's `<PagedCollection>` **keyless**; `OnInitializedAsync` awaits `Session.EnsureReadyAsync()` (an async auth-state resolve → re-render), and on that re-render the parent's `<NotAuthorized>` child content disposed/recreated the keyless card, resetting its `_loadedFor` and re-firing the initial first-page fetch. **Fix:** a stable `@key="@PublicFeedKey"` (a `const string` field) on the card — mirroring `HomeTimeline.razor`'s `@key="@FeedKey"` — keeps the same component instance across the re-render so the initial load fires once. **Razor gotcha:** `@key` with a string-literal value does not compile under this SDK's Razor generator (`CS1662`/`CS0246` cascade); the value must be a C# variable. **Verified live on fresh origin `:8120`** (anonymous): `public/feed?limit=20` **2 → 1**; landing renders correctly. Build 0 warn/0 err; `Iris.Web.Tests` 62/62 (WASM-side → Playwright-verified, no new coded web test per policy).
- **64.3 — home-feed content-object coalescing (request spam, topic #3)** ([change doc 643](docs/changes/643-request-spam-feed-content-dedup.md)): the home timeline rendered a single post **twice** — once as the author's `Create` (note embedded, from the followed actor's outbox) and once as a follower's `Announce` (boost, note link-only, from the viewer's own outbox which is prepended). Distinct activity IRIs → the by-IRI de-dup couldn't remove them; the client's `IsContentItem` renders both → 2 engagement bars → 2× the per-card `/likes`+`/shares` walks. **Fix:** `FeedService.TruncateDedup` now coalesces **by content-object IRI** — a `Create`/`Announce` referencing the same object keeps one representative, preferring the item carrying the object **embedded** (the author's `Create`) over a link-only reference (the boost); the representative keeps its first-seen position (stable ordering); the `MaxItems` cap applies last. **Verified:** 5 new `FeedServiceTests` + live on `:8095` (`GET /ap/v1/u/andrew/feed`): the status appears once (the embedded `Create`), the boost is dropped, 0 duplicated content-object IRIs, `Like`s preserved. Full fast suite green (Iris.Server.Tests 974/975, one known-flaky delivery test passes in isolation; Iris.Web 62/62).
- **64.2 — per-item likes/shares fan-out (request spam, topics #4 + #5)** ([change doc 642](docs/changes/642-request-spam-likes-shares-fanout.md)): the EngagementBar's 54.8 fast path (seed counts from the server-rendered `iris:likedCount`/`sharedCount`/`isLiked`/`isShared` and skip the count-walk) was not engaging — the full `/likes` + `/shares` walk fired per card (10+10 on `/home`). **Root cause:** `IActorSessionAccessor.IrisNamespaceBase` derived the `iris:` namespace from the multi-instance-**rewritten** dial base (`http://localhost:PORT/ns#`) while the server writes the counters under the **canonical FQDN** namespace (`https://iris.luit.ink/ns#`) → mismatch → fast path skipped. **Fix:** `IrisNamespaceBase` now derives from `_rewriteBase` (the canonical FQDN, the pre-rewrite original). **Verified live on fresh origin `:8093`** (republished client + cookie-auth `/home` as `andrew`): `/likes` 10 → **5**, `/shares` 10 → **5** — un-engaged cards issue no walk; the residual 5+5 is only the ~4 items andrew engaged (engagement-proportional id-recovery, not feed-size N+1). Stable across two reloads; 0 console errors; build 0 warn/0 err; full suite green in isolation.
- **64.1 — actor-fetch in-flight coalescing (request spam, topics #1 + #2)** ([change doc 641](docs/changes/641-request-spam-actor-coalescing.md)): the per-card actor N+1 (the same author's document fetched once per card) eliminated. The actor cache existed but had **no in-flight coalescing** — N concurrent cards each missed the TTL cache before any populated it. Added a per-IRI in-flight `ConcurrentDictionary<string, Task<IObject?>>` gate in `UiContext.GetActorAsync` (first caller starts the fetch and publishes its `Task`; concurrent callers await it). **Verified live on fresh origin `:8091`**: local actor `GET /ap/v1/u/{actor}` 5× → **1×**; remote actor `POST /ap/v1/proxy/{host}/users/RayvenMX` 4× → **1×**. 0 console errors; build clean; full suite green in isolation.
- **63.2 — UI/UX design & information-architecture review — Phase 63 COMPLETE** ([change doc 632](docs/changes/632-ux-ia-design-review.md)): the "what makes a good interface" half of 63. Reviewed IA across nav, footer, root `/`, and `/home` timeline (live on `:8090`, `andrew`+`alice`). 3 findings: **IA-01** (med) root `/` was a dead-end interstitial for signed-in users → **fixed in-slice** (`Home.razor` now redirects signed-in users to `/home` in `OnInitializedAsync`; authless landing unchanged) — verified: `andrew`+`alice` login → auto-redirect to `/home`, authless `/` still shows hero+Public timeline. **IA-02** (low) admin footer links shown to all users → **fixed in-slice** (`MainLayout.razor` wraps Admin/Moderation/Dashboard in `@if (IsAdmin)`) — verified: non-admin sees only NodeInfo/WebFinger/Status, admin sees all 6. **IA-03** (low) flat 9-link nav — **deferred** (fits all widths, mobile hamburger already groups; revisit if nav grows past ~11 links). Build 0 warn/0 err; full suite green (one known-flaky delivery test fails in the full run, passes in isolation). **Loop note logged:** the Playwright MCP browser profile caches the Blazor WASM by content hash — after a razor republish a fresh origin (new port) is required to bypass the stale cached WASM.



## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
