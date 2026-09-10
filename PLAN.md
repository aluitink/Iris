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

- **Phase 71 — Note card + compose polish (COMPLETE).** All six slices done (71.1–71.6). Change doc: [711](docs/changes/711-note-card-compose-polish.md).
- **Phase 72 — Efficiency + UX residuals (distilled from 63/64/70/71 closeouts).** Four items: (1) shared per-object engagement-count cache on `UiContext` (Phase 64/70 residual — eliminates the 2× `/likes`+`/shares` walk when `ActorAvatar`'s async fetch re-renders the page); (2) minted-id extension (Phase 64 residual — render the minted Like/Announce activity IRI on the object so the client skips the id-recovery walk); (3) richer inline document rendering (Phase 71.2 residual — non-image documents like PDFs render as a link-card, not an inline preview); (4) IA-03 nav grouping (Phase 63 residual — flat 9-link nav, low priority, revisit only if nav grows past ~11 links).

## Active Slice

- **72 — Efficiency + UX residuals (in progress).** Distilled from Phase 63/64/70/71 closeouts. Four slices (details under Slices table): (1) engagement-count cache; (2) minted-id extension; (3) richer inline document rendering; (4) nav grouping. **No new coded tests** (WASM manual-test policy) — build 0 warn/0 err; `Iris.Web.Tests` 63/63.

  **Slice 71.1 — Note card built from all available content — DONE.** The feed/outbox `Create` branch of `ObjectView.razor` previously showed only the note's **text** + a `MediaGallery` for image attachments. Now builds the card from the full embedded object: in-reply-to, audience, updated timestamp, article duration/language, mentions, hashtags, custom emojis, media (all types), poll. Activity-scoped computed properties added to `ObjectView.razor.cs` (read the embedded content object, not the activity wrapper).

  **Slice 71.3 — Interaction bar at the bottom of the post — DONE.** The `EngagementBar` now renders **last** in the `Create` branch (after all content, metadata, attachments, poll), so the bar is the post's footer.

  **Slice 71.4 — Sensitive content blurred until reveal — DONE.** The content is now rendered **blurred** (CSS `filter: blur(8px)`, `user-select: none`, `pointer-events: none`) behind the notice, so the shape of the post is visible. The blur is removed on reveal (click-to-reveal kept). Applies to both the `Create` branch (new `ActivityRevealed` state) and the `IObject` branch (existing `Revealed` state, now drives a CSS class).

  **Slice 71.5 — Compose @handle autocomplete shows known actors — DONE.** The `@handle` autocomplete now shows the signed-in actor's **follows** as the default list (empty token), and filters the follows + supplements with live search for a non-empty token. New `UiContext.GetFollowingActorIrisAsync()` resolves the following collection (cached, 2-min TTL); `IsFollowingAsync` now delegates to it. `Compose.razor` injects `UiContext` and uses `GetActorAsync` (coalesced, 5-min TTL) to fetch each followed actor's name + handle.

  **Slice 71.2 — Attachments inline with text — PARTIAL.** Documents render after the text in the `MediaGallery` (which is now positioned after all content per 71.1). The document card style (icon + name + open-in-new-tab) is a link-card rather than a fully inline document preview; a richer inline rendering is deferred as a follow-up.

  **Slice 71.6 — Review remote user content with attachments — DONE.** Drove the remote-browse path live on `:8088` (fresh WASM publish, cookie-auth as `andrew`): (1) `/actor?iri=https://mastodon.world/users/RayvenMX` renders the remote profile + 14 posts, **0 console errors**; (2) `/object?iri=…/statuses/116182691592848717` (the "Doggo" status with a `.jpg` document attachment) renders the attachment as an `<img>` in the media gallery. **Fix:** `MediaGallery.razor`'s `IsImage` now treats `Document` attachments with image file extensions as images (Mastodon sends image attachments as `Document` type with a `Preview`).

  **Phase 71 — COMPLETE** (all six slices: 71.1 [card from all content], 71.2 [attachments inline — partial, richer doc preview deferred], 71.3 [bar at bottom], 71.4 [sensitive blur], 71.5 [mention autocomplete], 71.6 [remote attachments verified + fixed]).

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
| 71 | Note card + compose polish — card built from all content, inline attachments, interaction bar at bottom, sensitive-blur, mention dropdown | **COMPLETE** — all 6 slices done |
| 72 | Efficiency + UX residuals — engagement-count cache, minted-id extension, inline document rendering, nav grouping | **in progress** — see Slices table |

- Each phase's closeout writes its change doc AND distills the next-next phase's topics into this file.
- **Operator latitude:** I am authorized to do whatever keeps testing moving — recreate containers, create new test users, seed content, restart services, adjust test data. Log significant interventions in the slice's tracker notes.

### Loop protocol

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
    - **MCP Runs in docker** - If the MCP server seems down or stuck you can restart the container
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker**. Class is the routing axis (blocker → current phase's blocker slice, bug → current phase's fix slice, UX → a later UX phase, perf → a later efficiency phase); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope**: implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics into this file.

### Slices (Phase 72)

| Slice | Scope |
|---|---|
| **72.1** | **Shared per-object engagement-count cache on `UiContext`** (Phase 64/70 residual). Each remote post's `EngagementBar` fires its `/likes`+`/shares` walk **2×** (14 posts → 56 round-trips instead of 28) because `ActorAvatar`'s async actor-fetch completion triggers a page `StateHasChanged` that **recreates** the `EngagementBar` as a fresh instance (a per-instance `_countsLoaded` guard — attempted, verified as a no-op, reverted — cannot catch it). **Fix:** a shared per-object engagement-count cache on `UiContext` (key by object IRI), mirroring the 64.1 actor-coalescing gate. A re-created `EngagementBar` for the same post reuses already-loaded counts. **Caveat:** local-feed engagement state (like/boost) optimistically mutates the per-instance count — the shared cache must not stale those (the cache stores the *server-derived* count; the optimistic delta is applied on top in the component). |
| **72.2** | **Minted-id extension** (Phase 64 residual). The residual 5+5 `/likes`+`/shares` walks that remain after 64.2 — one collection walk per card the viewer actually engaged (liked/boosted), to recover the minted Like/Announce activity IRI that an unlike/un-boost (Undo) references. **Fix:** bounded server addition — render the minted Like/Announce activity IRI on the object alongside `isLiked`/`isShared` (a per-(requester, object) activity lookup in `EnrichCollectionItemsAsync`) — plus a new client extension read so the client never needs the id-recovery walk. |
| **72.3** | **Richer inline document rendering** (Phase 71.2 residual). Non-image documents (PDFs, etc.) still render as a link-card (icon + name + open-in-new-tab) rather than a fully inline document preview. **Fix:** render non-image documents with a preview image (if `Preview` is present) + name + download link, consistent with the image grid, so an attached document reads as part of the post. For PDFs, consider an embedded `<iframe>` or `<object>` preview (bounded height, click-to-expand). |
| **72.4** | **IA-03 nav grouping** (Phase 63 residual, low priority). The top nav is a flat row of 9 links with no visual grouping — actions, navigation, and account links are interleaved. **Fix:** a visual grouping (a divider before the account links, or a right-aligned account cluster). **Only action if the nav grows past ~11 links** — currently fits at all desktop widths and the mobile hamburger already groups. |

## Up Next

- 72.1 — Shared per-object engagement-count cache on `UiContext` (eliminates the 2× `/likes`+`/shares` walk on re-render).
- 72.2 — Minted-id extension (render the minted Like/Announce IRI on the object; client skips the id-recovery walk).
- 72.3 — Richer inline document rendering (non-image documents get a preview + name + download link, not just a link-card).
- 72.4 — IA-03 nav grouping (low priority; only if nav grows past ~11 links).

## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

**Resolved (this turn) — Login 400 was stale Playwright context state, not a code/proxy bug.** Investigating the recurring MCP Playwright sign-in 400 step-by-step proved it was **not** a code bug and **not** a reverse-proxy misconfiguration. The persistent Playwright browser context had accumulated **stale/desynced anti-forgery state** (from repeated testing: clearing cookies, re-fetching `/local/v1/antiforgery` tokens, posting multiple times in rapid succession); a desynced (field-token vs `.AspNetCore.Antiforgery.*` cookie) pair is rejected with a silent **400**. In a clean state (fresh context / cookies cleared) the **normal form flow works every time**: navigate `/login` → fill `handle`+`password` → click "Sign in" → **302** to `/home` (verified 3/3 in fresh contexts + with the standard Playwright MCP tools, no `run_code_unsafe`). The proxy correctly passes `Set-Cookie` + forwarded headers + TLS; **no proxy reconfiguration needed**. **Repeatable login:** if a login 400s, log out first (or use a fresh context) to clear stale anti-forgery state, then re-login normally. **71.6 is now unblocked.**

## Recently Completed

- **71.6 — Review remote user content with attachments (Phase 71, slice 6) — DONE, Phase 71 COMPLETE** ([change doc 711](docs/changes/711-note-card-compose-polish.md)): drove the remote-browse path live on `:8088` (fresh WASM publish, cookie-auth as `andrew`): (1) `/actor?iri=https://mastodon.world/users/RayvenMX` renders the remote profile + 14 posts, **0 console errors**; (2) `/object?iri=…/statuses/116182691592848717` (the "Doggo" status with a `.jpg` document attachment) renders the attachment as an `<img>` in the media gallery. **Fix:** `MediaGallery.razor`'s `IsImage` now treats `Document` attachments with image file extensions (`.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.svg`, `.avif`, `.bmp`) as images — Mastodon sends image attachments as `Document` type with a `Preview`, which the old check (`Type is null or "Image" && Preview is null`) missed. **Phase 71 (Note card + compose polish) is COMPLETE** (71.1 [card from all content] + 71.2 [attachments inline — partial] + 71.3 [bar at bottom] + 71.4 [sensitive blur] + 71.5 [mention autocomplete] + 71.6 [remote attachments verified + fixed]).
- **71.1/71.3/71.4/71.5 — Note card + compose polish (implementation)** ([change doc 711](docs/changes/711-note-card-compose-polish.md)): (71.1) the feed/outbox `Create` branch of `ObjectView.razor` now builds the card from the full embedded object (in-reply-to, audience, updated, article meta, mentions, hashtags, emojis, media, poll) — not just text + images. (71.3) `EngagementBar` moved to the bottom of the post (footer). (71.4) sensitive content rendered **blurred** (CSS `filter: blur(8px)`) until reveal, in both `Create` + `IObject` branches. (71.5) compose `@handle` autocomplete shows the signed-in actor's **follows** as the default list (empty token) + filters/supplements with live search (non-empty token); new `UiContext.GetFollowingActorIrisAsync()`. Build 0 warn/0 err; `Iris.Web.Tests` 63/63. **No new coded tests** (WASM manual-test policy).
- **70.3 — post content + view it within the instance (Phase 70, sub-goal 3) — DONE, Phase 70 COMPLETE** ([change doc 703](docs/changes/703-post-view-in-instance.md)): re-verified the compose → outbox → `/home` round-trip live on the **redeployed 8088 container** (fresh `docker compose up --build`, FQDN `https://iris.luit.ink` — same-origin, **0 console errors**; the earlier `:8088` CORS errors were an artifact of dialing the FQDN-advertised instance on `localhost:8088`, not a code defect). Posted as `andrew` through the **real `IActivityPubClient.PostNoteAsync`** (the exact code `Compose.razor`'s `PostAsync` invokes) — a signed client built with andrew's key fetched via the cookie-auth owner-only actor doc (the WASM session's own key-load path, replicated server-side) — → **HTTP 202**, MintedId `…/creates/06G8QD0VYJTVGC6CXQFXWYS30G`. Verified: (1) newest item in andrew's outbox; (2) renders **at the top** of `/home` ("just now"); (3) `/object?iri=…` detail view renders it in thread context, **0 console errors**. **No code change required** (path already correct from Phases 32/54). **Caveat:** the compose textarea is **not drivable via MCP Playwright** (Blazor `@bind="Content"` never registers synthetic input — `pressSequentially`/per-key/native `input` all leave `Content` empty → `PostAsync` no-ops; a human typing works). Verified via the server write path the UI invokes, not the undrivable UI form. **Phase 70 (Content & media improvement) is COMPLETE** (70.1 [701] + 70.2 [702] + 70.3 [703], all verification slices).
- **70.2 — browse remote users + view their content (Phase 70, sub-goal 2) — DONE, verification slice** ([change doc 702](docs/changes/702-browse-remote-users.md)): drove the remote-browse path live on a fresh origin (`:8155`, fresh single-WASM publish, cookie-auth as `andrew`): (1) `/actor?iri=https://mastodon.world/users/RayvenMX` renders the remote profile (handle, name, avatar, bio, 10 followers / 19 following) + the actor's outbox — 14 posts each with author handle, link/mention/hashtag parsing, and rich media (a `.jpg` document rendered as a decoded `<img>`), **0 console errors**; (2) `/object?iri=…/statuses/…` renders a single remote Note (text, author, created, media), **0 console errors**; (3) the actor-document fetch is coalesced to **1×** (`POST /ap/v1/proxy/…/users/RayvenMX`) — Phase 64.1's `UiContext.GetActorAsync` in-flight gate holds for remote actors. **No code change required** — the path was already correct from Phases 20/61/64.1. **Residual (not fixed this pass, deferred to a Phase-64 follow-up):** each remote post's `EngagementBar` fires its `/likes`+`/shares` walk **2×** (28+28 instead of 14+14) because `ActorAvatar`'s async actor-fetch completion triggers a page `StateHasChanged` that **recreates** the `EngagementBar` as a fresh instance (a per-instance `_countsLoaded` idempotency guard — attempted, verified live as a no-op, and reverted — cannot catch it); the robust fix is a shared per-object engagement-count cache on `UiContext` (mirroring the 64.1 actor-coalescing gate). A bandwidth/latency efficiency issue, not a correctness or render one (all 56 requests 200; the page renders correctly).
- **70.1 — media/pictures render correctly (Phase 70, sub-goal 1) — DONE, verification slice** ([change doc 701](docs/changes/701-media-render-verified.md)): drove the full media path live on a fresh origin (`:8150`, fresh WASM publish, DB `irisweb-db-1`, cookie-auth as `andrew`): (1) upload `POST /local/v1/u/andrew/media` → **201** + blob on disk + `Media` row; (2) `GET /ap/v1/media/{id}` → **200 image/png**; (3) created a consistent note-with-`Image`-attachment (Objects + Create Activity + BoxItems) and confirmed the object-detail view renders the `<img>` **same-origin** (`RewriteMediaToSameOrigin`: `https://iris.luit.ink/ap/v1/media/{id}` → `/ap/v1/media/{id}`), image decoded (10×10, `complete=true`), `alt=test-media.png`, **0 console errors**. Render code (`ObjectView` `MediaAttachments`/`ActivityMediaAttachments` + `RewriteMediaToSameOrigin`; `IriExtensions.GetMediaAttachments`) confirmed correct by inspection. **No code change required** — the path was already correct from Phases 20/54/61. The compose-UI image-attach + post could not be driven via MCP Playwright (Blazor `@bind` textarea value never registers under Playwright — a known automation limitation, not an app defect); the render was verified via the object-detail view (same `ObjectView`) and the upload via the direct media endpoint. Test data cleaned up.
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
