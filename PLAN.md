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

1. ~~**44.1: Content warning / sensitive flag on compose (F-28)**~~ **COMPLETE** — "Content warning" checkbox + summary input on Compose (Note posts); the Note path builds the note via `ComposeNote.Build` (sensitive + summary + to) and posts it via the `PostNoteAsync(Note)` overload; the feed's existing reveal toggle renders it. 4 integration tests. → [docs/changes/336](docs/changes/336-44.1-content-warning-compose.md)
2. ~~**44.2: Edit own post (F-02)**~~ **COMPLETE** — an "Edit" button on own posts (object detail, next to Delete) enters inline edit mode; the client's new `UpdateNoteAsync` posts an `Update` activity through the signed outbox; the server's `UpdateActivityHandler` refreshes the stored object in place and federates to followers. 5 integration tests. → [docs/changes/337](docs/changes/337-44.2-edit-own-post.md)
3. ~~**44.3: Media attachment (image) on compose (F-27)**~~ **COMPLETE** — an "Add image" file picker on Compose (top-level Note/Article posts only); on post the image is uploaded via `IMediaClient` (Phase 20.4a) to the local media endpoint → same-origin media IRI; `ComposeNote.Build` (and the Article path) carries a single `Image` attachment (url + id = media IRI, mediaType, name); the feed's existing `GetMediaAttachments` rendering shows it. 5 unit tests + 4 integration tests. → [docs/changes/338](docs/changes/338-44.3-media-compose.md)

## Active Slice

*(none — Phases 32–44 are complete; no slice selected. The next turn expands the next phase per the loop: if Up Next is empty, define the next phase as a one-line placeholder in ROADMAP.md, seed Up Next with its first slices, commit, and end the turn.)*

### Loop protocol (production app)

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build --no-cache iris-web && docker compose up -d iris-web`
3. **Verify**: MCP Playwright — log in as `alice` / `alice-password`, navigate pages, test interactions
4. **Tests**: `cd /workspace && dotnet test --no-build -c Release` (0 failures required)
5. **Visual review**: screenshot all pages (home, compose, profile, directory, notifications, search, actor detail, object detail, landing). Compare against what a polished social platform should look like.
6. **Add items**: append new findings to Up Next. Mark completed items with ~~strikethrough~~ + **COMPLETE**.
7. **Update PLAN.md**: move completed items to Recently Completed; keep Up Next sorted by priority.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap.

*(empty — no slices queued. Phases 32–44 are complete; the next phase will be defined by the loop per the Active Slice note.)*

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 44.3: **Media attachment (image) on compose (F-27)** (Phase 44) — "Add image" picker on Compose (top-level posts); on post uploads via `IMediaClient` → same-origin media IRI; `ComposeNote.Build`/Article path carries a single `Image` attachment; feed's `GetMediaAttachments` already renders it. 5 unit + 4 integration tests.
  - 44.2: **Edit own post** (Phase 44) — "Edit" button on own posts (object detail); client `UpdateNoteAsync` posts an `Update` activity; server refreshes the stored object in place and federates; 5 integration tests.
  - 44.1: **Content warning / sensitive flag on compose** (Phase 44) — CW checkbox + summary on Compose (Note posts); note built via `ComposeNote.Build` (sensitive + summary); feed reveal toggle already renders it; 4 integration tests.
  - 43.3: **Moderation on actor detail** (Phase 43) — `ModerationActions` (Block/Report via `IActivityPubClient`, Mute via `ILocalModerationClient`, Undo state from blocks/mutes collections) + community join-request "Requests" tab; 8 integration tests.
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
