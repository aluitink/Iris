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

**29.3 (follow-up) — convert the mutating + federation test classes to the shared-host fixture.** The `SharedHostFixture` (per-xunit-collection, build-the-host-once) landed in 29.3 + the two cleanest read-only single-instance classes were converted (measured: Iris.Server.Tests fast run 5m37s → ~5m16s, ~6%). The **remaining** work: convert the 11 RISKY single-instance classes (they write to persistence + assert absolute counts — they need a **per-method persistence reset** via `IAsyncLifetime`/reseed before they can share a host) and the two-instance federation classes (their shared-fixture shape is more involved — cross-wired delivery transports). This is the larger, higher-risk half of the host-reuse win (host construction is only ~11% of the run, so the ceiling is modest — see [docs/changes/254](docs/changes/254-29.3-shared-host-per-collection.md) for the measurement + which classes are SAFE/RISKY).

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

- 29.3: **shared-host-per-collection test fixture (test-suite speed)** — investigated + measured the host-reuse win before building it: per-host construction is ~89ms (single) / ~221ms (two-instance pair), and the **total** construction budget across Iris.Server.Tests is only ~38s of the ~337s fast run (~11% — the rest is per-method test-body work host-reuse can't reduce), so it's a real but **bounded** win. Added `SharedHostFixture` (in `Iris.Testing`, builds the host once per xunit collection instead of once per method) and converted the two cleanest single-instance, read-only classes (`CollectionEndpointIntegrationTests` + `GlobalSearchIntegrationTests`, 9 methods each). Measured: Iris.Server.Tests fast run 5m37s → ~5m16s (~6%). The 11 mutating single-instance classes (need a per-method persistence reset before they can share a host) + the two-instance federation classes are the **29.3 follow-up** (the larger, higher-risk half). → [docs/changes/254-29.3-shared-host-per-collection.md](docs/changes/254-29.3-shared-host-per-collection.md)
- 29.2: **visual review of all sample pages (layout/UX fixes)** — screenshot every `SampleBlazorClient` page (logged-in, two-instance docker stack) and fixed six layout/UX defects, all verified live after a rebuild: (1) tiny compose/reply textareas — added a `textarea` rule to `app.css` (full-width, dark, `resize: vertical`); (2) transient empty-state flash — `PagedCollection` now shows the "Loading…" spinner during the first fetch (`Items = null` instead of `Items = []`) and only shows the empty state after a genuinely empty fetch (guard against spinning forever on an empty collection); (3) Instance page — render a clean `InstanceDisplayName` (strips the sample server's leading `iris-` software prefix so the H1 reads `iris-dev1.luit.ink`, not `iris-iris-dev1.luit.ink`) and drop the redundant description line; the NodeInfo handler now serves a neutral description instead of echoing the name; (4) ObjectView — omit an actor's `name` when it mirrors its `preferredUsername` (seeded actors name themselves by username), so a row reads `alice` once, not `alice alice`; (5) added a `favicon.ico` + `<link>` to remove the 404. Catalogued one **pre-existing** cross-instance feed render issue (stuck on the spinner; the client's page walk is correct, the loop is in the proxy's handling of the collection's self-referencing `first`/page IRIs — code this slice did not touch) and deferred it. → [docs/changes/253-29.2-sample-ui-visual-review.md](docs/changes/253-29.2-sample-ui-visual-review.md)
- 29.1: **functional review of every SampleBlazorClient page (proxy query-string fix)** — walked all 9 pages (Home, Instance, Actors, ActorDetail, ObjectPage, Feed, Compose, Community, Deliver) against the running two-instance docker stack and verified each action end-to-end; found and fixed **one real bug** — the cross-instance read proxy dropped the target IRI's query string (the `{**target}` route value carries only the path), so a paginated read always relayed page 1 and the client's `next`-walk looped forever (the Community feed's 1080+ duplicated items + 429 burst). Fixed in `ProxyHandler` (append `context.Request.QueryString` to the route value before `Iri.TryParse`); added a non-vacuous regression test (proxy a `?page=2` read → assert page 2 is relaid, not page 1). Catalogued 6 UX limitations (session-persistence gap, pre-filled public handle, missing favicon, transient empty-state flash, Unlike learned-id, advertised-IRI vs dial-base mismatch) — deferred to 29.2+. → [docs/changes/252-29.1-sample-ui-functional-review.md](docs/changes/252-29.1-sample-ui-functional-review.md)
- 29.0: **test-suite fast/full run convention (`Category=Slow` trait)** — a small but durable speed/ergonomics win: introduced the `Category=Slow` xunit trait (constants in `Iris.Testing.TestCategories`) and tagged the genuinely-slow tests that wait out a real delivery backoff budget (`DeliveryDeadLetterIntegrationTests`, `DeliveryRetryTests.TransientFailure_WaitsConfiguredBackoff_BetweenRetries`). The everyday loop green-check is now `dotnet test --filter "Category!=Slow"` (fast); the source-of-truth full run is plain `dotnet test`. Verified: fast run excludes exactly the 2 slow tests (898 of 900 Server.Tests) and is green. Honest finding documented in [TESTING.md](docs/reference/TESTING.md#running-the-suite-fast-vs-full): the slow-test exclusion saves only ~1s — the real cost is ~900 test methods each building fresh hosts + driving multi-hop deliveries (xunit builds a fresh class instance per method); the structural fix (shared hosts per collection) is the 29.3 follow-up. → [docs/changes/251-29.0-test-suite-fast-full-run.md](docs/changes/251-29.0-test-suite-fast-full-run.md)
- 28.3: **relay subscription lifecycle (end-to-end lock; Phase 28 closed)** — a test-only lock (no code change; the lifecycle was already implemented across 19.0b.2b (the local relay endpoint) and 28.1 (the `DeliverToRelaysAsync` fan-out)). New `RelayLifecycleIntegrationTests` (1, 2-instance `TestServer`): bob (B) authenticates with Basic auth and subscribes to a relay (`POST /local/v1/u/bob/relays/{target}`, the F-06 edge recorded in `IRelayStore`); bob publishes a `Create` to his outbox → the 28.1 fan-out delivers it to the relay (R stores it); bob unsubscribes (`POST ...?unsubscribe=true`, the edge removed); a subsequent `Create` is NOT fanned out to the de-subscribed relay (R stores nothing for it). Uses the actual local relay endpoint (Basic auth via `IActorCredentialValidator`), not direct store manipulation. Non-vacuous: making `LocalRelayHandler`'s unsubscribe branch a no-op → the test fails at the "B should have removed the F-06 relay subscription edge" assertion. → [docs/changes/250-28.3-relay-lifecycle.md](docs/changes/250-28.3-relay-lifecycle.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
