# 077 — Phase 8: Fix the delivery model — the outbox is the write surface

> 2026-08-30 · Phase 8 (Sample) · Delivery-model fix

## What was built

The ActivityPub delivery model is now consistent **everywhere** (client, server, tests, samples): the
actor's **outbox** is the write surface for authored activities. A client expresses intent by POSTing an
authored activity to the actor's **own outbox** — never to a recipient's inbox. The server records the
activity in the actor's outbox + activity store and is the **only** thing that delivers it to a
recipient's inbox (the server→server federation hop). Inbound federation (a remote peer delivering to a
local actor) still uses the local actor's **inbox**.

This resolves the "forward-looking" tension flagged in [076](076-explorer-write-screens.md) and the
"model 1 vs model 2" open question in [SAMPLE_PLAN §4.3a](../SAMPLE_PLAN.md#43a-delivery-model-the-outbox-is-the-write-surface):
the project commits to **the outbox-as-write-surface model** (model 2).

## The change, part by part

### Client: every authored write routes to `actorId.OutboxOf()`

`src/Iris.Client/ActivityPubClient.cs` — the nine authored-write methods now deliver to the acting
actor's own outbox instead of a recipient's inbox (or, for like/undo, the actor's own inbox):

- `FollowAsync`, `BlockAsync`, `UnblockAsync`, `FlagAsync`, `UnflagAsync`, `UndoFollowAsync`,
  `LikeAsync`, `PostNoteAsync`, `PostReplyAsync` → `DeliverAsync(actorId.OutboxOf(), …)`.
- `MuteAsync` / `UnmuteAsync` remain local Basic-auth (not a signed delivery at all).

### Server: new `POST /ap/v1/u/{handle}/outbox` publish surface

`src/Iris.Server/ActivityPubServerExtensions.cs` adds an `OutboxPublishHandler` (mapped after the inbox
POST). It:

1. signature-validates the request;
2. resolves `actorIri` via `BuildActorIri(baseUrl, handle)`;
3. guards with `TryGetActorAsync` (404 if the actor is absent);
4. enforces `activity.Actor == actorIri` (403 otherwise — an actor may only publish to its own outbox);
5. records the activity in the actor's **outbox** + the activity store (`AddToOutboxAsync` +
   `PutActivityAsync`);
6. records the **local** edge for the activity type and resolves the recipient (the server→server
   delivery target) — `RecordFollowLocalAsync` / `RecordBlockLocalAsync` / `RecordFlagLocalAsync` /
   `RecordLikeLocalAsync` / `RecordUndoLocalAsync` / `RecordCreateLocalAsync`;
7. if the recipient is remote, enqueues `delivery.DeliverToActorAsync(recipient, activity, actorIri, ct)`
   (the `DeliveryWorker` performs the async server→server hop to the recipient's inbox/shared-inbox).

`RecordUndoLocalAsync` resolves the stored activity the `Undo` references (via its deterministic IRI) and
delegates to the matching `Remove*LocalAsync` (follow/block/flag) so an un-follow / un-block / un-flag
removes the actor's home edge and federates the undo.

### Server: the actor's home follow edge is recorded regardless of target locality

`RecordFollowLocalAsync` / `RemoveFollowLocalAsync` previously recorded (and removed) the actor's own
follow edge only when the **target** was local. That was wrong for federation: when alice (on A) follows
bob (on B), A must still record the `alice → bob` edge in its **own** follow store so alice's `following`
collection lists the remote target. The edge is now recorded/removed unconditionally; a follow of a local
*community* additionally records the community's follows + followers sets (the F-24 community branch, the
inverse of `FollowActivityHandler`'s community branch).

### Tests

- `tests/Iris.Client.Tests/ActivityPubClientTests.cs` — three assertions updated to expect
  `…/outbox` (Follow, FollowCommunity, PostNote).
- `tests/SampleBlazorClient.Tests/S7ScreenTests.cs` — the federated follow/unfollow test rewritten to the
  new model: the client publishes Follow / Undo to alice's **own outbox on A** (a single `toA` client),
  A records the edge, and the server delivers to bob's inbox on B (the test polls with
  `TestFederation.WaitForAsync` for B's edge, since the `DeliveryWorker` is async).
- `tests/Iris.Testing/TestFederation.cs` — `StartServer` gains an optional `deliveryTransport`
  parameter so a test instance's outbound `DeliveryWorker` can be routed to another in-process
  `TestServer` (previously it always used the real `HttpClientHandler`, which cannot reach an
  in-process peer). The federated sample test wires A↔B with a deferred `LazyHandler`.

## Tests

Full solution green — **878 tests, 0 failures** — build clean (0 warnings).

## Decisions

- **The outbox is the write surface (model 2).** A user-initiated activity is a signed POST to the
  author's own outbox on the author's home instance. The server owns the recipient hop: it records the
  local edge and, for a remote recipient, performs the server→server delivery. This is the model the
  follow lifecycle already assumed for posts/replies, now extended to relationship edges (follow/block/
  flag/like) so the client has a single, uniform write path.
- **`activity.Actor` must equal the outbox owner.** The outbox publish surface rejects (403) an activity
  whose actor is not the outbox's owner — an actor cannot publish another actor's activity to its own
  outbox. Signature validation already binds the request to a key; this check binds the activity body to
  the outbox.
- **The home follow edge is locality-independent.** Recording the actor's own `following`/`blocks`/
  `flags` edge in the home store does not depend on whether the target is local; only the server→server
  delivery (to the target's inbox) is conditional on the target being remote.
