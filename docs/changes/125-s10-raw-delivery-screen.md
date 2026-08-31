# 125 — Sample Explorer: Raw-delivery screen exercises `DeliverAsync` (closes a §3.1 gap)

**Status:** DONE — new `/deliver` page drives the **raw** `DeliverAsync` escape hatch directly (build a
`Follow`, show its signed JSON, POST it to the target's inbox). 3 new in-process tests; full solution 0
warnings, 876/876 green.

## What

The second-round plan's §3.1 gap list named `DeliverAsync` ("Raw signed activity to an inbox —
ServerToServer profile") as "All writes go through high-level helpers; the escape hatch is unused." This
slice wires it into a new **Raw delivery** screen (`/deliver`, a new nav link) so the method is exercised
standalone.

The screen:

1. Takes a **target actor IRI** input.
2. Builds the `Follow` activity exactly as the high-level `FollowAsync` does (deterministic
   unique-per-(actor,target) IRI, `Actor` = the logged-on actor, `Object` = the target).
3. **Shows the activity JSON** (`ActivityJson.Serialize`) — the exact payload that is signed + sent.
4. Calls `DeliverAsync(target.InboxOf(), follow)` — the raw escape hatch — and shows the
   `DeliveryResult` (status code + success).

```
Deliver.razor — DeliverAsync:
  var follow = new Follow { Id = $"{self}/follows/{target}", Actor = [self], Object = [target] };
  ActivityBody = ActivityJson.Serialize(follow);        // shown to the user
  Result = await Session.GetClient().DeliverAsync(target.InboxOf(), follow);   // raw escape hatch
```

This is the **only** screen that drives `DeliverAsync` directly. Every other write (follow, like,
moderation, compose) reaches it *through* a high-level helper; this one proves the helper's underlying
method works on its own (signature validation + inbox recording + the `202`/`404` contract).

## Tests (3 new, `S10RawDeliveryTests`)

In-process against a real `Iris.Server` pipeline (mirrors the S9/S3 host):

- `Deliver_RawFollowToInbox_IsAcceptedAndRecordsEdge` — a raw `Follow` to bob's inbox is **202
  Accepted** and records the follow edge (bob's `followers` collection gains alice).
- `Deliver_RawFollowTwice_SameIri_DoesNotDuplicateEdge` — delivering the same `Follow` (same
  deterministic IRI) twice is still 202 and does not duplicate the edge (followers list the sender once)
  — mirroring how the high-level helpers dedupe by activity IRI.
- `Deliver_RawFollowToUnknownInbox_IsNotFound` — delivery to a non-existent actor's inbox is **404**
  (the endpoint checks the recipient exists before processing).

## Why a `Follow` (and not another activity)

A `Follow` is the simplest activity with a clean, observable effect on the target (its `followers`
collection), and it exercises the same inbox path (`POST /ap/v1/u/{handle}/inbox` →
`InboxProcessor` → `FollowActivityHandler`) the high-level `FollowAsync` uses — so the screen proves the
escape hatch end-to-end without needing the outbox-write surface. A `Like`/`Create` would require the
object to exist or the owner's inbox to be resolved; a `Follow` keeps the screen minimal.

## §3.1 gap list — now fully closed in the UI

| Method | UI home |
|---|---|
| `GetRelaysAsync`/`SubscribeRelayAsync`/`UnsubscribeRelayAsync` | Actor detail → Relays (S4) |
| `GetFollowFeedAsync` | **client-tested** (S3); the Feed page keeps the paged `GetCollectionAsync` (the typed method can't carry the `next`-link) |
| `GetActorAsync` | Actor detail (S9) |
| `GetCollectionAsync` | Feed page "Load more" (S3) |
| `DeliverAsync` | **Raw delivery page (S10)** |
| `MuteAsync`/`UnmuteAsync`/`BlockAsync`/`UnblockAsync`/`FlagAsync`/`UnflagAsync` | Actor detail → Moderation |
| `UndoFollowAsync`/`LikeAsync` | Actor detail → Follow/Unfollow; Object page → Like |
