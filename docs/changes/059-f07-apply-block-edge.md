# 059 — F-07 moderation: apply the block edge (feed exclusion + delivery suppression)

> 2026-08-29 · Slice 12.14 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the *application* half of gap **F-07** (moderation): Slice 12.13 **recorded** the directed block
edge `<c>blocker → blocked</c>` (the `IModerationStore`) and served the actor's `blocks` collection, but
the edge was inert — a blocked actor's content still appeared in a local actor's feed, and the instance
still delivered content to an actor that had blocked a local actor. This slice **applies** the edge in
both directions, so a block is now a real moderation decision rather than a recorded fact:

- **Feed exclusion (blocker's side).** `FeedService` (the F-14 followed feed / home timeline, served at
  `GET /ap/v1/u/{handle}/feed`) now reads the actor's `blocks` set from the moderation store and **skips
  any follow the actor has blocked** when merging outboxes. A blocked actor's content — local or remote —
  no longer appears in the actor's home timeline. The check is by the follow's actor IRI (the edge is
  recorded on the actor IRI), so it applies uniformly to local and remote follows.
- **Delivery suppression (blocked's side).** `CreateActivityHandler` (J-18 outbound federation) now skips
  a remote follower who has **blocked the author** before scheduling that follower's delivery: a follower
  that blocked the author does not want the author's content, so the author's `Create` is not federated to
  them. `DeliveryService.DeliverToActorAsync` (the generic actor-targeted delivery seam) additionally
  suppresses a delivery **signed as a local actor** when the recipient has blocked that signing actor — a
  second line of defense at the enqueue boundary (e.g. for future activity types that fan out through
  `DeliverToActorAsync`). A delivery signed as the instance actor (a null acting actor) is never
  suppressed (the instance actor is not the blocked party).

The two seams the edge is read through are the same `IModerationStore` from Slice 12.13 (`GetBlocksAsync`
for the forward "who does this actor block" query feeding the feed, and `IsBlockedAsync` for the directed
"has X blocked Y" predicate feeding delivery). No new store or wire surface is added; this slice only
*consumes* the recorded edges.

*Scope note:* this slice is the **application** of the recorded block edge (feed + delivery). It does
**not** yet wire the **un-block** (`Undo` of `Block`, via the existing `IModerationStore.RemoveBlockAsync`)
nor add `Mute` / `Flag` (the remaining F-07 verbs; `Flag` is the natural next addition, `Mute` has no
ActivityStreams type and is Iris-specific). F-06 (shared-inbox / relay) is still open and is the next
Phase 12 item after F-07 is closed.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/FeedService.cs` | Reads `persistence.Moderation.GetBlocksAsync(actorIri)` and skips blocked follows when merging the followed feed (F-07 feed exclusion). |
| `src/Iris.Server/DeliveryService.cs` | `DeliverToActorAsync` suppresses an actor-targeted delivery (signed as a local actor) when the recipient has blocked the signing actor (F-07 delivery suppression at the enqueue boundary). |
| `src/Iris.Server/CreateActivityHandler.cs` | J-18 federation skips a remote follower who has blocked the author (`Moderation.IsBlockedAsync(follower, author)`). |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | DI: passes `persistence.Moderation` into the `FeedService` and `DeliveryService` registrations. |
| `tests/Iris.Server.Tests/FeedServiceTests.cs` | 4 new unit tests (blocked local follow excluded, blocked remote follow excluded, partial block keeps unblocked follows, no-moderation-store includes all). |
| `tests/Iris.Server.Tests/DeliveryQueueAndServiceTests.cs` | 4 new unit tests (recipient-blocked-signer suppresses, no block delivers, instance-actor skips the check, no-moderation-store never suppresses). |
| `tests/Iris.Server.Tests/CreateActivityHandlerTests.cs` | 2 new unit tests (remote follower who blocked the author is skipped; a follower who did not block is delivered to). |
| `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` | 1 new end-to-end test: a signed inbound `Block` of a followed actor excludes that actor's post from the blocker's followed feed over the wire (present before the block, absent after). |

## Tests

746 → **757** (+11):

- `tests/Iris.Server.Tests/FeedServiceTests.cs` — 4 new. Each drives the real `FeedService` against an
  `InMemoryPersistenceProvider` (follows + activities + moderation). Coverage: a **blocked local follow**
  contributes nothing (the follow exists and the actor is blocked → the feed is empty); a **blocked remote
  follow** contributes nothing (the remote outbox is not merged); a **partial block** (follow two actors,
  block one) keeps the unblocked actor's content and excludes the blocked one; and a service constructed
  **without a moderation store** (moderation disabled) merges every follow (the pre-F-07 behavior — no
  block edge can exist).
- `tests/Iris.Server.Tests/DeliveryQueueAndServiceTests.cs` — 4 new. Each drives the real
  `DeliveryService` against an `InMemoryDeliveryQueue` + `InMemoryModerationStore`. Coverage: a
  `DeliverToActorAsync` **signed as a local actor** is **suppressed** (no job enqueued, asserted via
  `queue.Count == 0` — the bounded queue's `TryDequeueAsync` blocks when empty) when the recipient has
  blocked the signing actor; with **no block** the same call enqueues normally; a delivery **signed as the
  instance actor** (null acting actor) is **never suppressed** even when the recipient blocked that actor;
  and a service **without a moderation store** never suppresses (pre-F-07).
- `tests/Iris.Server.Tests/CreateActivityHandlerTests.cs` — 2 new. A local author's `Create` is **not**
  federated to a remote follower who has blocked the author (the post is still surfaced in the author's
  outbox, J-8, but no delivery is scheduled), and a remote follower who did **not** block the author is
  delivered to normally (J-18).
- `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` — 1 new end-to-end. Bob follows carol and
  carol has a post; the test asserts carol's note IRI is in bob's followed feed (`GET /ap/v1/u/bob/feed`)
  **before** the block, then delivers a signed `Block` (actor = bob, object = carol) to carol's inbox, and
  asserts carol's note IRI is **absent** from bob's followed feed **after** the block — the block edge is
  applied on the blocker's side, end to end over the wire.

## Decisions

- **The feed is filtered by the actor's *own* `blocks` (forward edge).** The block is applied on the
  blocker's side: the actor's home timeline excludes the content of actors *that actor* blocked. The feed
  reads `GetBlocksAsync(actorIri)` once and skips those follows. This is the "my timeline doesn't show
  blocked people's posts" semantics — the most visible half of the moderation decision. (A separate,
  distinct behavior — "a remote actor blocked *me*, so hide their content from me" — is a product choice
  not made here; the edge is recorded (Slice 12.13) and available via `GetBlockersAsync`, but the feed
  does not auto-hide content from actors that blocked the reader.)
- **Delivery suppression has two layers, both keyed on the *directed* edge.** `CreateActivityHandler`
  checks `IsBlockedAsync(follower, author)` per remote follower (the follower blocked the author → skip),
  and `DeliveryService.DeliverToActorAsync` checks `IsBlockedAsync(recipient, signingActor)` as a
  boundary guard (the recipient blocked the signer → suppress). The handler check is the primary,
  activity-aware path; the service check is a generic safety net for any actor-targeted delivery. Both use
  the existing `IsBlockedAsync` predicate — no new query.
- **The instance-actor (null) delivery is never suppressed.** An automated event signed as the instance
  actor is not the blocked party; suppression requires a non-null acting actor that the recipient blocked.
  This keeps system-level deliveries (e.g. a `Follow` → `Accept` signed by the instance actor) working
  even in the presence of block edges.
- **Optional moderation store (null disables the feature).** Both `FeedService` and `DeliveryService`
  accept an optional `IModerationStore` (defaulting to null). When null, no block filtering / suppression
  occurs (the pre-F-07 behavior) — so a host that does not register a moderation store (or a test that
  constructs the services directly, as many existing `DeliveryService` tests do) is unchanged. In the
  default `AddActivityPubServer` wiring the store is always present (Slice 12.13 registers
  `InMemoryModerationStore`), so the feature is on by default. The DI passes `persistence.Moderation`
  explicitly (the services are constructed in `TryAddSingleton` factory lambdas, not auto-resolved).
- **The edge is read, not written, here.** This slice adds no new write path — it only *consumes* the
  edges `BlockActivityHandler` recorded in Slice 12.13. The un-block (`Undo` of `Block`, which would call
  `RemoveBlockAsync`) is the remaining F-07 work; once wired, a block and its un-block are symmetric
  writes, and this slice's feed/delivery logic will reflect the current edge set automatically (it reads
  the live store on each request/delivery).
