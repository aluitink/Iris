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

Phase 30 (server production-readiness & hardening) is the active phase. 30.1 (configuration surface) is complete; 30.2 (health check + readiness probe) is next.

## Active Slice

**Phase 31 — explorer UI & server namespace polish.** 31.1 (WebFinger RFC 7033 host handling + client base-URI retry), 31.2 (`PagedCollection` initial-load re-render + error-state spinner guard), and 31.3 (carla `remote.example` fake-remote seed gated behind `Iris:Seed:RemoteStandIn`, default off) complete. Next: 31.4 (actors page — directory search returns only actors; rename to "Directory"). Phase 30 (30.2 health check + readiness probe) resumes after the Phase 31 Inbox is drained.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

**Phase 30 — server production-readiness & hardening** (the operational surface: making the server deployable and robust in real conditions, not just in-process tests):

1. ~~30.1: **configuration surface**~~ **COMPLETE** — `AddActivityPubServer(IServiceCollection, IConfiguration)` binds `ActivityPubServerOptions` + all delivery/observability options from `Iris:*` sections. 9 integration tests. → [docs/changes/273](docs/changes/273-30.1-configuration-surface.md)
2. 30.2: **health check + readiness probe** — expose an `IHealthCheck` that verifies the persistence provider is reachable and the delivery worker is running, plus a readiness gate that reports not-ready until the initial key material is loaded. Integration test: assert the health endpoint reports Healthy after startup and Unhealthy after the persistence provider is faulted.
3. 30.3: **structured logging + diagnostics** — wire `ILogger<T>` into the delivery worker (per-attempt, per-dead-letter), the signature validator (rejection reason), and the inbox handler (activity type, actor, outcome) so a production deployment has actionable logs without adding a logging package. Integration test: capture logs from a delivery + rejection scenario and assert the structured fields are present.

> **Deferred (live-infrastructure, not provisionable in this environment):**
>
> 4. External-FQDN verification — resolver reachability, public navigation, browser verification over live hostnames. (Needs a reachable public FQDN / reverse proxy; deferred.) See [phase-22-closeout.md §1](docs/plans/phase-22-closeout.md#1-external-fqdn-verification).
> 5. Live federation/interop checks against public Mastodon accounts (follow, post/receive, signatures, pagination, community flows). (Needs real external accounts; deferred.) See [phase-22-closeout.md §2](docs/plans/phase-22-closeout.md#2-live-federation-and-interop-checks).



## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

**Phase 31 — explorer UI & server namespace polish** (user review, 2026-09-05). Visual-confirmed via MCP Playwright against the running sample (UI :8090, server :8081) before each slice starts.

1. ~~31.1: **WebFinger — non-matching resources + client base-URI parsing (RFC 7033)**~~ **COMPLETE** — server returns 404 for a non-matching `host` (RFC 7033); client retries a failed dial-base resolution against the account's own advertised host (RFC 8410) when the hosts differ. 6 new tests. → [docs/changes/274](docs/changes/274-31.1-webfinger-rfc7033-host-and-retry.md)
2. ~~31.2: **`PagedCollection` initial load never re-renders — "Loading…" stuck until Refresh is clicked (feed + actor detail).**~~ **COMPLETE** — `LoadInitialAsync` (fired fire-and-forget from `OnParametersSet`) now ends with `StateHasChanged()`, mirroring the auto-re-render the `@onclick` handlers rely on; the spinner branch is also guarded on `LoadError` so a failed first fetch shows the error line, not an eternal spinner. Repairs every `PagedCollection` (feed + all actor-detail collections). New bUnit project, 4 tests. → [docs/changes/275](docs/changes/275-31.2-pagedcollection-initial-load-rerender.md)
3. ~~31.3: **Sample should not seed fake `remote.example` activities.**~~ **COMPLETE** — carla (the `remote.example` in-process stand-in) is now gated behind `Iris:Seed:RemoteStandIn` (default off); the default sample seeds one honest instance (alice, bob, community) with no fake cross-instance graph. Tests that exercise carla opt in; 2 new tests assert the default excludes carla. Also fixed `CreateWebHostBuilder` to pass the host's resolved config into `ConfigureServices` (per-host `UseConfiguration` was invisible to the seed). → [docs/changes/276](docs/changes/276-31.3-sample-no-fake-remote-seed.md)
4. ~~31.4: **Actors page — directory search should return only actors; rename to "Directory".**~~ **COMPLETE** — the search surface gained an optional `?type` filter (client `SearchOptions.Type`); `type=Actor` runs only the actor pass (no content), a non-actor type filters the content pass by `@type`, and no type keeps the full actor+content search. The `/actors` page passes `Type=Actor` and is renamed "Directory" (nav + `<PageTitle>`); route unchanged. 5 new tests. → [docs/changes/277](docs/changes/277-31.4-directory-actors-only-and-rename.md)
5. ~~31.5: **Actor detail — break up into tabs + profile-style header.**~~ **COMPLETE** — new `ActorProfile` component (avatar + handle + name + summary + IRI; initial fallback when no icon; reuses for 31.6) + the sections are now tabs (Outbox/Liked/Followers/Following/Raw, + a Moderation tab with a count badge once counts load). Header + tab bar always visible; tab switches preserve state; `ActiveTab` resets to Outbox on a new actor. 6 new bUnit tests. → [docs/changes/278](docs/changes/278-31.5-actor-detail-profile-header-and-tabs.md)
6. 31.6: **Communities — same treatment as actor detail.** `/community` should get the same tabbed, profile-style organization as 31.5 (it is the Group-actor analogue).
7. 31.7: **Shared collection browser.** A single shared component that walks the pages of an ordered collection and renders each item extensibly (a per-collection-type "template"; when none is registered, fall back to the basic object rendering). Reuse it for inbox / outbox / feed / liked / followers / following (and the moderation collections) instead of the ad-hoc per-page `PagedCollection` + hand-rolled follower/following lists.
8. 31.8: **Server — use the public URI in the namespace + host the namespace document.** The `iris:` extension namespace should be derived from the server's public (advertised) base URI, and the server should actually host the namespace document (the JSON-LD context at that IRI) so it is resolvable, rather than advertising an unresolvable `https://iris.example/ns#`.
9. 31.9: **Followed-feed (`/feed`) is missing items — only 6 of 15 activities returned.** Confirmed live (MCP Playwright + curl, 2026-09-05): after the full Mastodon round-trip (follow, accept, post, 2 replies, 2 boosts, 2 stars, 2 quotes, 2 alice posts), the community feed (`/ap/v1/c/iris/feed`) correctly holds **15 items**, but the followed-feed (`/ap/v1/u/alice/feed`) returns only **6**: 2 Announce + 1 Like + 3 Note. **Missing: all 7 Create activities** (RayvenMX's original "Hello World" post, 2 replies, 2 quotes, alice's 2 posts) **plus alice's Accept**. The UI faithfully renders the 6 the server returns — the bug is in the server's `FollowFeedHandler` (`ActivityPubServerExtensions.cs:4741`), which builds the "union of followed actors' outboxes" but is dropping Create activities (and the Accept) from the union. Likely cause: the handler reads each followed actor's outbox but filters or fails to include `Create`-type items (possibly only collecting Note/Announce/Like, or the Create's embedded object isn't being resolved from the outbox entry). Fix: ensure the followed-feed union includes all activity types present in the followed actors' outboxes (Create, Announce, Like, Note, Accept, etc.), de-duplicated, newest-first. Add an integration test: seed a followed actor's outbox with a Create + Announce + Like + Note, assert the follower's `/feed` returns all four.
10. 31.10: **Remote Likes are not recorded — a remote actor starring a local post leaves the local `liked` collection empty.** Confirmed live (Mastodon → Iris, 2026-09-05): RayvenMX (mastodon.world) starred both of alice's posts; Iris accepted each `Like` (202) and stored it in the inbox, but `GET /ap/v1/u/alice/liked` returned **0 items**. `LikeActivityHandler` only records the like edge when the **liker is a local actor** (it writes to the liker's own `LikeStore`); a **remote** liker's like on a local post is accepted + stored but never surfaced anywhere (no liked entry, no like-count on the object, no feed entry). For federation parity (Mastodon shows "N boosts / N favourites" on a post regardless of who reacted), a remote actor's like on a local object should be recorded — at minimum as a like-count on the object (or a "likes received" collection on the local author) — so the local instance can show that a remote actor reacted. Scope: extend `LikeActivityHandler` to record remote-liker likes on local objects (a new store or a count field), surface it in the object view / actor detail, and add an integration test (two-instance: remote actor likes a local post → local `liked`/like-count reflects it). Also confirm the symmetric `Undo(Like)` path removes the remote-liker like.

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 31.5: **actor detail — profile-style header + tabs** — new `ActorProfile` component (avatar + handle + name + summary + IRI; name-initial fallback when no icon; reuses for 31.6 `/community`) + the one-long page is now tabs (Outbox/Liked/Followers/Following/Raw, + a Moderation tab with a count badge once counts load). Header + tab bar always visible; tab switches preserve state. 6 new bUnit tests. Full fast suite green (1439/1439). → [docs/changes/278-31.5-actor-detail-profile-header-and-tabs.md](docs/changes/278-31.5-actor-detail-profile-header-and-tabs.md)
- 31.4: **directory search returns only actors; page renamed "Directory"** — the search surface gained an optional `?type` filter (client `SearchOptions.Type`): `type=Actor` runs only the actor pass (no content), a non-actor type filters the content pass by `@type`, no type keeps the full actor+content search (backward compatible). The `/actors` page passes `Type=Actor` and is renamed "Directory" (nav + `<PageTitle>`); route unchanged. 5 new tests. Full fast suite green (1433/1433). → [docs/changes/277-31.4-directory-actors-only-and-rename.md](docs/changes/277-31.4-directory-actors-only-and-rename.md)
- 31.3: **sample should not seed fake `remote.example` activities** — carla (the `remote.example` in-process stand-in) is gated behind `Iris:Seed:RemoteStandIn` (default off); the default sample seeds one honest instance with no fake cross-instance graph. Tests that exercise carla opt in; 2 new tests assert the default excludes carla. Also fixed `CreateWebHostBuilder` to pass the host's resolved config into `ConfigureServices`. Full fast suite green (1428/1428). → [docs/changes/276-31.3-sample-no-fake-remote-seed.md](docs/changes/276-31.3-sample-no-fake-remote-seed.md)
- 31.2: **`PagedCollection` initial-load re-render + error-state spinner guard** — the fire-and-forget `LoadInitialAsync` now ends with `StateHasChanged()` (the card re-renders when the first page arrives, no manual Refresh click); a failed first fetch shows the error line without an eternal spinner. New bUnit project (first consumer of pinned bunit 2.9.0), 4 tests. Full fast suite green (1426/1426). → [docs/changes/275-31.2-pagedcollection-initial-load-rerender.md](docs/changes/275-31.2-pagedcollection-initial-load-rerender.md)
- 30.1: **configuration surface** — `AddActivityPubServer(IServiceCollection, IConfiguration)` binds `ActivityPubServerOptions` + delivery/inbound/feed/health options from `Iris:*` config sections. 9 integration tests. Full fast suite green (908/908, 6m11s). → [docs/changes/273-30.1-configuration-surface.md](docs/changes/273-30.1-configuration-surface.md)
Rolling window of the last ~2 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
