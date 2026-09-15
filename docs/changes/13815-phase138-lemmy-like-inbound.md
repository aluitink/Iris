# 13815 — Lemmy → Iris Likes (138.15)

## Summary

Verified that Iris's existing `LikeActivityHandler` correctly records a Lemmy upvote on a synced
post (stored in Iris's object store). No new production code was required — the handler already
records the like edge when the liked object is locally stored, regardless of the liker's locality
(remotes work identically to locals). This mirrors the Mastodon like path (31.10).

## Verification

Three integration tests (`LemmyLikeInboundIntegrationTests`):

1. **`LemmyUpvoteOnSyncedPost_LikeEdgeRecorded`** — a remote Lemmy actor likes a `Page` stored in
   Iris's object store; the like edge is recorded in the `ILikeStore` (the post's likers include the
   Lemmy actor).

2. **`LemmyUpvoteOnCommunityPost_RecordedInMembersOutboxes`** — a remote Lemmy actor likes a
   community post (delivered to the community's inbox); the like edge is recorded AND the like
   appears in the community's local members' outboxes (the community-feed path via
   `CommunityContentRecorder.RecordToMembersAsync`).

3. **`LemmyUpvoteOnRemotePost_NotRecordedLocally`** — a remote Lemmy actor likes a post NOT stored
   in Iris's object store; no like edge is recorded locally (the edge belongs on the object's
   author's home instance).

All 3 tests pass.

## Why no production code change

The `LikeActivityHandler` (src/Iris.Server/Inbox/LikeActivityHandler.cs) already handles the
Lemmy case correctly:
- Line 95-99: if the liked object is in the local object store (`TryGetObjectAsync`), the like edge
  is recorded via `ILikeStore.RecordLikeAsync`. This is independent of the liker's locality — a
  remote Lemmy actor's like of a locally-stored object is recorded, exactly as a remote Mastodon
  actor's like would be.
- Line 106-116: if the recipient is a local community, the like is recorded in each local member's
  outbox via `CommunityContentRecorder.RecordToMembersAsync`.

## Files

- `tests/Iris.Server.Tests/LemmyLikeInboundIntegrationTests.cs` (new) — 3 integration tests
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.15 marked `[x]`
