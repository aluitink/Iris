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

## Now

**Phase 27 — protocol conformance & edge-case hardening (closed).** 27.1 (cross-instance `Update` propagation — a real code fix: wired the outbox-publish `Update` branch to `UpdateActivityHandler` + tightened the owner guard to require `attributedTo` for all actors), 27.2 (inbound `Move` with `iris:` extension round-trip — test-only lock: the ActivityStreams `ExtensionData` already preserves `iris:` extensions through the wire round-trip), and 27.3 (cross-instance `Undo(Like)` of a remote object — a no-op: already locked by Phase 24.1's `LikeAnnounceUndoPropagationIntegrationTests`) are done.

## Active Slice

None — Phase 27 is closed.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

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

- 27.3: **cross-instance `Undo(Like)` of a remote object (no-op)** — a **no-op** (no new test, no code change): the slice's scenario (undoing a like of a remote object federates correctly, with the 24.1 delivery-target resolution applied to the `Undo` path) is already locked end-to-end by Phase 24.1's `LikeAnnounceUndoPropagationIntegrationTests.UndoLike_ServerDeliversToRemote_StoredOnRemoteInstance` (2-instance `TestServer`): alice (A) likes bob's remote Note (B) → A's `RecordLikeLocalAsync` resolves the Note's author via `ResolveObjectOwnerForDeliveryAsync` (the 24.1 fix) and delivers the signed `Like` to bob's inbox on B → B stores the `Like`; alice then publishes an `Undo(Like)` → A's `RecordUndoLocalAsync` → `RemoveLikeLocalAsync` removes the local edge and re-resolves the author via the same 24.1 path → A delivers the signed `Undo` to bob's inbox on B → B stores the `Undo` (its `UndoActivityHandler` resolves the original `Like` from B's activity store). Both tests pass (2/2 green). → [docs/changes/247-27.3-cross-instance-undo-like-noop.md](docs/changes/247-27.3-cross-instance-undo-like-noop.md)
- 27.2: **inbound `Move` with `iris:` extension round-trip** — a test-only lock (no code change; the ActivityStreams library's `ExtensionData` already preserves `iris:`-namespaced extension properties through the wire round-trip). New `MoveExtensionRoundTripIntegrationTests` (1, 2-instance `TestServer`): alice (A) migrates to a new IRI on B and delivers a `Move` carrying an `iris:reason` extension to bob's inbox on B; B validates the signature, stores the `Move`, and re-points bob's follow edge; the stored `Move` preserves the `iris:reason` extension (value `domain migration`). Non-vacuous: if the library dropped `ExtensionData` during serialize/deserialize, the stored `Move` would not have the `iris:reason` key and the test would fail. → [docs/changes/246-27.2-inbound-move-iris-extension-round-trip.md](docs/changes/246-27.2-inbound-move-iris-extension-round-trip.md)
- 27.1: **cross-instance `Update` (object edit) propagation** — a real code fix + 3 integration tests: (1) wired the outbox-publish `Update` branch to `UpdateActivityHandler` (mirroring the `Delete` branch; previously an `Update` published to the outbox fell into the catch-all with no delivery), and (2) tightened the owner guard to require `attributedTo` for **all** actors (local and remote), not just remote — a local actor can no longer forge an `Update` to a remote actor's federated copy. New `UpdatePropagationIntegrationTests` (3, 2-instance `TestServer`): cross-instance Update federates to follower and refreshes the stored copy; local-only Update (no remote followers) refreshes locally; non-owner Update (local actor forging a remote object) is rejected. Non-vacuous: disabling the outbox-publish `Update` branch → the cross-instance test fails (A keeps original content); reverting the owner guard to the old `!actorIsLocal &&` form → the non-owner test fails (forge succeeds). → [docs/changes/245-27.1-cross-instance-update-propagation.md](docs/changes/245-27.1-cross-instance-update-propagation.md)
- 26.4: **`Move` key-rotation cache invalidation (F-25)** — a test-only lock (no code change; the `MoveActivityHandler`'s F-25 cache invalidation — clearing the moving actor's `RemoteActorCache[oldIri]` + `RemoteKeyCache[oldIri#key-1]` on an inbound `Move` — already exists and is unit-tested, but no end-to-end 2-instance test locked the full path). New `MoveKeyRotationIntegrationTests` (1, 2-instance `TestServer`): bob (B) follows old alice (A); Phase 1 warms B's `RemoteActorCache`+`RemoteKeyCache` (old-key `Follow` → 202, key-cache count=1); Phase 2 the old alice delivers a `Move` to a new IRI (on A, new key) → B re-points bob's edge AND clears the old IRI's cache entries (key-cache count=1→0); Phase 3 A decommissions the old IRI → an old-key delivery is rejected **401** (B's cache was cleared → re-fetch → 404 → no key) while a new-key delivery is accepted **202** (B resolves the new key via a fresh fetch of the new actor doc). Non-vacuous: disabling the handler's `RemoteKeyCache` invalidation fails the test at Phase 2 (count stays 1, not 0) — the Phase 3 401 is only reachable because the `Move` cleared the stale cached old key. The new IRI is modeled on the same host as the old (a migration to a new path) so B resolves the new key via the *remote* fetch path, not a local lookup. → [docs/changes/243-26.4-move-key-rotation-cache-invalidation.md](docs/changes/243-26.4-move-key-rotation-cache-invalidation.md)
- 26.3: **cross-instance `Announce` (boost) fan-out to a remote author's local followers** — a **no-op** (no new test, no code change): the slice's scenario (a boost of a remote note reaches the remote author's local followers on the receiving instance) is already implemented by the inbound `AnnounceActivityHandler` and is already locked end-to-end by Phase 19.3.3's `AnnouncePropagationIntegrationTests` (2, 2-instance `TestServer`). → [docs/changes/242-26.3-cross-instance-announce-fanout-noop.md](docs/changes/242-26.3-cross-instance-announce-fanout-noop.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
