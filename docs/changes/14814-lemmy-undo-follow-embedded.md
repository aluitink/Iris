# 14814 — Lemmy Undo (un-follow) 400: embed the original Follow in the outbound Undo

**Status:** done
**Slice:** Dev Queue Inbox — Lemmy Undo 400 (un-follow dead-lettered)
**Owner:** dev

## Problem

When a local actor (person or community) on Iris un-follows a **remote** actor/community, Iris
publishes an `Undo` of the original `Follow` to the remote peer's inbox. Iris built the `Undo` with
its `object` as a **bare IRI link** to the original `Follow` (the canonical ActivityStreams shape,
which Iris's own inbound `UndoActivityHandler` accepts).

Lemmy (0.19.20) rejects that shape. Its ActivityStreams `Activity` parser is an *untagged* enum and
requires the `Undo`'s `object` to be an **embedded activity object** — a bare IRI link matches no
variant, so Lemmy responds:

```
400 {"error":"unknown","message":"data did not match any variant of untagged enum AnnouncableActivities"}
```

Confirmed live: a bare-link `Undo` → 400 (both `/inbox` and `/c/interop/inbox`); the same `Undo`
with the `Follow` **embedded** in `object` → **200** and the `community_follower` row removed.

The 400 caused the `DeliveryWorker` to **dead-letter** the delivery, so the un-follow never reached
the remote instance (the remote kept following the un-follower).

## Fix

Before cross-instance delivery of an `Undo`, Iris now **embeds the original `Follow`** in the
outbound `Undo`'s `object` (resolved from Iris's own activity store). The *stored* activity is left
unchanged (bare link); only the *outbound* delivery materializes the embedded form. This is
backward-compatible: Iris's inbound `UndoActivityHandler` (`ResolveObjectIri`) already accepts either
a stored-IRI or an embedded object, so Iris ↔ Iris interop is unaffected.

Two delivery paths, both fixed:

1. **Person outbox** — `OutboxPublishHandler`. When the published activity is an `Undo`, a new
   `MaterializeUndoObjectForDeliveryAsync(persistence, undo, ct)` helper resolves `undo.Object`
   (IRI) via `ResolveObjectIri`, fetches the stored activity with `Activities.TryGetActivityAsync`,
   and — if it is a `Follow` — returns a copy of the `Undo` with `Object = [follow]` (copying
   Id/Actor/To/Cc/Published). The delivery calls (`AddToInboxAsync` / `DeliverToActorAsync`) use the
   materialized `outboundActivity` when one is produced.

2. **Community outbox** — `CommunityOutboxPublishHandler`. The follow-discovery loop already captures
   the original `Follow` (`foundFollow`); the delivery now calls a new `EmbedFollowInUndo(undo,
   foundFollow)` helper that returns the `Undo` unchanged when `foundFollow` is null, else a copy
   with `Object = [foundFollow]`.

### Shape

```json
{
  "type": "Undo",
  "actor": "https://iris.luit.ink/ap/v1/u/s7test",
  "object": {
    "type": "Follow",
    "id": "https://iris.luit.ink/ap/v1/activities/…",
    "actor": "https://iris.luit.ink/ap/v1/u/s7test",
    "object": "https://lemmy.luit.ink/c/interop"
  }
}
```

## Verification

- **Regression test** `tests/Iris.Server.Tests/PersonUnfollowEmbeddedObjectPropagationIntegrationTests.cs`
  (`PersonUnfollowOfRemotePerson_DeliversUndoWithEmbeddedFollow`): two-host fixture (A `pue-a`
  alice, B `pue-b` bob) with A's outbound delivery routed to a **capturing** transport. Establishes
  the follow (federates A → B), publishes the `Undo`, and asserts the captured delivered `Undo`
  carries the original `Follow` **embedded** (`object` is an object with `type`/`id`/`actor`/`object`
  matching the minted Follow), not a bare IRI link. Fails on the pre-fix bare-link shape.
- **No regressions:** all existing unfollow/Undo propagation tests pass
  (`PersonFollowsPersonUnfollowPropagationIntegrationTests`,
  `CommunityFollowsPersonUnfollowPropagationIntegrationTests`,
  `CommunityFollowsCommunityUnfollowPropagationIntegrationTests`, etc.).
- **Full fast suite** (`dotnet test --filter "Category!=Slow"`): **1401 passed, 0 failed**.

## Live re-verify (COMPLETE, 2026-09-20)

Rebuilt + redeployed (`b2e4f6c`), then via the live harness:
1. `follow` s7test → Lemmy `c/interop` → **202**; Lemmy `community_follower` count went **1 → 2**.
2. `undo` (of the minted Follow) → **202**; the delivered Undo now carries the embedded Follow.
3. Lemmy `community_follower` count went **2 → 1** — the un-follow reached Lemmy. No 400, no
   dead-letter in `docker logs irisweb-iris-web-1`.

## Files

- `src/Iris.Server/ActivityPubServerExtensions.cs`
  - `OutboxPublishHandler` — `outboundActivity` + `MaterializeUndoObjectForDeliveryAsync` call.
  - `MaterializeUndoObjectForDeliveryAsync` (new).
  - `EmbedFollowInUndo` (new).
  - `CommunityOutboxPublishHandler` — `foundFollow` + `EmbedFollowInUndo` call.
- `tests/Iris.Server.Tests/PersonUnfollowEmbeddedObjectPropagationIntegrationTests.cs` (new).
