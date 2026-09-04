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

**Phase 26 — federation completeness & remaining invariant locks (defined this turn; in progress).** The residual locally-actionable federation gaps, seeded in **Up Next** below: the cross-instance `Accept` (follow-acceptance) hop (26.1, the inverse of 25.2's `Reject` lock), the inbound `Tombstone` object contract (26.2, a possible small code fix), the cross-instance `Announce` (boost) fan-out to a remote author's local followers (26.3), the `Move` key-rotation cache invalidation (26.4, F-25), and the delivery-worker dead-letter store wiring into a real 2-instance topology (26.5). Only the two live-infrastructure items (a public FQDN, real external Mastodon accounts) — which this environment cannot provision — remain deferred across all phases.

## Active Slice

None in progress — the next turn picks the top item from **Up Next** below (Phase 26 slice 26.1: cross-instance `Accept` propagation).

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

> **Phase 23 status (CLOSED — 22.11–22.14):** Phase 23 (broader federation maturity & live-account
> remediation, per [phase-22-closeout.md §5](docs/plans/phase-22-closeout.md#5-future-follow-on-work)) is
> now fully closed on its *locally-actionable* scope: 22.11 (moderation-collection cache invalidation),
> 22.12 (Mastodon / draft-10 `Digest` header casing locked with tests), 22.13 (cross-instance moderation
> **undo** propagation locked with tests, proven non-vacuous), and 22.14 (cross-instance community
> **un-follow** propagation locked with tests, proven non-vacuous). Only the two live-infrastructure items
> (a public FQDN, real external Mastodon accounts) — which this environment cannot provision — remain
 > deferred. Phase 24 (federation robustness & remaining cross-instance propagation gaps) is now fully
 > closed on its locally-actionable scope (24.1–24.4). Phase 25 (federation hardening & robustness) is now
 > fully closed on its locally-actionable scope (25.1–25.3). Phase 26 (federation completeness & remaining
 > invariant locks) is now active.

  > **Phase 26 slices (locally-actionable, in-process 2-instance `TestServer`) — seeded, to be refined as
  > each is picked up:**
  >
  1. **26.1 — cross-instance `Accept` (follow-acceptance) propagation:** lock the B → A `Accept` hop end-to-end (the inverse of 25.2's `Reject` lock — the more common follow flow): alice on A follows bob on B (B records the edge + stores the `Follow` + server-delivers the signed `Accept` to alice's inbox on A); A's `AcceptActivityHandler` finalizes alice's local follow edge. Proven non-vacuous (disabling the `Accept` delivery or the handler fails the test).
  2. **26.2 — inbound `Tombstone` object contract (F-10):** a peer may deliver a `Tombstone` directly (a standalone `Tombstone` or a `Create`/object whose embedded body is a `Tombstone`) when an object is deleted on the peer's instance; ensure such an inbound `Tombstone` is recognized and stored under the object IRI (so a subsequent `GET` serves the tombstone, not stale content or a 404) — a small code fix if `CreateActivityHandler`/the object endpoint do not yet intercept it, locked with a 2-instance test.
  3. **26.3 — cross-instance `Announce` (boost) fan-out to a remote author's local followers:** lock that a boost of a remote note reaches the remote author's local followers on the receiving instance (erin on B boosts a federated note by alice on A; B's `AnnounceActivityHandler` propagates the boost to alice's local followers on B, e.g. bob; bob's outbox on B surfaces the boost). Proven non-vacuous.
  4. **26.4 — `Move` key-rotation cache invalidation (F-25):** lock the full `Move` path — alice migrates from an old IRI (A) to a new IRI (B) with a new key; B re-points the follow edge AND clears alice's cached key/actor; a subsequent delivery signed as the new alice (new key) is validated by B by fetching the new actor document (not the stale cached one). Proven non-vacuous (a stale key cache would 401 the new signature).
  5. **26.5 — delivery-worker dead-letter store wiring into a real topology:** lock that a failed cross-instance delivery (e.g. B's inbox returns a 5xx) is dead-lettered in the `InMemoryDeliveryDeadLetterStore` with the correct inbox IRI, activity IRI, actor IRI, attempt count, and failure kind — wiring the store into a real 2-instance `TestServer` (the existing `DeliveryRetryTests` construct the worker explicitly, so a wiring regression would keep them green). Proven non-vacuous.
  >
  > **Deferred (live-infrastructure, not provisionable in this environment):**
 >
 6. External-FQDN verification — resolver reachability, public navigation, browser verification over live hostnames. (Needs a reachable public FQDN / reverse proxy; deferred.) See [phase-22-closeout.md §1](docs/plans/phase-22-closeout.md#1-external-fqdn-verification).
 7. Live federation/interop checks against public Mastodon accounts (follow, post/receive, signatures, pagination, community flows). (Needs real external accounts; deferred.) See [phase-22-closeout.md §2](docs/plans/phase-22-closeout.md#2-live-federation-and-interop-checks).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(none currently)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 25.3: **cross-instance person→person `Undo(Follow)` (un-follow) propagation** — a test-only lock (no code change; the path was already fully implemented — A's `RecordUndoLocalAsync`→`RemoveFollowLocalAsync` removes the follower's local edge + returns the target IRI, and B's `UndoActivityHandler` `recipient==target` arm resolves the stored `Follow` and removes the edge) of the one follow-flavored undo path still untested end-to-end: a plain **person → person** un-follow (the most common real-federation flow — only the community-initiated un-follows were locked, and the single-instance test collapses both sides into one store). New `PersonFollowsPersonUnfollowPropagationIntegrationTests` (2, 2-instance `TestServer`): alice on A follows bob on B (A records its local edge + B records the edge + stores the `Follow`); alice publishes `Undo(Follow)` and A removes its edge + B removes its edge (alice no longer in bob's followers). Proven non-vacuous (disabling B's target-side removal fails the un-follow test while the forward follow test still passes). `Undo(Join)` investigated and **deferred** (non-canonical — the standard membership departure is the standalone `Leave`, already covered cross-instance). → [docs/changes/239-25.3-person-person-undo-follow-propagation.md](docs/changes/239-25.3-person-person-undo-follow-propagation.md)
- 25.2: **cross-instance `Reject` (declined follow) propagation** — a real code fix + invariant lock (not test-only): the inbound `RejectActivityHandler` was asymmetric with `AcceptActivityHandler` — it handled only a *person* follower and had **no G-3 community-follower override**, so a community's declined follow was never undone on the community side (the follows set kept listing a rejected follow), and no 2-instance test locked the cross-instance `Reject` hop at all. Fixed `RejectActivityHandler` to mirror `AcceptActivityHandler`: `IsLocalRecipientAsync` now widens the local check to a local community, and `ApplyAsync` removes a community's rejected follow from its follows set (`ICommunityStore.RemoveFollowAsync`) vs. a person's edge from the follow store. New `CrossInstanceRejectPropagationIntegrationTests` (2, 2-instance `TestServer`): a follower (person `alice` / community C) on A follows `bob` on B (B records the remote edge + stores the `Follow`); bob publishes `Reject`, B server-delivers it to the follower's inbox on A, and A's handler removes A's follow edge. Proven non-vacuous (disabling the G-3 override fails the community test while the person test still passes). → [docs/changes/238-25.2-cross-instance-reject-propagation.md](docs/changes/238-25.2-cross-instance-reject-propagation.md)
- 25.1: **delivery retry observes the configured backoff** — a test-only lock (no code change): the `DeliveryWorker` (F-22) already retries a failed delivery with exponential backoff, but every existing `DeliveryRetryTests` outcome test sets `BaseDelay = 0`, so none locked that the worker *actually observes* a non-zero delay between retries (a regression removing the `Task.Delay` would keep all green). New `TransientFailure_WaitsConfiguredBackoff_BetweenRetries` (1, drives a real worker against a failable transport with a real 150ms `BaseDelay`, 500/500/200 recovery): asserts total elapsed `>= 250ms` (two backoffs) and the gap before the 2nd attempt `>= 100ms` — proven non-vacuous (`BaseDelay = 0` → observed 69ms → fail). `FailableHandler` gains an optional timestamp-tracking mode. → [docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md](docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md)
- 24.4: **cross-instance community→community un-follow propagation** — locked with tests (no code change): a community C on A follows a community D on B (federates A → B; B's `FollowActivityHandler` community branch, F-24, records **both** of D's edges — D's follows set D → C and D's followers set C → D — and stores the original `Follow`), then C's `Undo(Follow)` federates A → B and B's `UndoActivityHandler` community-target arm removes both of D's edges. New `CommunityFollowsCommunityUnfollowPropagationIntegrationTests` (2, 2-instance `TestServer`) — proven non-vacuous (disabling the remote community-target removal fails the unfollow test while the forward follow test still passes). Closes Phase 24's locally-actionable scope. → [docs/changes/236-24.4-cross-instance-community-community-unfollow-propagation.md](docs/changes/236-24.4-cross-instance-community-community-unfollow-propagation.md)
- 24.3: **idempotent handling of duplicate inbound deliveries** — a real federation peer may redeliver the same signed activity (on timeout/retry/retransmission), so the receiving instance must treat a repeat as a no-op. Locked the C-07 inbox-Id dedup guard (store add-if-absent + skip handler re-dispatch on a re-delivery) for an *edge-recording* activity with `DuplicateInboundDeliveryIdempotencyIntegrationTests` (2, 2-instance `TestServer`): a redelivered `Block` records the block edge exactly once and the second delivery is accepted as a no-op (202, not 500); a redelivered `Follow` records the follow edge exactly once **and** emits exactly one `Accept` to the follower (the `FollowActivityHandler` mints a fresh `Accept` id per dispatch, so the "exactly one Accept" assertion is the non-vacuous proof the handler runs exactly once — the idempotent store edge alone would stay one even if the handler ran twice). Proven non-vacuous: disabling the C-07 early-return makes the `Follow` test observe **two** distinct `Accept`s in the follower's inbox (guard on → one). → [docs/changes/235-24.3-duplicate-inbound-delivery-idempotency.md](docs/changes/235-24.3-duplicate-inbound-delivery-idempotency.md)

Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
