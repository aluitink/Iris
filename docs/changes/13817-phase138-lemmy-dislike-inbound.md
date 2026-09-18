# 13817 — Lemmy → Iris Dislikes/Downvotes (138.17)

## Summary

Verified that the existing `DislikeActivityHandler` correctly records a Lemmy downvote on a synced
post (stored in Iris's object store), and that `Undo(Dislike)` (handled by the existing
`UndoActivityHandler`) removes the edge. The handler mirrors the `LikeActivityHandler`: it records
the dislike edge when the disliked object is locally stored, and when delivered to a community's
inbox, records the dislike in local members' outboxes. No new production code was required beyond
adding XML doc comments to the handler (the handler, store, and Undo path were already implemented).

## What was verified

Four integration tests (`LemmyDislikeInboundIntegrationTests`):

1. **`LemmyDownvoteOnSyncedPost_DislikeEdgeRecorded`** — a remote Lemmy actor downvotes a `Page`
   stored in Iris's object store; the dislike edge is recorded in the `IDislikeStore`.

2. **`LemmyDownvoteOnCommunityPost_RecordedInMembersOutboxes`** — a remote Lemmy actor downvotes a
   community post (delivered to the community's inbox); the dislike edge is recorded AND the dislike
   appears in the community's local members' outboxes.

3. **`LemmyDownvoteOnRemotePost_NotRecordedLocally`** — a remote Lemmy actor downvotes a post NOT
   stored in Iris's object store; no dislike edge is recorded locally.

4. **`UndoDislike_RemovesDislikeEdge`** — a previously recorded dislike edge is removed via
   `RemoveDislikeAsync` (the inverse path, exercised by `UndoActivityHandler`'s `Undo(Dislike)`
   branch).

All 4 tests pass.

## Production changes

- `src/Iris.Server/Inbox/DislikeActivityHandler.cs`: added XML doc comments to the constructor and
  `HandleAsync` method (required by the coding style; the handler logic was already complete).

## Files

- `src/Iris.Server/Inbox/DislikeActivityHandler.cs` (modified) — XML doc comments added
- `tests/Iris.Server.Tests/LemmyDislikeInboundIntegrationTests.cs` (new) — 4 integration tests
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.17 marked `[x]`
