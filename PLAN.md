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

**Phase 24 — federation robustness & remaining cross-instance propagation gaps.** Phase 23's locally-actionable scope is fully closed (22.11–22.14). Phase 24 continues the in-process federation hardening: the remaining propagation gaps (mute/un-mute cross-instance, community→community un-follow) and delivery robustness (retry/backoff on transient failure, idempotent handling of duplicate inbound deliveries) — all testable on the 2-instance `TestServer` harness. 24.1 (cross-instance like/announce undo propagation) is now closed: it surfaced and **fixed** a real delivery-target bug (a Like/Announce of a remote object — and its Undo — was delivered to the object IRI's non-existent `/inbox` instead of the object's author's inbox) and locked the fix with tests. Only the two live-infrastructure items (a public FQDN, real external Mastodon accounts) remain deferred.

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
> deferred. Phase 24 (federation robustness & remaining cross-instance propagation gaps) is now active.

> **Phase 24 slices (locally-actionable, in-process 2-instance `TestServer`):**
>
1. **24.2 — cross-instance mute / un-mute propagation:** on a 2-instance topology, a local actor on A mutes an actor on B (the mute federates A → B), then A un-mutes; assert B reflects the mute then its removal. (In-process loopback — no external infra.)
2. **24.3 — idempotent handling of duplicate inbound deliveries:** deliver the same signed activity to B's inbox twice (the same server-minted IRI); assert B records the edge exactly once and does not double-record or error (a real federation peer may redeliver on timeout/retry). (In-process loopback — no external infra.)
3. **24.4 — community→community un-follow propagation:** a community C on A follows a community D on B (federates A → B; B records C in D's followers), then C's `Undo(Follow)` federates A → B and B removes C from D's followers (the community→community analogue of 22.14). (In-process loopback — no external infra.)
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

Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

- 24.1: **cross-instance like / announce undo propagation** — surfaced and **fixed** a real delivery-target bug: a `Like`/`Announce` of a **remote** object (and its `Undo`) was delivered to the object IRI's non-existent `/inbox` (a Note has no inbox) instead of the object's author's inbox, so the remote instance never received the activity or its undo. Fixed in `OutboxPublishHandler` + the 4 `Record/Remove Like/Announce` helpers to resolve the object's author (`attributedTo`) by fetching the remote object's document (best-effort; a fetch failure degrades to the object IRI fallback and never fails the publish); registered the outbound `IActivityPubClient` in the server host. New `LikeAnnounceUndoPropagationIntegrationTests` (2, 2-instance `TestServer`: like asserts B stores the delivered activity — the like edge is home-instance-local; announce asserts B records then removes the edge) — proven non-vacuous (disabling the remote owner-resolution makes both fail). → [docs/changes/233-24.1-cross-instance-like-announce-undo-propagation.md](docs/changes/233-24.1-cross-instance-like-announce-undo-propagation.md)
- 22.14: **Phase 23 closes** — cross-instance community **un-follow** propagation locked with tests (no code change): a community C on A follows a person bob on B (federates A → B; B records C in bob's followers + stores the original `Follow`), then C's `Undo(Follow)` federates A → B and B's `UndoActivityHandler` (F-1911-1) removes C from bob's followers. New `CommunityFollowsPersonUnfollowPropagationIntegrationTests` (2, 2-instance `TestServer`) — proven non-vacuous (disabling the remote removal fails the unfollow test). → [docs/changes/232-22.14-cross-instance-community-unfollow-propagation.md](docs/changes/232-22.14-cross-instance-community-unfollow-propagation.md)
- 22.13: **cross-instance moderation undo propagation** — locked with tests (no code change): an `Undo(Block)`/`Undo(Flag)` published to A's outbox federates A → B and B's `UndoActivityHandler` (F-07) resolves the parties from the **locally-stored original** and removes the recorded edge. New `ModerationUndoPropagationIntegrationTests` (2, 2-instance `TestServer`: block + flag variants, each asserting the edge is gone on both A and B and the index/collection agrees) — proven non-vacuous (disabling the remote removal makes both fail). → [docs/changes/231-22.13-cross-instance-moderation-undo-propagation.md](docs/changes/231-22.13-cross-instance-moderation-undo-propagation.md)
- 22.11: fixed the moderation-collection cache-invalidation gap (C2S invariant): `blocks`/`flags`/`mutes` pages were served through the 60s-TTL page cache but never invalidated on a local moderation write, so the owner's card stayed stale until TTL; generalized `InvalidateLocalOutboxPage` → `InvalidateLocalCollectionPage` and wired it into the outbox-publish `Block`/`Flag`/`Undo` branches + both mute handlers. New `ModerationCollectionCacheInvalidationIntegrationTests` (5), prime→write→plain-read (proven to fail on the old code). → [docs/changes/229-22.11-moderation-collection-cache-invalidation.md](docs/changes/229-22.11-moderation-collection-cache-invalidation.md)

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
