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

**Phase 29 — sample-UI functional + visual review (closed).** 29.0 (fast/full test run convention), 29.1 (functional review — found + fixed cross-instance read proxy query-string drop), 29.2 (visual review — six layout/UX fixes), 29.3 (shared-host-per-collection fixture + converted all 13 single-instance RISKY + 19 two-instance + 3 three-instance federation classes — measured ~12% suite speedup: 5m36s → 4m56s).

## Active Slice

**Phase 30 — server production-readiness & hardening.** 30.1 (configuration surface) complete — `AddActivityPubServer(IServiceCollection, IConfiguration)` binds all options from conventional config sections. Next: 30.2 (health check + readiness probe).

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

*(none currently)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 30.1: **configuration surface** — `AddActivityPubServer(IServiceCollection, IConfiguration)` binds `ActivityPubServerOptions` + delivery/inbound/feed/health options from `Iris:*` config sections. 9 integration tests. Full fast suite green (908/908, 6m11s). → [docs/changes/273-30.1-configuration-surface.md](docs/changes/273-30.1-configuration-surface.md)
- 29.3 (follow-up, partial 17): **1 three-instance federation class** — converted `OutboxCreateFanOutIntegrationTests` (3, A identity + RoutingFetcher + RoutingHandler delivery to B/C by host, B/C fetchers reach A lazy, bob→alice + carol→alice follow edges on A). **All 13 single-instance RISKY + 19 two-instance + 3 three-instance federation classes converted. 29.3 follow-up COMPLETE. Phase 29 closed.** Full fast suite green (899/899, 4m56s). → [docs/changes/271-29.3-outbox-create-fanout.md](docs/changes/271-29.3-outbox-create-fanout.md)
- 29.3 (follow-up, partial 16): **1 three-instance federation class** — converted `OutboxAudienceMetadataIntegrationTests` (3, A identity + RoutingFetcher + RoutingHandler delivery to B/C by host, B/C fetchers reach A lazy, bob→alice follow edge on A, carol non-follower + parent note on A's object store for reply test). Full fast suite green (899/899, 5m4s). → [docs/changes/270-29.3-outbox-audience-metadata.md](docs/changes/270-29.3-outbox-audience-metadata.md)
- 29.3 (follow-up, partial 15): **SharedThreeHostFixture + 1 three-instance federation class** — created `SharedThreeHostFixture` (3 hosts, per-method reset, ServerRegistry); converted `OutboxAudienceMatchIntegrationTests` (2). Full fast suite green (899/899, 5m36s). → [docs/changes/269-29.3-outbox-audience-match.md](docs/changes/269-29.3-outbox-audience-match.md)
- 29.3 (follow-up, partial 14): **1 two-instance federation class** — converted `AnnouncePropagationIntegrationTests` (2, DeliveryCounter via Lazy<T>). Full fast suite green (899/899, 5m). → [docs/changes/268-29.3-announce-propagation.md](docs/changes/268-29.3-announce-propagation.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
