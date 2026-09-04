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

**Phase 25 — federation hardening & robustness (in progress).** Phase 24's locally-actionable scope is fully closed (24.1–24.4). Phase 25 is the next in-process federation-hardening phase (delivery retry/backoff on transient failure, the cross-instance `Reject`/declined-follow path, and remaining activity-type undo-propagation coverage). **Slice 25.1 is done** — a test-only lock that the `DeliveryWorker`'s retry loop actually observes the configured exponential backoff between retries (the first test in the suite with a real, non-zero `BaseDelay`; the existing outcome tests all used `BaseDelay = 0`, so none locked the wait itself). The next slice is **25.2** (cross-instance `Reject`/declined-follow propagation), seeded in **Up Next** below. All plan docs are exhausted on their locally-actionable scope; only the two live-infrastructure items (a public FQDN, real external Mastodon accounts) — which this environment cannot provision — remain deferred.

## Active Slice

None in progress — the next turn picks the top item from **Up Next** below.

<!-- When a slice is in flight, replace the line above with:
- **Slice:** <title>
- **Definition of done:** <the specific checklist for *this* slice — build clean, tests green, plus anything slice-specific>
- **Remaining steps:** <what's left, if resumed mid-slice>
-->

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
 > active.

 > **Phase 25 slices (locally-actionable, in-process 2-instance `TestServer`) — seeded, to be refined in
 > the next turn:**
 >
  1. ~~**25.1 — delivery retry/backoff on transient failure**~~ — **done** (test-only lock: the worker's retry loop observes the configured exponential backoff between retries; `BaseDelay = 0` in the pre-existing tests is why no test locked the wait itself). → [docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md](docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md)
 2. **25.2 — cross-instance `Reject` (declined follow) propagation:** a community/person on A follows an actor/community on B (federates A → B), then B's operator **Rejects** the follow (a `Reject` authored to B's outbox); the `Reject` federates B → A and A records the follow as declined (the inverse of the existing `Accept` path). (In-process loopback — no external infra.)
 3. **25.3 — remaining activity-type undo-propagation coverage:** lock the cross-instance undo path for the activity types not yet covered end-to-end (e.g. a community's `Undo` of a join/membership decision, and any remaining edge-recording activity whose remote-side removal is untested), each proven non-vacuous. (In-process loopback — no external infra.)
 >
 > **Deferred (live-infrastructure, not provisionable in this environment):**
>
4. External-FQDN verification — resolver reachability, public navigation, browser verification over live hostnames. (Needs a reachable public FQDN / reverse proxy; deferred.) See [phase-22-closeout.md §1](docs/plans/phase-22-closeout.md#1-external-fqdn-verification).
5. Live federation/interop checks against public Mastodon accounts (follow, post/receive, signatures, pagination, community flows). (Needs real external accounts; deferred.) See [phase-22-closeout.md §2](docs/plans/phase-22-closeout.md#2-live-federation-and-interop-checks).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(none currently)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

- 25.1: **delivery retry observes the configured backoff** — a test-only lock (no code change): the `DeliveryWorker` (F-22) already retries a failed delivery with exponential backoff, but every existing `DeliveryRetryTests` outcome test sets `BaseDelay = 0`, so none locked that the worker *actually observes* a non-zero delay between retries (a regression removing the `Task.Delay` would keep all green). New `TransientFailure_WaitsConfiguredBackoff_BetweenRetries` (1, drives a real worker against a failable transport with a real 150ms `BaseDelay`, 500/500/200 recovery): asserts total elapsed `>= 250ms` (two backoffs) and the gap before the 2nd attempt `>= 100ms` — proven non-vacuous (`BaseDelay = 0` → observed 69ms → fail). `FailableHandler` gains an optional timestamp-tracking mode. → [docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md](docs/changes/237-25.1-delivery-retry-observes-configured-backoff.md)
- 24.4: **cross-instance community→community un-follow propagation** — locked with tests (no code change): a community C on A follows a community D on B (federates A → B; B's `FollowActivityHandler` community branch, F-24, records **both** of D's edges — D's follows set D → C and D's followers set C → D — and stores the original `Follow`), then C's `Undo(Follow)` federates A → B and B's `UndoActivityHandler` community-target arm removes both of D's edges. New `CommunityFollowsCommunityUnfollowPropagationIntegrationTests` (2, 2-instance `TestServer`) — proven non-vacuous (disabling the remote community-target removal fails the unfollow test while the forward follow test still passes). Closes Phase 24's locally-actionable scope. → [docs/changes/236-24.4-cross-instance-community-community-unfollow-propagation.md](docs/changes/236-24.4-cross-instance-community-community-unfollow-propagation.md)
- 24.3: **idempotent handling of duplicate inbound deliveries** — a real federation peer may redeliver the same signed activity (on timeout/retry/retransmission), so the receiving instance must treat a repeat as a no-op. Locked the C-07 inbox-Id dedup guard (store add-if-absent + skip handler re-dispatch on a re-delivery) for an *edge-recording* activity with `DuplicateInboundDeliveryIdempotencyIntegrationTests` (2, 2-instance `TestServer`): a redelivered `Block` records the block edge exactly once and the second delivery is accepted as a no-op (202, not 500); a redelivered `Follow` records the follow edge exactly once **and** emits exactly one `Accept` to the follower (the `FollowActivityHandler` mints a fresh `Accept` id per dispatch, so the "exactly one Accept" assertion is the non-vacuous proof the handler runs exactly once — the idempotent store edge alone would stay one even if the handler ran twice). Proven non-vacuous: disabling the C-07 early-return makes the `Follow` test observe **two** distinct `Accept`s in the follower's inbox (guard on → one). → [docs/changes/235-24.3-duplicate-inbound-delivery-idempotency.md](docs/changes/235-24.3-duplicate-inbound-delivery-idempotency.md)

Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

- 24.2: **cross-instance mute / un-mute propagation** — a genuine feature addition (not a test-only lock): `Mute` is **not** an ActivityStreams type (the library has no `Mute` class), so an inbound `"type": "Mute"` deserialized to a plain `Object` (not an `Activity`) — the inbox 400'd it — and a `MuteActivity` serialized as a generic object (dropping `@context`), so a mute could neither be sent nor received across instances. Fixed by introducing the Iris-specific `MuteActivity` (a thin `Activity` subclass pinning `Type` to `["Mute"]`) and registering it in the library's `ObjectTypes.Types` so it round-trips with its `@context`/`type`; added the `MuteActivityHandler` (records the muter → muted edge when the muted actor is local), the inbox/outbox `Mute` wrap, the outbox-publish mute + `Undo(Mute)` arms, and `"mutes"` collection-page invalidation. New `MuteUndoPropagationIntegrationTests` (1, 2-instance `TestServer`: alice on A mutes bob on B → B records the edge; `Undo(Mute)` → both remove it) — proven non-vacuous (disabling the `ObjectTypes.Types` registration or the `MuteActivityHandler` registration each fails the test). → [docs/changes/234-24.2-cross-instance-mute-undo-propagation.md](docs/changes/234-24.2-cross-instance-mute-undo-propagation.md)
- 24.1: **cross-instance like / announce undo propagation** — surfaced and **fixed** a real delivery-target bug: a `Like`/`Announce` of a **remote** object (and its `Undo`) was delivered to the object IRI's non-existent `/inbox` (a Note has no inbox) instead of the object's author's inbox, so the remote instance never received the activity or its undo. Fixed in `OutboxPublishHandler` + the 4 `Record/Remove Like/Announce` helpers to resolve the object's author (`attributedTo`) by fetching the remote object's document (best-effort; a fetch failure degrades to the object IRI fallback and never fails the publish); registered the outbound `IActivityPubClient` in the server host. New `LikeAnnounceUndoPropagationIntegrationTests` (2, 2-instance `TestServer`: like asserts B stores the delivered activity — the like edge is home-instance-local; announce asserts B records then removes the edge) — proven non-vacuous (disabling the remote owner-resolution makes both fail). → [docs/changes/233-24.1-cross-instance-like-announce-undo-propagation.md](docs/changes/233-24.1-cross-instance-like-announce-undo-propagation.md)
## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
