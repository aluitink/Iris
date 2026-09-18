# 136.9 — Update/Delete/Undo propagation (edit, delete/tombstone, undo flows)

**Date:** 2026-09-14
**Slice:** 136.9 (Lemmy interop — edit/update propagation, delete/tombstone behavior incl. remote visibility, undo flows)
**Status:** **COMPLETE (impl + tests).** Deleting a parent post now **collapses the thread under it on every
instance that holds a copy** — the tombstoned parent's `/replies` collection is empty there (no orphaned-but-
still-served replies). Update/Delete/Undo propagation was otherwise verified **already correct** in both
directions (no change needed there).

## What this slice delivers

136.9's acceptance criterion: *"Verify edit/update propagation from each source instance to the remote copy.
Verify delete/tombstone behavior for posts and comments including remote visibility changes. Verify undo flows
(unfollow/unlike) remove or adjust remote state as expected. Exit when lifecycle changes converge and stale
artifacts are bounded and documented."*

### Verification: what was already correct (no change)

The lifecycle propagation was audited end-to-end and found to be **already correct** in **both directions**
(source → remote copy, and remote author → local copy), across the three lifecycle primitives:

| Primitive | Outbound (local author → remote copy) | Inbound (remote author → local copy) | Coverage |
|-----------|----------------------------------------|--------------------------------------|----------|
| **Update** (edit) | federates to remote followers + relays; remote copy refreshed | remote author's Update refreshes the local attributed copy; no collateral; no re-prop | `UpdatePropagationIntegrationTests` (27.1), `UpdateDeleteRelayFanOutIntegrationTests` (28.2), `ObjectPropagationIntegrationTests` 19.3.6 |
| **Delete** (tombstone) | federates to remote followers + relays + remote parent owner; remote copy tombstoned (AS2.0 `Tombstone`, F-10) | remote author's Delete tombstones the local attributed copy; no collateral; no re-prop | `UpdateDeleteRelayFanOutIntegrationTests` (28.2), `ObjectPropagationIntegrationTests` 19.3.4, `DeleteActivityHandlerTests` |
| **Undo** (unfollow/unlike/unboost) | the Undo is delivered to the remote instance; the edge is removed there | the remote instance removes the follow/like/announce edge (the inverse of the original) | `LikeAnnounceUndoPropagationIntegrationTests`, `*UnfollowPropagationIntegrationTests`, `MuteUndoPropagationIntegrationTests`, `ModerationUndoPropagationIntegrationTests` |

So the "lifecycle changes converge" half of the acceptance is satisfied: an edit, delete, or undo issued by an
author on their home instance propagates to every remote instance holding a copy, and a remote author's edit,
delete, or undo is applied to the local copy (with the owner guard + no-collateral + no-re-propagation
invariants). No implementation change was required for Update, Delete-tombstone, or Undo.

### The core gap (why deleting a parent left a still-visible thread)

The **delete/tombstone "remote visibility changes"** half had one genuine gap: when a **parent** post is
deleted, its **replies** were left **orphaned but still listed and served** under the now-tombstoned parent —
on **both** the home instance and every remote instance holding a copy.

The `DeleteActivityHandler` already handled the **reverse** edge (F-12): when the deleted object **is a
reply**, it removed that object's `parent → child` reply edge (so the parent's `/replies` collection no longer
listed the deleted reply). But it did **not** enumerate the deleted object's **children** — so when the deleted
object was a **parent** (it had replies to it), each child's `parent → child` edge remained in the
`IReplyStore`, and the tombstoned parent's `/replies` collection (`GET {parent}/replies`, served by
`ObjectRepliesAsync`) still listed (and a thread reader still rendered) the orphaned replies. This was a stale,
still-visible thread — the exact "remote visibility" / "stale artifact" the acceptance calls out.

### What changed

**`src/Iris.Server/Inbox/DeleteActivityHandler.cs`**

- `HandleAsync` — after the existing F-12 cleanup (removing the deleted object's own parent edge, when it is a
  reply), the handler now **collapses the thread under a deleted parent**: it enumerates the deleted object's
  children via `IReplyStore.GetRepliesAsync(objectIri)` and removes each child's `parent → child` reply edge
  via `IReplyStore.RemoveReplyAsync(objectIri, child)`, so the tombstoned parent's `/replies` collection is
  empty. The **child objects themselves remain stored** (still fetchable by direct IRI) — only the thread
  **listing** is collapsed, not the content. This is local state; the federated half is the **existing** Delete
  propagation (`DeletePropagationService.PropagateDeleteAsync`), which delivers the `Delete` to the remote
  instances holding copies — each applies the same cleanup via the same handler, so the thread collapses on
  **every** instance (home + remote), not just the home.
- Class + method doc comments updated to document the new parent-collapse behavior (the F-12 paragraph now
  covers both directions: deleting a reply removes its own edge; deleting a parent collapses its children).

**`tests/Iris.Server.Tests/Inbox/DeleteActivityHandlerTests.cs`** — 2 new unit tests:

1. **`HandleAsync_LocalOwnerDeletesParent_CollapsesChildrenReplyEdges`** — a parent with two replies is
   deleted; the parent is tombstoned, both children's reply edges are removed (the parent's `/replies`
   collection is empty), and the child objects remain stored (fetchable by direct IRI).
2. **`HandleAsync_LocalOwnerDeletesLeaf_NoChildren_ReplyEdgesUntouched`** — a leaf reply (no children) is
   deleted; its own parent edge is removed (F-12) but the parent is untouched (still a `Note`, not a
   tombstone) — deleting a leaf does not collapse or affect the parent.

**`tests/Iris.Server.Tests/CrossInstanceDeleteThreadCollapseIntegrationTests.cs`** (new) — a two-instance
harness (A: `bob`, the parent's home — the Lemmy stand-in; B: `alice`, bob's follower), reusing the
`ActivityPubHostFactory.Create` bidirectional-federation wiring (a `RoutingFetcher` per instance routes
signature validation by actor host — A validates bob's local Create/Delete *and* alice's federated reply; B
validates alice's local reply *and* bob's federated Create/Delete). Topology: the follow edge alice→bob is
recorded on A (A owns bob's follower set, so A's outbound Create/Delete federation targets alice's inbox on B).

1. **`DeleteOfParent_CollapsesThreadOnRemoteInstance`** — the full cross-instance flow:
   - bob (A) posts the parent m1 (IRI in A's `/ap/v1` serving namespace); A fans the `Create` out to alice (B);
     B stores the m1 copy (attributedTo bob).
   - alice (B) replies to m1 (r1) via a signed `POST /ap/v1/u/alice/outbox`; B's `CreateActivityHandler`
     stores r1 and records the `m1 → r1` reply edge in B's reply store (the thread on the remote instance).
   - bob (A) deletes m1 via a signed `POST /ap/v1/u/bob/outbox`; A tombstones m1 + collapses the thread; A's
     `DeletePropagationService` delivers the `Delete` to alice's inbox on B; B's `DeleteActivityHandler`
     (owner guard accepts the remote author bob, whom B holds an attributed copy of) tombstones B's m1 copy
     **and** removes the `m1 → r1` reply edge.

     Asserted on **B (the remote instance)**: (a) the m1 copy is a `Tombstone`; (b) B's `/replies` collection
     for m1 is empty (the thread collapsed on the remote instance too); (c) the reply object r1 itself remains
     stored (fetchable by direct IRI). And on **A (the home)**: the parent is tombstoned.

## What is NOT in this slice (bounded stale artifacts, documented)

The acceptance's "stale artifacts are bounded and documented" exit is met by bounding + documenting the
residual stale artifacts that are **not** collapsed by this fix:

- **The deleted parent's replies' own content is not removed** — only the thread **listing** under the parent
  is collapsed. Each child object remains stored and fetchable by direct IRI (a client with a direct link can
  still retrieve it). This is the ActivityStreams "collapse the thread, not the content" convention; a
  hard-delete of the children is out of scope (and would be a separate, destructive behavior change).
- **Replies to a parent that was deleted *before* the reply was federated** — if a reply's `inReplyTo` points
  at an object this instance never stored (a remote parent seen only by IRI, the 136.7 case), the reply edge is
  recorded against that parent IRI. If the parent is later deleted by its remote author and the `Delete`
  reaches this instance, the handler collapses the edge (the object is a stored copy attributed to the remote
  author). A parent that was **never stored and never deleted** (a ghost) is a pre-existing, bounded condition
  (the reply threads under an IRI with no local object) — not introduced or worsened by this change.
- **Like/Announce edges on a tombstoned object** — when an object is deleted, the existing like/announce edges
  pointing at it are not swept (the object's `/likes`/`/shares` collections on the home are not reset). These
  edges are bounded (they reference a tombstone; a conforming client treats a tombstoned object's counters as
  moot) and are a candidate for a future moderation/cleanup slice, not a 136.9 lifecycle gap.
- **No live Iris↔Lemmy delete delivery** — the Lemmy side remains blocked by the Lemmy-side signature-parse +
  egress gap (136.3) and the Iris→Lemmy WebFinger/egress gap (136.2), both ops/Lemmy-side, not Iris code
  gaps. The Iris-side contract (a parent delete federates to every copy-holder and collapses the thread there)
  is what 136.9 implements + pins, against a synthetic remote peer (instance B) as the Lemmy stand-in.

## Verification

- `CrossInstanceDeleteThreadCollapseIntegrationTests`: **1 passed** (a parent deleted on the home A tombstones
  the remote follower B's copy **and** removes the `m1 → r1` reply edge there — the thread collapses on both
  instances).
- **Non-vacuity check** — the test **fails** (the `Assert.Empty(GetRepliesAsync(m1))` on B) when the
  parent-collapse fix is disabled (the remote instance B's reply edge is not collapsed; the orphaned reply
  r1 remains in B's `/replies` collection for m1), confirming the test exercises the new code path, not a
  pre-existing behavior.
- `DeleteActivityHandlerTests`: **12 passed** (was 10, +2 — the new parent-collapse + leaf-unaffected unit
  tests; no regression in the existing F-12 reply-edge tests).
- Full fast suite: **Iris.Server.Tests 1169 passed, 0 failed** (was 1166, +3 — no regression);
  **Iris.Core.Tests 445 passed, 0 failed**.
- Full solution build: **0 warnings, 0 errors** (`TreatWarningsAsErrors`).
- The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
