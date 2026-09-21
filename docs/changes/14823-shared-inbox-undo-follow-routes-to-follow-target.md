# 14823 — Shared-inbox Undo of a Follow routes to the follow's target (fixes S33)

- **Finding:** [s33](../qa/s33-unfollow-undo-not-propagated-peer-edge-remains.md) — S2-sev, cross-instance interop (Iris↔Iris).
- **Commit:** `d6914d6` (fix + tests), on top of `7d290c4` (S31 docs).
- **Date:** 2026-09-21.

## What was built

A person on instance A un-follows a person on instance B. A removes its own follow edge and
server-delivers the signed `Undo` of the original `Follow` to B. B advertises an instance-wide
**shared inbox** (`endpoints.sharedInbox` → `{base}/ap/v1/shared-inbox`, which the app always
configures), so A delivers to that shared inbox rather than to the followee's per-actor inbox.
B's `SharedInboxHandler` must read the intended recipient from the payload and route the delivery
to the matching local actor.

**The bug (S33):** for any non-content activity the handler routed to `typedActivity.Object`. For
an `Undo` of a `Follow`, the object is the **original Follow** (an activity, not an actor) — either
an embedded `Follow` (whose `id` is the follow IRI) or a bare IRI link to the follow IRI. So the
recipient resolved to the **follow IRI**, which is not a local actor:

```
Inbox rejected: unknown recipient https://…/ap/v1/u/ii-b1/follows/06GC0G2GXSP0H524BRG9D8MBZ4
```

The delivery 404s at the recipient-existence check, the followee's `UndoActivityHandler` never runs,
and the unfollower **remains in the followee's `followers` set** on B. The un-follow is correct on
the unfollower's instance (A) but the peer edge is stale (B).

## Root cause

`SharedInboxHandler` (`ActivityPubServerExtensions.cs`, `POST /ap/v1/shared-inbox`) treated every
non-content activity as "object-addressed" and used `FirstIriFromCollection(typedActivity.Object)`
as the recipient. That is correct for a `Follow` (object = the followee, an actor) but wrong for an
`Undo` (object = the undone activity, an `Undo` whose object is the original `Follow` → its `id` is
the follow IRI, an activity). The follow IRI is not an actor, so the recipient check rejects it.

The per-actor-inbox path was never affected: `OutboxPublishHandler` resolves the Undo's recipient as
the follow's **target** (the followee) via `RecordUndoLocalAsync` → `RemoveFollowLocalAsync`, and
delivers to the followee's inbox. The two-instance test
`PersonFollowsPersonUnfollowPropagationIntegrationTests.PersonUnfollowOfRemotePerson_FederatesAndRemovesFromRemoteFollowers`
(forwarding to B's per-actor inbox) passes — it simply never exercises the shared inbox, which is
why the live stack (shared inbox) reproduced the rejection while the test stayed green.

## The fix

`SharedInboxHandler` now special-cases an `Undo` whose object is a `Follow` (detected via the
`typedActivity is Undo { Object: { } }` pattern, then resolving the follow's target):

- **Embedded Follow** (`undoObject is Follow`): read the follow's `Object` directly (the followee) —
  no store lookup needed.
- **Bare IRI link** to the Follow: resolve the follow from this instance's activity store
  (`persistence.Activities.TryGetActivityAsync(followIri, out stored)`); when it is a `Follow`, read
  its `Object` (the followee). The followee's instance stored the original `Follow` when it received
  it, so the store has it.
- When the follow's target cannot be resolved (e.g. a bare IRI to a follow this instance never
  stored), no recipient is added and the delivery is **accepted and dropped** (the handler's existing
  `recipients.Count == 0` behavior — a 4xx would make the sender retry a delivery this instance can
  never process).

The routed recipient (the followee) is a local actor, so `HandleInboxPostAsync`'s `exists` check
passes and the delivery proceeds to the followee's `UndoActivityHandler`, which resolves the original
follow (from the embedded object or the followee's activity store) and removes the unfollower from
the followee's followers set.

This is backward-compatible: a receiving Iris that resolves the embedded follow the same way it
resolves a stored one is unaffected; the change only corrects *which local actor* the shared inbox
routes the Undo to.

## Tests

`tests/Iris.Server.Tests/Security/SharedInboxIntegrationTests.cs` (+2):

- `UndoOfFollow_DeliveredToSharedInbox_RoutesToFollowTargetAndRemovesEdge` — the **embedded** shape:
  a remote alice un-follows bob; the `Undo` (object = embedded `Follow`) is delivered to B's shared
  inbox; asserts B removes the `alice → bob` edge.
- `UndoOfFollow_BareIri_DeliveredToSharedInbox_RoutesToFollowTargetAndRemovesEdge` — the **bare-IRI**
  shape (the exact wire shape S33 reproduced on the live stack): the `Undo`'s object is a link to the
  original follow IRI; B resolves the follow from its activity store and removes the edge.

Both **fail without the fix** (the edge remains stale — verified by stashing the fix) and **pass
with it**. The existing 4 shared-inbox tests (Follow, Announce fan-out, Follow-to-remote dropped,
Create non-local dropped) still pass — the `Follow`/`Announce`/`Create` routing is unchanged.

**Fast suite:** `Iris.Server.Tests` **1412 pass, 0 fail** (was 1410; +2). Web **108 pass, 0 fail**.

## Live re-verification

Deferred to QA on the two-instance federation stack (dev does not run it). Re-verify from a clean
entry: `ii-b1` (B) follows `ii-a1` (A); A `ii-a1/followers` = `[ii-b1]`. `ii-b1` un-follows. B
`following` = empty. A log: the `Undo` is **received and applied** (no "unknown recipient" rejection
of the follow IRI). `GET A /ap/v1/u/ii-a1/followers` → **empty** (ii-b1 removed).
