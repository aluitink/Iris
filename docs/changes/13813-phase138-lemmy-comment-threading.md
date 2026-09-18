# 138.13 — Lemmy comment threading in Iris

**Date:** 2026-09-15
**Phase:** 138 (Lemmy community federation)
**Slice:** 138.13 — Lemmy comment surfaces in Iris as a threaded reply

## Summary

Verified that a Lemmy `Note` comment (a `Create(Note)` where the `Note` has `inReplyTo` pointing to a
parent post or comment) threads correctly under the synced post in Iris. The existing
`CreateActivityHandler` already handles this path — no code changes were needed.

## How it works

When a Lemmy community member posts a comment, Lemmy sends a `Create(Note)` activity to the
community's inbox (or sharedInbox), where the `Note` has:
- `inReplyTo`: a link to the parent (either a `Page` post or another `Note` comment)
- `attributedTo`: the commenting user's IRI

The `CreateActivityHandler` (when the recipient is a local community) does the following:

1. **Stores the `Note` in the object store** (`StoreEmbeddedObjectAsync`): the `Note` is stored under
   its own IRI, so it can be served by direct fetch.

2. **Records the reply edge** (F-12 threading): when the stored object has `inReplyTo` set, the
   handler calls `GetParentIri()` to extract the parent IRI, then records the `parent → child` edge
   in the replies store via `RecordReplyAsync`. This is what makes the comment appear under its
   parent in the thread view.

3. **Records the `Create` in the community's local members' outboxes** (via
   `CommunityContentRecorder.RecordToMembersAsync`): the `Create` activity (with the embedded `Note`)
   is added to each local member's outbox, so it appears in the community feed.

4. **Derives the `conversationId`** (Phase 57.3): for a reply, the `conversationId` is set to the
   parent's `conversationId` (or the parent's IRI if the parent has no `conversationId`), so the
   reply anchors to the correct thread root.

## Changes

None. The existing code already handles this path correctly.

### `tests/Iris.Server.Tests/LemmyCommentThreadingIntegrationTests.cs` (new)

3 integration tests:

1. `LemmyComment_InReplyTo_Post_ThreadsUnderParent_AndRecordedInMemberOutbox`: A Lemmy comment
   (`Create(Note)` with `inReplyTo` pointing to a parent `Page`) is stored in the object store, the
   reply edge (parent → child) is recorded in the replies store, and the `Create` is recorded in the
   community's local member's outbox.

2. `LemmyComment_MultiLevelThread_RecordsAllReplyEdges`: A 2-level comment thread (comment → reply
   to comment) records both reply edges (parent → level1, level1 → level2), and both comments are
   stored in the object store and recorded in the member's outbox.

3. `LemmyComment_DeliveredToCommunity_DoesNotAppearAsTopLevelInFeed`: A comment's `Note` carries
   `inReplyTo` (it's a reply, not a top-level post), and the comment is recorded in the member's
   outbox (available for threading).

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test tests/Iris.Server.Tests` — 1229 passed, 16 skipped, 0 failed (including the 3 new
  tests).

## Notes

- This is a verification slice (no code changes). The `CreateActivityHandler` was designed to handle
  inbound `Create` activities delivered to a community's inbox, including replies. The Lemmy comment
  path is a subset of that: a `Create(Note)` with `inReplyTo` is just a reply, and the handler's
  existing F-12 threading logic records the reply edge.
- The live end-to-end test (posting a comment from Lemmy and verifying it appears in the Iris
  thread view) requires a running Lemmy stack and is exercised manually (per the Phase 45+ web test
  policy).
