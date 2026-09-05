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

**Phase 29 — sample-UI functional + visual review (in progress).** The user-facing surface is getting its first full functional + visual pass (per user direction — the real gaps are in the sample explorer, not more protocol edge cases). 29.0 (test-suite fast/full run convention — `Category=Slow` trait), 29.1 (functional review of every page — found + fixed the cross-instance read proxy's query-string drop, which looped paginated reads forever), 29.2 (visual review of all pages — six layout/UX fixes), and 29.3 (shared-host-per-collection fixture + converted the two cleanest read-only classes — measured ~6% win) are done. The 29.3 follow-up (convert the mutating + federation classes) is the active slice.

## Active Slice

**29.3 (follow-up) — convert the mutating + federation test classes to the shared-host fixture.** The `SharedHostFixture` + per-method reset + `ServerRefFor` are in place. **Nine RISKY single-instance classes converted so far** (across three partials): Community (11), Object (11), CommunityModeration (12), CommunitySearch (9), CommunityOutboxCollection (5), CommunityFeedCorrectness (4), InteractionCollection (9), MutesCollection (8), RelaysCollection (8). Full fast suite green (899/899, 5m31s). **remaining:** (a) ~4 RISKY single-instance classes with signature validation (Blocks, CommunityJoinRequest, Flags, Reply — need key-preservation on reseed: use `SeedPerson` + manual `publicKey` setup instead of `SeedPersonWithKey`); (b) two-instance federation classes (~20 — cross-wired delivery transports, reset covers both instances). Host construction is only ~11% of the run, so the ceiling is modest — see [docs/changes/257](docs/changes/257-29.3-basic-auth-risky-classes.md).

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

**Phase 29 — sample-UI functional + visual review** (the user-facing surface; the real gaps per user direction, not more protocol edge cases):

1. 29.1: **functional review of every SampleBlazorClient page** — spin up `SampleServer` + `SampleBlazorClient`, walk each page (Home, Actors, ActorDetail, Feed, ObjectPage, Community, Compose, Instance, Deliver) against a seeded server, and verify each page's actions actually work end-to-end (log on, browse, follow, like, boost, compose, navigate cross-instance). Fix any broken flows found.
2. 29.2: **visual review of all sample pages** — screenshot every page (logged-out + logged-in, empty + populated), find layout/UX issues (broken layout, missing states, confusing labels, overflow), and fix them.
3. 29.3: **test-suite speed structural follow-up** — the `Category=Slow` fast/full convention is in; the bigger win is reusing in-process hosts across a test class's methods. **Done (254):** `SharedHostFixture` (per-collection, build-the-host-once) + converted the two cleanest read-only single-instance classes (measured ~6% win). **Remaining (active slice):** convert the 11 mutating single-instance classes (need a per-method persistence reset) + the two-instance federation classes (more involved shared-fixture shape).

> **Deferred (live-infrastructure, not provisionable in this environment):**
>
> 4. External-FQDN verification — resolver reachability, public navigation, browser verification over live hostnames. (Needs a reachable public FQDN / reverse proxy; deferred.) See [phase-22-closeout.md §1](docs/plans/phase-22-closeout.md#1-external-fqdn-verification).
> 5. Live federation/interop checks against public Mastodon accounts (follow, post/receive, signatures, pagination, community flows). (Needs real external accounts; deferred.) See [phase-22-closeout.md §2](docs/plans/phase-22-closeout.md#2-live-federation-and-interop-checks).



## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(none currently)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 29.3 (follow-up, partial 3): **Basic-auth RISKY classes with client rebuild** — converted `MutesCollectionIntegrationTests` (8) + `RelaysCollectionIntegrationTests` (8) to the shared host. Pattern: fixture builds host once (initial key for self-fetcher + `CredentialValidator`); test class rebuilds `_client`/`_local` in `InitializeAsync` using reseeded keys (non-readonly fields). Works because these use Basic auth (not signature validation), so the key-replacement issue doesn't apply. **Nine RISKY single-instance classes converted total.** Full fast suite green (899/899, 5m31s). `BlocksCollection` attempted + reverted (signature validation — needs key-preservation on reseed). Remaining: ~4 signature-validation RISKY classes + federation classes. → [docs/changes/257-29.3-basic-auth-risky-classes.md](docs/changes/257-29.3-basic-auth-risky-classes.md)
- 29.3 (follow-up, partial 2): **3 clean RISKY classes + ServerRefFor + InteractionCollection** — converted `CommunitySearchIntegrationTests` (9), `CommunityOutboxCollectionIntegrationTests` (5, methods seed themselves — reset only), `CommunityFeedCorrectnessIntegrationTests` (4) to the shared host with per-method reset. Added `SharedHostFixture.ServerRefFor` (a static `ConcurrentDictionary<IPersistenceProvider, TestServer>` deferred lookup) so a self-fetcher / signed client wired during host construction can resolve the server at request time; converted `InteractionCollectionIntegrationTests` (9, a self-fetcher class) using it. **Seven RISKY single-instance classes converted total.** Full fast suite green (899/899, 5m51s). Remaining: ~6 RISKY classes with custom clients + federation classes. → [docs/changes/256-29.3-clean-risky-classes-serverref.md](docs/changes/256-29.3-clean-risky-classes-serverref.md)
- 29.3 (follow-up, partial): **per-method reset mechanism + 3 RISKY classes converted** — added the missing piece that blocks the mutating classes from sharing a host: `Clear()` on all 12 in-memory stores + `InMemoryPersistenceProvider.Reset()`, `Clear()` on `MemoryCache`/`CachingReadThrough`/`LocalCollectionPageCache` (the collection-page response cache was leaking between methods on a shared host — the root cause of the first conversion failure), and `SharedHostFixture.Reset()` (clears persistence + the cache, leaves the key store intact so the host's signer keeps working). Converted 3 RISKY single-instance classes (`CommunityEndpointIntegrationTests` 11, `ObjectEndpointIntegrationTests` 11, `CommunityModerationIntegrationTests` 12) using `IAsyncLifetime.InitializeAsync` (reset + reseed before each method). Full fast suite green (899/899). Remaining: 8 RISKY + federation classes. → [docs/changes/255-29.3-per-method-reset-mutating-classes.md](docs/changes/255-29.3-per-method-reset-mutating-classes.md)
- 29.3: **shared-host-per-collection test fixture (test-suite speed)** — investigated + measured the host-reuse win before building it: per-host construction is ~89ms (single) / ~221ms (two-instance pair), and the **total** construction budget across Iris.Server.Tests is only ~38s of the ~337s fast run (~11% — the rest is per-method test-body work host-reuse can't reduce), so it's a real but **bounded** win. Added `SharedHostFixture` (in `Iris.Testing`, builds the host once per xunit collection instead of once per method) and converted the two cleanest single-instance, read-only classes (`CollectionEndpointIntegrationTests` + `GlobalSearchIntegrationTests`, 9 methods each). Measured: Iris.Server.Tests fast run 5m37s → ~5m16s (~6%). The 11 mutating single-instance classes (need a per-method persistence reset before they can share a host) + the two-instance federation classes are the **29.3 follow-up** (the larger, higher-risk half). → [docs/changes/254-29.3-shared-host-per-collection.md](docs/changes/254-29.3-shared-host-per-collection.md)
- 29.2: **visual review of all sample pages (layout/UX fixes)** — screenshot every `SampleBlazorClient` page (logged-in, two-instance docker stack) and fixed six layout/UX defects, all verified live after a rebuild: (1) tiny compose/reply textareas — added a `textarea` rule to `app.css` (full-width, dark, `resize: vertical`); (2) transient empty-state flash — `PagedCollection` now shows the "Loading…" spinner during the first fetch (`Items = null` instead of `Items = []`) and only shows the empty state after a genuinely empty fetch (guard against spinning forever on an empty collection); (3) Instance page — render a clean `InstanceDisplayName` (strips the sample server's leading `iris-` software prefix so the H1 reads `iris-dev1.luit.ink`, not `iris-iris-dev1.luit.ink`) and drop the redundant description line; the NodeInfo handler now serves a neutral description instead of echoing the name; (4) ObjectView — omit an actor's `name` when it mirrors its `preferredUsername` (seeded actors name themselves by username), so a row reads `alice` once, not `alice alice`; (5) added a `favicon.ico` + `<link>` to remove the 404. Catalogued one **pre-existing** cross-instance feed render issue (stuck on the spinner; the client's page walk is correct, the loop is in the proxy's handling of the collection's self-referencing `first`/page IRIs — code this slice did not touch) and deferred it. → [docs/changes/253-29.2-sample-ui-visual-review.md](docs/changes/253-29.2-sample-ui-visual-review.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
