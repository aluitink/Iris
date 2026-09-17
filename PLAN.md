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
**Phase 151 - Background Processing** - Our goal is to cache content, any content we happen upon should be replicated for access in our database. If a UI request proxy loads an object or actor we should cache them, however the content we are fetching needs to resolve quickly, this is an interactive load and we don't have time to decorate all of the items with our extensions. We should have a background service that identifies objects that need a refresh or need enrichment. The idea is that an object is initially cache as is, but we will crawl the reset of the object in the background filling in more information about it. Actors have outboxes that can be crawled, we should probably be topical at first, attempt to identify a total count of items and record it, actors have followers and following. We should enhance our stored objects with iris extension properties. Not just actors, content as well, it would be good if each object stored some meta information about replies/likes/shares in extension properties so we could easily show that infromation quickly when we load our objects.

**Phase 152 - Announce/Like card enhancements** - When we render a boosted by or Liked by card we are showing the original card, if that original card was an inreplyto we want to show that original post it was in reply to - essentially a boosted/like post should render as the original post card - just with the boost/like indicator.

**Phase 153 - General object card improvements** - The object cards actor header could render with a thin banner strip similar to an actor card to add a bit of color and splash to the various cards - this personalization would live behind the user's avatar and name - possibly with some darker opacity so the name renders well on top of the banner strip. On hover for any card should show the link click mouse cursor to indicate it's clickable. I don't like the card text turning into a link - the entire card should be the link.

**Phase 154 - Notifications** - Our notifications page stands out as looking different then all other pages, we should show notifications and a stream of cards similar to the other pages, we should use tabs instead of the se pill selectors - similar to the other pages. Can we somehow highlight the new notifications so they stand out and when you click them or mark all as read they change. The cards on the notification page could just be normal object cards just like we see in the feeds.

**Phase 155 - Actor Posts Feed** - There is no need for moderation buttons on the actors post feed cards - the moderation buttons live on the actor page so we don't need additional buttons per card in their outbox. We should be using common card controls everywhere - so they sould look the same as the main feed and profile outbox feed.

**Phase 156 - New Post/Compose enhancements** - We really need a way to auto complete and generate proper @mentions when composing, ideally the user would start to type @ and a selection dropdown would auto complete by performing searches for actor. I suppose since the client posts the message to the outbox, the client should build the tags and mentions lists in the post. Review the way mastodon constructs it's messages by viewing some of the cached objects we have in the database. We want to emulate this by constructing the html within the content body that provide the link to the actor and populates the mentions/tags. We will likely need to also handle this on the backend by parsing out @mention and #hashtags that don't have matching references in our note, so new hashtags and and mentions resolve properly, obviously they would need to be @handle@domain.tdl and #hashtag

**Phase 157 - Signature Compatibility Check** Review the ui container logs for indications of failed signature validation and investigate.
## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Known flake (non-blocking)

- *(none — Phase 145 eliminated the known background-delivery flake by driving the two affected tests' delivery deterministically.)*

## Recently Completed

- **Phase 150 - Additional Media Viewer** (done, live-verified, Core tests 446/446, web tests 106/106): attachments that are video/audio now render an embedded `<video>`/`<audio>` player (controls + preload="metadata") instead of a poster image, detected via the attachment's `mediaType` or its URL extension (`RichAttachment.MediaType` + `ResolveAttachmentMediaType`); a plain web-link (`type:"Link"`) attachment now renders as a clickable link card (`<a target="_blank" rel="noopener">`, no forced download, not proxied through the media endpoint). Root cause fixed: `GetRichAttachments` only read `type`/`name` off `IObject`, so a `Link` attachment surfaced `Type == null` and was misclassified as an image — it now reads the declared type/name off `ILink` so a `type:"Link"` attachment yields `Type == "Link"`. New `IsPlainWebLink`/`IsVideoUrl`/`IsAudioUrl` predicates in `MediaGallery`. Live-verified: video note renders `<video controls>` with 0 console errors; link note renders `<a target="_blank" rel="noopener" class="doc-gallery-link">` (no proxied `<img>`, 0 console errors). See docs/plans/production-app-feature-matrix.md (media rows) + docs/ROADMAP.md.

- **Phase 149 - Community Page Renovations** (done, live-verified, web tests 106/106): `/communities` now shows a Following / "All on this instance" view over the `local=true` actor search (the old unqualified search returned 100 mixed remote actors → empty page; fixed with `LocalOnly=true`), each community rendered as the unified community card (banner + avatar + handle + Community badge + summary + post/member stats + Follow/Unfollow); the Following tab re-reads the following collection on a toggle. The Directory's Communities tab uses the same card (149.2). Community feed post cards are clickable → `/object?iri=…` whose object page shows the reply thread (149.3). Community-card avatar sized to the feed-card 2.5rem (149.4). New `CommunityCard` component + `FollowButton.FollowChanged` event. See docs/plans/production-app-feature-matrix.md (Communities + feed rows).

  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
