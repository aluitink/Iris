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

Phase 32 (production social platform app) is the active phase — the user directed it to start immediately (2026-09-06), ahead of the deferred Phase 33. **Phase 32 is a new branch** (created at its first implementation turn from a clean, fully-committed `main`). **Phase 32 test policy (user directive): no new tests — maintain existing tests only.** Every slice keeps the existing suite green (fast run `dotnet test --filter "Category!=Slow"` as the loop check); the Phase 32 scope docs' test-project plans (e.g. `Iris.Server.Data.Tests`, `Iris.Web.Tests`) are deferred until the user lifts this directive.

## Active Slice

**Phase 32 — production social platform app.** A new Blazor app (`Iris.Web`) that is a real, deployable social platform on top of the libraries — production persistence (EF Core + PostgreSQL), local username/password auth, a full-stack docker-compose, and the MVP feature set (timeline, compose, communities, notifications, moderation, search). Fully scoped in [docs/plans/production-app-overview.md](docs/plans/production-app-overview.md) (a 3-level plan tree — read that file first, it links to the rest). **Step 0** (before any new code — [production-app-overview.md](docs/plans/production-app-overview.md) §6): bring down the running sample `docker-compose.yml` stack, relocate it to `samples/docker-compose.yml` (fix `build.context` `.`→`..`, drop `iris-ui`'s now-conflicting `8088:8090` mapping, update `scripts/docker-smoke-test.sh` + [docs/reference/DEPLOYMENT.md](docs/reference/DEPLOYMENT.md) paths). The production app then publishes on host port `8088` to take over the existing `https://iris.luit.ink` reverse-proxy target.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

**Phase 32 — production social platform app** (new branch; no new tests, maintain existing only):

1. 32.0: **relocate the sample compose stack** (step 0, [production-app-overview.md](docs/plans/production-app-overview.md) §6) — bring down the running root `docker-compose.yml` stack, `git mv` it to `samples/docker-compose.yml`, fix `build.context` `.`→`..`, drop `iris-ui`'s `8088:8090` mapping (keep `8090:8090`), update `scripts/docker-smoke-test.sh` + [docs/reference/DEPLOYMENT.md](docs/reference/DEPLOYMENT.md) paths, bring it back up from the new location + re-run the smoke test. Explicitly confirm (or placeholder) the `https://iris.luit.ink` 8088 outage tradeoff until the real `iris-web` stack lands.
2. 32.1: **bare `Iris.Web` host** — stand up `apps/Iris.Web` as a Blazor Web App that boots `Iris.Server` with in-memory persistence; confirm WebFinger / actor doc / inbox / outbox work inside the new host. → [production-app-web-host.md](docs/plans/production-app-web-host.md)
3. 32.2: **EF Core persistence provider** — `src/Iris.Server.Data` (PostgreSQL) as a production `IPersistenceProvider`; prove durability (container restart, data survives) before building UI against it. → [production-app-persistence.md](docs/plans/production-app-persistence.md)
4. 32.3: **local auth** — username/password registration + login, provisioning a local actor per account. → [production-app-authentication.md](docs/plans/production-app-authentication.md)

**Deferred to Phase 33 — server production-readiness & hardening (remainder)** (the operational surface: making the server deployable and robust in real conditions; 33.1 de-risks Phase 32's production observability but is not a hard gate):

1. ~~30.1: **configuration surface**~~ **COMPLETE** — `AddActivityPubServer(IServiceCollection, IConfiguration)` binds `ActivityPubServerOptions` + all delivery/observability options from `Iris:*` sections. 9 integration tests. → [docs/changes/273](docs/changes/273-30.1-configuration-surface.md)
2. ~~30.2: **health check + readiness probe**~~ **COMPLETE** — `PersistenceHealthCheck` (real read against `IPersistenceProvider.Actors`), `DeliveryWorkerHealthCheck` (worker `IsRunning`), `IReadinessGate`/`DefaultReadinessGate` (ready once the instance actor's signing key is registered + resolvable), and the public `GET /ap/v1/ready` probe (200/503, `{ready}`). 24 new tests. → commit 614130e (change doc pending)
3. 33.1 (was 30.3): **structured logging + diagnostics** — wire `ILogger<T>` into the delivery worker (per-attempt, per-dead-letter), the signature validator (rejection reason), and the inbox handler (activity type, actor, outcome) so a production deployment has actionable logs without adding a logging package. Integration test: capture logs from a delivery + rejection scenario and assert the structured fields are present.


## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

**Phase 31 — explorer UI & server namespace polish** (user review, 2026-09-05). Visual-confirmed via MCP Playwright against the running sample (UI :8090, server :8081) before each slice starts.

1. ~~31.1: **WebFinger — non-matching resources + client base-URI parsing (RFC 7033)**~~ **COMPLETE** — server returns 404 for a non-matching `host` (RFC 7033); client retries a failed dial-base resolution against the account's own advertised host (RFC 8410) when the hosts differ. 6 new tests. → [docs/changes/274](docs/changes/274-31.1-webfinger-rfc7033-host-and-retry.md)
2. ~~31.2: **`PagedCollection` initial load never re-renders — "Loading…" stuck until Refresh is clicked (feed + actor detail).**~~ **COMPLETE** — `LoadInitialAsync` (fired fire-and-forget from `OnParametersSet`) now ends with `StateHasChanged()`, mirroring the auto-re-render the `@onclick` handlers rely on; the spinner branch is also guarded on `LoadError` so a failed first fetch shows the error line, not an eternal spinner. Repairs every `PagedCollection` (feed + all actor-detail collections). New bUnit project, 4 tests. → [docs/changes/275](docs/changes/275-31.2-pagedcollection-initial-load-rerender.md)
3. ~~31.3: **Sample should not seed fake `remote.example` activities.**~~ **COMPLETE** — carla (the `remote.example` in-process stand-in) is now gated behind `Iris:Seed:RemoteStandIn` (default off); the default sample seeds one honest instance (alice, bob, community) with no fake cross-instance graph. Tests that exercise carla opt in; 2 new tests assert the default excludes carla. Also fixed `CreateWebHostBuilder` to pass the host's resolved config into `ConfigureServices` (per-host `UseConfiguration` was invisible to the seed). → [docs/changes/276](docs/changes/276-31.3-sample-no-fake-remote-seed.md)
4. ~~31.4: **Actors page — directory search should return only actors; rename to "Directory".**~~ **COMPLETE** — the search surface gained an optional `?type` filter (client `SearchOptions.Type`); `type=Actor` runs only the actor pass (no content), a non-actor type filters the content pass by `@type`, and no type keeps the full actor+content search. The `/actors` page passes `Type=Actor` and is renamed "Directory" (nav + `<PageTitle>`); route unchanged. 5 new tests. → [docs/changes/277](docs/changes/277-31.4-directory-actors-only-and-rename.md)
5. ~~31.5: **Actor detail — break up into tabs + profile-style header.**~~ **COMPLETE** — new `ActorProfile` component (avatar + handle + name + summary + IRI; initial fallback when no icon; reuses for 31.6) + the sections are now tabs (Outbox/Liked/Followers/Following/Raw, + a Moderation tab with a count badge once counts load). Header + tab bar always visible; tab switches preserve state; `ActiveTab` resets to Outbox on a new actor. 6 new bUnit tests. → [docs/changes/278](docs/changes/278-31.5-actor-detail-profile-header-and-tabs.md)
6. ~~31.6: **Communities — same treatment as actor detail.**~~ **COMPLETE** — `/community` reuses the 31.5 `ActorProfile` header verbatim (a Group is an Actor) + its one-long page is now tabs (Feed / Members / Followers / Following / Manage / Requests / Moderation / Raw). Header + tab bar always visible; tab switches preserve state; `ActiveTab` resets to Feed on a new community load. Pure client-side reorganization. Full fast suite green (1439/1439). → [docs/changes/279](docs/changes/279-31.6-community-profile-header-and-tabs.md)
7. ~~31.7: **Shared collection browser.**~~ **COMPLETE** — new `CollectionBrowser` component: walks the pages of an ordered collection and renders each item via a registered `ItemTemplate`, or — when none is registered — a built-in basic fallback (actor → detail link + type, object → `/object` link, bare link → `<code>`), plus an `ItemActions` slot for per-row controls (the follower Block button). ActorDetail's hand-rolled paged followers/following lists (~200 lines) are replaced by two `<CollectionBrowser>` calls; outbox/liked/inbox/mutes/blocks/flags + community followers/following/moderation move from `PagedCollection` to it. `PagedCollection` is retained for the non-collection data paths (the followed feed, community feed/members, replies). 7 new bUnit tests. Full fast suite green (1446/1446). → [docs/changes/280](docs/changes/280-31.7-shared-collection-browser.md)
8. ~~31.8: **Server — use the public URI in the namespace + host the namespace document.**~~ **COMPLETE** — the `iris:` extension namespace is now derived from the public base URI as `{BaseUri}/ns#` when `NamespaceIri` is unset (canonical default when `BaseUri` is also unset; an explicit `NamespaceIri` is honored verbatim), and the server hosts the JSON-LD namespace document at `{BaseUri}/ns` (root `GET /ns`, `application/ld+json`, long `Cache-Control`) so the advertised `@vocab` is resolvable. The SampleServer derives its namespace + advertised hostname from the same `Iris:*` base. 5 new integration tests; test host pins the canonical namespace by default (`PinDefaultNamespace`) so the existing known-namespace tests stay stable. Full fast suite green (1451/1451). → [docs/changes/281](docs/changes/281-31.8-server-namespace-derivation-and-document.md)
9. ~~31.9: **Followed-feed (`/feed`) is missing items — only 6 of 15 activities returned.**~~ **COMPLETE (verification; no server change)** — 6 integration tests confirm the followed feed returns the followed actors' data, **every activity type** (Create, Announce, Like, Accept, Note), for **local + remote** follows, under **in-memory + file-backed** persistence. The reported drop could not be reproduced in-process: the server's `FeedService.BuildFeedAsync` merges every followed actor's outbox with no per-type filter (de-duplicated by IRI, capped by `FeedOptions.MaxItems`), and the remote-walk path (`FetchRemoteOutboxAsync`) returns a remote followed's full outbox over the wire. Per the user's direction ("we just need to confirm that it does return data and that we can see that data in the sample"), this slice locks the contract with tests rather than changing the (correct) server path; the UI faithfully renders what the server returns. If the live feed still shows fewer items, the residual cause is what the followed actors' outboxes hold live (upstream delivery), not the feed-merge. 6 new tests; full fast suite green (1459/1459). → [docs/changes/282](docs/changes/282-31.9-followed-feed-returns-followed-data-verified.md)
10. ~~31.10: **Remote Likes are not recorded — a remote actor starring a local post leaves the local `liked` collection empty.**~~ **COMPLETE** — confirmed live (Mastodon → Iris, 2026-09-05): RayvenMX (mastodon.world) starred both of alice's posts; Iris accepted each `Like` (202) and stored it in the inbox, but the post's like count stayed at zero. `LikeActivityHandler` recorded the like edge only when the **liker** was a local actor; a **remote** liker's like on a local post was accepted + stored but never surfaced. The fix flips the guard: the edge is now recorded when the **liked object is local** (stored in this instance's object store), regardless of the liker's locality — so a remote actor's like on a local post is surfaced on the object's `/likes` collection + like count (the existing per-object reverse index + endpoint, decision 056 (d) — no new store or endpoint). A like of a *remote* object is still not recorded locally (the edge lives on the object's author's home instance; recording it here would duplicate). The symmetric `Undo(Like)` already removes the edge unconditionally; a two-instance integration test confirms the edge is recorded on the object's home instance, the `/likes` collection carries it over the wire, and the Undo removes it. 3 new unit tests (remote-liker-of-local-object records; remote-liker-of-remote-object doesn't; the local-liker tests now seed a local object); the `UndoLike` integration test now asserts the B-side edge + `/likes` count + Undo removal. Full fast suite green (1459/1459). → [docs/changes/283](docs/changes/283-31.10-remote-likes-recorded-on-local-objects.md)

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 31.10: **remote Likes are recorded on local objects** — `LikeActivityHandler` now records the like edge when the **liked object is local** (stored in this instance's object store), regardless of the liker's locality, so a remote actor's like on a local post is surfaced on the object's `/likes` collection + like count (the existing per-object reverse index + endpoint, decision 056 (d) — no new store or endpoint). A like of a *remote* object is still not recorded locally (the edge lives on the object's author's home instance; recording it here would duplicate). The symmetric `Undo(Like)` already removes the edge unconditionally; a two-instance integration test confirms the edge is recorded on the object's home instance, the `/likes` collection carries it over the wire, and the Undo removes it. 3 new unit tests; the `UndoLike` integration test now asserts the B-side edge + `/likes` count + Undo removal. Full fast suite green (1459/1459). → [docs/changes/283-31.10-remote-likes-recorded-on-local-objects.md](docs/changes/283-31.10-remote-likes-recorded-on-local-objects.md)
- 31.9: **followed feed (`/feed`) returns the followed actors' data — verification (no server change)** — 6 integration tests confirm the followed feed returns the followed actors' data, **every activity type** (Create, Announce, Like, Accept, Note), for **local + remote** follows, under **in-memory + file-backed** persistence. The reported live drop could not be reproduced in-process: the server's `FeedService` merges every followed actor's outbox with no per-type filter (de-duplicated by IRI, capped by `FeedOptions.MaxItems`), and the remote-walk path returns a remote followed's full outbox over the wire. Per the user's direction this slice locks the contract with tests rather than changing the (correct) server path. 6 new tests; full fast suite green (1459/1459). → [docs/changes/282-31.9-followed-feed-returns-followed-data-verified.md](docs/changes/282-31.9-followed-feed-returns-followed-data-verified.md)
- 31.8: **server — derive the `iris:` extension namespace from the public base URI + host the namespace document** — when `NamespaceIri` is unset the namespace is derived as `{BaseUri}/ns#` (canonical default when `BaseUri` is also unset; an explicit `NamespaceIri` is honored verbatim), and the server hosts the JSON-LD namespace document at `{BaseUri}/ns` (root `GET /ns`, `application/ld+json`, long `Cache-Control`) so the advertised `@vocab` is resolvable rather than a dangling IRI. The SampleServer derives its namespace + advertised hostname from the same `Iris:*` base. 5 new integration tests; the test host pins the canonical namespace by default (`PinDefaultNamespace`) so the existing known-namespace tests stay stable. Full fast suite green (1451/1451). → [docs/changes/281-31.8-server-namespace-derivation-and-document.md](docs/changes/281-31.8-server-namespace-derivation-and-document.md)
- 31.7: **shared collection browser** — new `CollectionBrowser` component walks the pages of an ordered collection and renders each item via a registered `ItemTemplate`, or — when none is registered — a built-in basic fallback (actor → detail link + type, object → `/object` link, bare link → `<code>`); an `ItemActions` slot supplies per-row controls (the follower Block button). ActorDetail's hand-rolled paged followers/following lists are replaced by two `<CollectionBrowser>` calls; outbox/liked/inbox/mutes/blocks/flags + community followers/following/moderation move from `PagedCollection` to it. `PagedCollection` retained for the non-collection data paths (followed feed, community feed/members, replies). 7 new bUnit tests. Full fast suite green (1446/1446). → [docs/changes/280-31.7-shared-collection-browser.md](docs/changes/280-31.7-shared-collection-browser.md)
- 31.6: **communities — profile-style header + tabs** — `/community` reuses the 31.5 `ActorProfile` header verbatim (a Group is an Actor) + the one-long page is now tabs (Feed / Members / Followers / Following / Manage / Requests / Moderation / Raw). Header + tab bar always visible; tab switches preserve state. Pure client-side reorganization (no data/server change). Full fast suite green (1439/1439). → [docs/changes/279-31.6-community-profile-header-and-tabs.md](docs/changes/279-31.6-community-profile-header-and-tabs.md)
Rolling window of the last ~2 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
