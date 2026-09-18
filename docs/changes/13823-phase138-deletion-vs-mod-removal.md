# 138.23 — Deletion vs. moderator-removal semantics

## What changed

`DeleteActivityHandler` now distinguishes an author's own delete from a community moderator's
removal, and records the distinction on the resulting tombstone.

### Before

A remote actor's `Delete` was accepted **only** when the actor was the object's `attributedTo`
owner. A Lemmy moderator who removes a community member's post sends a `Delete` with the
moderator as actor; the old guard rejected this, so the locally-archived copy was never
tombstoned.

### After

The owner guard is relaxed: a remote actor's `Delete` is accepted when the actor is either
(a) the object's `attributedTo` owner (author delete) **or** (b) a member of a community
referenced in the object's `to`/`cc` array (moderator removal). Since Iris does not model a
dedicated moderator role, community membership is the proxy for "this actor has moderation
authority over this community's content."

When the deleter is **not** the owner (case b), the tombstone carries the new
`iris:removedBy` extension (the deleting actor's IRI). This allows the UI to distinguish
"deleted by author" (no `removedBy` — a permanent, irreversible delete) from "removed by
moderator" (`removedBy` present — a moderation action that may be reversible via a future
restore path).

### `iris:removedBy` extension term

Added to `IrisExtensionTerms` as `RemovedBy` (wire key `{NamespaceIri}removedBy`). Rendered
on a `Tombstone` document only when the deleter is not the object's `attributedTo` owner.
Cacheable, per-object (tombstones are permanent).

### Outbox cleanup fix

For a mod-removal, the deleting actor is the moderator, not the author. The `Create`
activity was recorded in the **author's** outbox (the object's `attributedTo`), not the
moderator's. The handler now resolves the outbox owner from the stored object's
`attributedTo` (via `ResolveOutboxOwnerIri`) rather than using the activity's actor, so the
correct outbox entry is removed.

## Files changed

| File | Change |
|---|---|
| `src/Iris.Core/IrisExtensionTerms.cs` | Added `RemovedBy` extension term. |
| `src/Iris.Server/Inbox/DeleteActivityHandler.cs` | Relaxed owner guard to accept community-member deletes; added `IsCommunityMemberOfAssociatedCommunityAsync`; added `ResolveOutboxOwnerIri`; set `iris:removedBy` on tombstone for mod-removals. |
| `tests/Iris.Server.Tests/LemmyDeletionSemanticsIntegrationTests.cs` | 5 integration tests (new). |

## Tests

5 integration tests in `LemmyDeletionSemanticsIntegrationTests`:

1. **`RemoteAuthor_DeletesOwnPost_PermanentTombstone_NoRemovedBy`** — author delete produces
   a plain tombstone with no `iris:removedBy`.
2. **`RemoteMod_RemovesCommunityPost_TombstoneHasRemovedBy`** — community-member (mod) delete
   is accepted; tombstone carries `iris:removedBy` = the moderator's IRI.
3. **`RemoteStranger_DeletesCommunityPost_Rejected_NoTombstone`** — non-member, non-owner
   delete is rejected; the post is unchanged.
4. **`RemoteMod_RemovesPost_ReplyEdgeCleanedUp`** — mod-removal collapses the parent's reply
   edges (same as author delete).
5. **`RemoteAuthor_DeletesOwnPost_ReplyEdgeCleanedUp`** — author delete collapses the
   parent's reply edges (regression check).

All 5 pass. Full suite: 1257 passed, 1 known flaky (`MutualPeeringHandshakeIntegrationTests`,
passes in isolation).

## Design notes

- **Community membership as moderator proxy.** Iris does not model a dedicated moderator
  role. The pragmatic approach is to treat any community member as having authority to
  remove content in that community. This is broader than Lemmy's actual moderator set, but
  safe: a non-member's delete is still rejected, and the `iris:removedBy` field records who
  actually removed the content for audit purposes. A future phase can tighten this to a
  `iris:moderator` extension term or a dedicated `IModerationStore`.
- **No restore path yet.** The 138.23 plan mentions "handle a reversible mod-removal without
  destroying the ability to restore it." The `iris:removedBy` field is the first step: it
  marks the tombstone as a mod-removal (restorable) vs. an author-delete (permanent). The
  actual restore path (a Lemmy `Undo(Delete)` or `Create` re-assertion that re-animates the
  tombstoned object) is deferred to 138.25 (extension-based metadata parity), which will
  design the full removed/deleted distinction alongside `nsfw`, `locked`, `featured`, etc.
- **`ResolveOutboxOwnerIri`.** For a mod-removal, the `Create` was recorded in the author's
  outbox, not the moderator's. Using the activity's actor (the moderator) for
  `RemoveFromOutboxAsync` would be a no-op (the moderator's outbox never had the entry).
  Resolving the owner from the stored object's `attributedTo` ensures the correct outbox
  entry is removed.
