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

**Phase 25 — federation hardening & robustness (locally-actionable scope CLOSED).** Phase 25 (the in-process federation-hardening phase — delivery retry/backoff on transient failure, the cross-instance `Reject`/declined-follow path, and remaining activity-type undo-propagation coverage) is fully closed on its locally-actionable scope: **25.1** (a test-only lock that the `DeliveryWorker`'s retry loop actually observes the configured exponential backoff between retries), **25.2** (a real code fix + end-to-end lock: `RejectActivityHandler` now mirrors `AcceptActivityHandler`'s G-3 community-follower override, and a new 2-instance test locks the cross-instance `Reject` hop for a person and a community follower), and **25.3** (a test-only lock of the cross-instance person→person `Undo(Follow)` un-follow path; `Undo(Join)` investigated and deferred as non-canonical). All plan docs are exhausted on their locally-actionable scope.

**Phase 26 — federation completeness & remaining invariant locks (CLOSED — 26.1–26.5).** The residual locally-actionable federation gaps are now fully closed: 26.1 (cross-instance `Accept` hop — test-only lock), 26.2 (inbound `Tombstone` object contract — a real code fix), 26.3 (cross-instance `Announce` fan-out — a no-op, already locked by Phase 19.3.3), 26.4 (`Move` key-rotation cache invalidation — test-only lock), and 26.5 (delivery-worker dead-letter store wiring — test-only lock). Only the two live-infrastructure items (a public FQDN, real external Mastodon accounts) — which this environment cannot provision — remain deferred across all phases.

## Active Slice

None in progress — Phase 26 (26.1–26.5) is fully closed on its locally-actionable scope. The next turn should expand the next phase from [docs/ROADMAP.md](docs/ROADMAP.md) or seed new slices.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

> **Phase 27 slices (locally-actionable, in-process 2-instance `TestServer`) — seeded, to be refined as
> each is picked up:**
>
> 1. **27.1 — cross-instance `Update` (profile change) propagation:** lock that when a remote actor updates their profile (name, summary), the `Update` activity federates to the local instance and the cached actor doc is refreshed. Check if `UpdateActivityHandler` already handles this; if so, test-only lock; if not, a real code fix.
> 2. **27.2 — inbound `Move` with `iris:` extension round-trip:** verify that a `Move` activity carrying `iris:`-namespaced extension properties (e.g. `iris:reason`) round-trips correctly across instances — the receiving instance stores the extensions and a subsequent `GET` on the activity returns them.
> 3. **27.3 — cross-instance `Undo(Like)` of a remote object:** verify that undoing a like of a remote object (object IRI on a different instance) federates correctly — the 24.1 fix covered `Like`/`Announce` of remote objects (resolving the object's author for the delivery target); lock that the `Undo(Like)` path also resolves the author correctly.

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

- 26.5: **delivery-worker dead-letter store wiring** — a test-only lock (no code change; the `DeliveryWorker`'s dead-letter store wiring already exists and is unit-tested, but no end-to-end 2-instance test locked the full path). New `DeliveryDeadLetterIntegrationTests` (1, 2-instance `TestServer`): bob (B) follows alice (A); A's `DeliveryWorker` delivers the auto-`Accept` to bob's inbox on B via a failable transport (500); after exhausting the default retry budget (5 attempts, 1s base delay, ~15s total backoff), the job is dead-lettered in A's `IDeliveryDeadLetterStore` with the correct inbox IRI, actor IRI (alice), failure kind (`NonSuccessStatus`), detail (`"500"`), and attempt count (5). Non-vacuous: forcing the "no store" path in `DeadLetterAsync` fails the test (store stays empty, 5 500s served). Key decision: used the default `DeliveryRetryOptions` (5 attempts, 1s base) instead of overriding via `ExtraServices` because `IOptions<T>` caches the first resolved instance and a later `AddSingleton<T>` does not override it. → [docs/changes/244-26.5-delivery-dead-letter-store-wiring.md](docs/changes/244-26.5-delivery-dead-letter-store-wiring.md)
- 26.4: **`Move` key-rotation cache invalidation (F-25)** — a test-only lock (no code change; the `MoveActivityHandler`'s F-25 cache invalidation — clearing the moving actor's `RemoteActorCache[oldIri]` + `RemoteKeyCache[oldIri#key-1]` on an inbound `Move` — already exists and is unit-tested, but no end-to-end 2-instance test locked the full path). New `MoveKeyRotationIntegrationTests` (1, 2-instance `TestServer`): bob (B) follows old alice (A); Phase 1 warms B's `RemoteActorCache`+`RemoteKeyCache` (old-key `Follow` → 202, key-cache count=1); Phase 2 the old alice delivers a `Move` to a new IRI (on A, new key) → B re-points bob's edge AND clears the old IRI's cache entries (key-cache count=1→0); Phase 3 A decommissions the old IRI → an old-key delivery is rejected **401** (B's cache was cleared → re-fetch → 404 → no key) while a new-key delivery is accepted **202** (B resolves the new key via a fresh fetch of the new actor doc). Non-vacuous: disabling the handler's `RemoteKeyCache` invalidation fails the test at Phase 2 (count stays 1, not 0) — the Phase 3 401 is only reachable because the `Move` cleared the stale cached old key. The new IRI is modeled on the same host as the old (a migration to a new path) so B resolves the new key via the *remote* fetch path, not a local lookup. → [docs/changes/243-26.4-move-key-rotation-cache-invalidation.md](docs/changes/243-26.4-move-key-rotation-cache-invalidation.md)
- 26.3: **cross-instance `Announce` (boost) fan-out to a remote author's local followers** — a **no-op** (no new test, no code change): the slice's scenario (a boost of a remote note reaches the remote author's local followers on the receiving instance) is already implemented by the inbound `AnnounceActivityHandler` (it records the boost in the local recipient's outbox via `IActivityStore.AddToOutboxAsync` + `IAnnounceStore.RecordAnnounceAsync`, then propagates to each of the recipient's followers — a *local* follower is recorded directly in their outbox on this instance, a *remote* follower gets a cross-instance delivery — reusing the server-minted id per decision 055) and is already locked end-to-end by Phase 19.3.3's `AnnouncePropagationIntegrationTests` (2, 2-instance `TestServer`): `Boost_LocalNote_ReachesPeerLocalFollower_Once` (alice on A boosts a local note → federated to bob on B → B's handler propagates to carol, bob's local follower; the boost reaches carol's outbox exactly once + no re-announce loop) and `Boost_RemotePeerNote_CarriesObjectLink_NoInfiniteChain` (alice on A boosts **bob's remote note** → federated to B → B's handler propagates to carol; the boost references the remote note by link, is attributed to alice the announcer, and does not chain). That second test is a 1:1 match for the slice's wording (the remote author is bob; bob's local follower carol on B surfaces the boost). Investigation note: a first attempt modeled on a *local* booster (erin on B boosting alice's note on A via the outbox-publish write surface) revealed that the outbox-publish `Announce` branch fans out to **remote** followers only (it does not run the `AnnounceActivityHandler`), so a local author's boost does not reach local followers — the local-follower fan-out lives exclusively on the *inbound* path, which the existing tests already cover. Along the way a latent test-suite footgun was found: a test file's private `LazyHandler` shadows the shared `Iris.Testing.LazyHandler`; a copy that omits the request-clone logic breaks the nested actor-doc fetch the `HttpSignatureValidator` uses to resolve a local key, producing a spurious `401` (the shared `LazyHandler` clones because the in-process transport + retry pipeline forbid re-sending the same `HttpRequestMessage`). → [docs/changes/242-26.3-cross-instance-announce-fanout-noop.md](docs/changes/242-26.3-cross-instance-announce-fanout-noop.md)
- 26.2: **inbound `Tombstone` object contract (F-10)** — a real code fix (not test-only): a peer signals an object deletion by delivering a `Tombstone` — either a **standalone** `Tombstone` (a `IObject`, not an `Activity`) posted to a follower's inbox, or a **`Create`** whose embedded `object` is a `Tombstone`. Pre-fix the standalone form was rejected with `400` (the inbox endpoint accepted only `Activity` or the special-cased `Mute`, so A kept the stale federated copy), and the `Create`-wrapped form was mis-processed (A stored the tombstone but ALSO recorded a bogus outbox `Create` + object→`Create` index entry and re-federated the "new post"). New `TombstoneInbound.ApplyAsync` stores the tombstone under the object IRI (replacing prior content → a `GET` serves the tombstone, not stale/404) and cleans up the local copy (the author's outbox `Create`, the object→`Create` index, and the reply edge) — gated on a prior copy having been stored; wired into both inbound paths (a standalone-`Tombstone` branch in `HandleInboxPostAsync` after the `Activity` check, and a `Create`-wrapped-`Tombstone` branch in `CreateActivityHandler` that returns early before outbox/fan-out/index). New `InboundTombstoneIntegrationTests` (2, 2-instance `TestServer`): bob on B authors a note federated to alice on A (A stores the copy); bob then deletes — the standalone test delivers a signed standalone `Tombstone` to alice's inbox (a `IObject` can't ride `IDeliveryService`, so it's a direct signed POST through the worker's transport), the `Create`-wrapped test delivers a `Create` with an embedded `Tombstone`. The non-vacuous signal is the stored object on A (a `Tombstone`, not the stale `Note`) + the cleaned outbox/object→`Create` index. Proven non-vacuous (disabling the fix → standalone returns `400`, `Create`-wrapped leaves the bogus object→`Create` index). → [docs/changes/241-26.2-inbound-tombstone-object-contract.md](docs/changes/241-26.2-inbound-tombstone-object-contract.md)
- 26.1: **cross-instance `Accept` (follow-acceptance) propagation** — a test-only lock (no code change; the B → A auto-`Accept` path was already fully implemented — the followed side's `FollowActivityHandler` auto-constructs the `Accept` and the `DeliveryWorker` delivers it, and the follower's home instance stores it + `AcceptActivityHandler` finalizes the edge — but no 2-instance test locked the cross-instance `Accept` hop). New `CrossInstanceAcceptPropagationIntegrationTests` (2, 2-instance `TestServer`, modeled on 25.2's `Reject` test): a follower (person `alice` / community `iris`) on A follows a target (`bob` / community `lumen`) on B (A records the provisional edge at publish; B records + stores the `Follow`); B's `FollowActivityHandler` auto-`Accept` builds + delivers the `Accept` (actor = the target, object = the minted follow id) to the follower's inbox on A, and A stores it. The non-vacuous signal is the stored `Accept` in A's activity store (the follow edge is recorded at publish, so it is not a usable cross-instance signal); the community case also locks the `AcceptActivityHandler`'s G-3 community-follower override. Proven non-vacuous (disabling B's auto-`Accept` emission fails both tests). → [docs/changes/240-26.1-cross-instance-accept-propagation.md](docs/changes/240-26.1-cross-instance-accept-propagation.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
