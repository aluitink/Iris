# 064 — F-06 relay: relay fan-out (the delivery half) — closes F-06

> 2026-08-29 · Slice 12.19 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-06** (relay / `star`) with the **fan-out** (delivery) half. Slice 12.18 wired the
**relay-subscription** (configuration) half — a local actor can subscribe to / un-subscribe from relays, and
the subscription set is the actor's `relays`/`star` collection. But subscribing alone gives a relay no reach
benefit: a relay is only useful if it actually *receives* the content of the local actors that point at it.
This slice wires that delivery half: when a local actor's `Create` (their own post) or `Announce` (their
boost) is processed, the content is now **delivered to each relay the actor has subscribed to**, signed as
the author. **F-06 is now fully resolved.**

The fan-out reuses the existing follower-federation machinery. A relay is a remote `star`-subscribed fan-out
server (ActivityPub §5.1.3) — *always remote*, never a local actor — so the relay delivery is scheduled
unconditionally (no local-actor skip), and the delivery target is resolved by the same `IDeliveryService`
path a remote follower uses (F-01 inbox / `sharedInbox` resolution + F-07 block suppression: a relay that
has blocked the author is suppressed before it is enqueued).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/CreateActivityHandler.cs` | The local-person branch now calls a new private `DeliverToSubscribedRelaysAsync(authorIri, activity, ct)` after the existing remote-follower federation. The helper reads the author's `relays`/`star` set and calls `IDeliveryService.DeliverToActorAsync(relayIri, activity, authorIri, ct)` for each relay. Class XML docs gained a "Relay fan-out (F-06)" paragraph. |
| `src/Iris.Server/AnnounceActivityHandler.cs` | The local-announcer branch now calls the same `DeliverToSubscribedRelaysAsync(announcerIri, announce, ct)` after the existing local-follower propagation, fanning the boost (the original `Announce`) out to each subscribed relay. |
| `src/Iris.Server/InMemoryDeliveryQueue.cs` | A new `Jobs` property: a point-in-time snapshot of the currently queued `DeliveryJob`s (drains and re-enqueues, preserving order) so a test can assert which deliveries the handler scheduled. |
| `tests/Iris.Server.Tests/CreateActivityHandlerTests.cs` | 4 new unit tests (a single subscribed relay fans the `Create` out to the relay's inbox signed as the author; multiple relays fan out to each; no relays → no fan-out; a follower **and** a relay → both are delivered). Added `Relay` / `RelayTwo` IRI constants. |
| `tests/Iris.Server.Tests/RelayFanOutIntegrationTests.cs` (NEW) | 3 end-to-end tests (mirroring `PostFederationIntegrationTests`): a local author on instance A who has subscribed to a relay on instance R posts a `Create` (and, separately, an `Announce`) — A's host `DeliveryWorker` POSTs the activity to the relay's inbox signed as the author, and R validates the delivery (resolving the author's key from A's actor document) and stores it; a no-relay author's post is surfaced locally but **not** fanned out. |

## Tests

817 → **824** (+7):

- `tests/Iris.Server.Tests/CreateActivityHandlerTests.cs` — 4 new. Each drives the real
  `CreateActivityHandler` against a recording `IDeliveryService`. Coverage: a single subscribed relay makes
  the handler schedule **one** delivery to the relay's inbox, signed as the author (not the instance actor);
  multiple subscribed relays schedule one delivery **per relay**; an author with **no** subscribed relays
  schedules no relay delivery; and an author with **both** a remote follower and a relay has the content
  delivered to **both** (the relay fan-out is additive to follower federation).
- `tests/Iris.Server.Tests/RelayFanOutIntegrationTests.cs` — 3 new end-to-end (instance A hosts the local
  author `alice`, subscribed to a relay on instance R). Coverage: a local post (`Create`) is fanned out to
  the relay **over the wire** — A's host `DeliveryWorker` POSTs the serialized `Create` to the relay's inbox
  signed as `alice`, and R validates it (resolving `alice`'s key from A's actor document) and stores it
  (and the post is also recorded in `alice`'s own outbox, J-8 — unchanged); a local **boost** (`Announce`)
  is fanned out the same way (R stores the `Announce`); and an author with **no** subscribed relays has their
  post surfaced locally but **not** fanned out to the relay (R stores nothing).

## Decisions

- **The fan-out is additive to follower federation, not a replacement.** The relay delivery is scheduled
  *after* the existing remote-follower loop in `CreateActivityHandler` and after the local-follower
  propagation in `AnnounceActivityHandler`, reusing `IDeliveryService.DeliverToActorAsync`. This keeps the
  two fan-out targets (followers, relays) orthogonal: a local actor's post reaches their followers **and**
  their relays, independently.
- **A relay is always remote — no local-actor skip, no explicit block check in the handler.** Unlike a
  follower (which may be local, in which case the author's outbox already surfaces the content), a relay is a
  remote fan-out server and is never a local actor, so the relay delivery is scheduled unconditionally. The
  F-07 block edge (a relay that has blocked the author) is enforced by `IDeliveryService.DeliverToActorAsync`
  (which suppresses an actor-targeted delivery signed by a local actor the recipient blocked) before the job
  is enqueued — so the handler needs no block check of its own.
- **The relay's delivery target is resolved by the same F-01 path as a follower.** `DeliverToActorAsync`
  fetches the relay's actor document and honors `endpoints.sharedInbox` (falling back to `{relayIri}/inbox`).
  A relay that advertises a shared inbox is delivered there; one that does not is delivered to its per-actor
  inbox. This means a real relay (which typically advertises a shared inbox for fan-out ingestion) is handled
  correctly with no relay-specific code.
- **The `Jobs` snapshot is a read-only inspection seam.** `InMemoryDeliveryQueue.Jobs` drains the channel and
  re-enqueues the jobs (preserving order), so a test can observe which deliveries were scheduled without
  consuming them. It is an in-memory convenience (a persistent queue would expose its own inspection surface);
  it is not part of the `IDeliveryQueue` contract.
- **Subscription and fan-out remain decoupled.** The subscription set (12.18) is a pure local configuration
  store; the fan-out half (this slice) reads that store at delivery time. Adding a relay to an author's `star`
  set takes effect for their *next* post (no cache invalidation needed — the store is read fresh on each
  `Create`/`Announce`), and removing one stops the fan-out the same way.

## Result

**F-06 (relay / `star`) is fully resolved.** A local actor can configure which relays to fan out through
(Slice 12.18: a Basic-authenticated subscribe/un-subscribe, a public `relays`/`star` collection served +
advertised on the actor document, and a client round-trip) **and** their `Create`/`Announce` content is
actually delivered to each subscribed relay (this slice), signed as the author — the relay's reach benefit is
now real.
