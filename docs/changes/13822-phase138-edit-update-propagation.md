# 138.22 — Edit/update propagation into the archive

## Purpose

Verify that a Lemmy-side post/comment `Update` updates Iris's locally archived copy, so the
archive doesn't go stale. The `UpdateActivityHandler` (existing, from earlier phases) accepts an
inbound `Update` when the updating actor owns the stored object (the object's `attributedTo`
matches the activity's `actor`), re-stores the updated content under the same IRI, and stamps the
`updated` timestamp.

## Approach

Wrote 5 integration tests in `LemmyUpdatePropagationIntegrationTests` that exercise the
`UpdateActivityHandler` with a remote (Lemmy) author updating a locally-archived copy:

1. **`RemoteAuthor_UpdatesArchivedPost_LocalStoreRefreshed`** — a `Page` post is archived; a
   Lemmy `Update` with new title/body is delivered; the local store has the updated content
   (same IRI, new title/body, original gone).
2. **`RemoteAuthor_UpdatesArchivedComment_LocalStoreRefreshed`** — a `Note` comment is archived;
   a Lemmy `Update` with new content is delivered; the local store has the updated comment.
3. **`NonOwner_RemoteActor_UpdateIsNoOp`** — a different remote actor (not the post's
   `attributedTo`) attempts an update; the local store is unchanged (owner guard).
4. **`Update_TombstonedObject_IsNoOp`** — a post is tombstoned first; a late-arriving `Update`
   does not resurrect it (re-animation guard from 136.19).
5. **`Update_UnknownObject_IsNoOp`** — an `Update` for an object never archived locally is a
   no-op (Updates don't create objects).

## Files changed

- `tests/Iris.Server.Tests/LemmyUpdatePropagationIntegrationTests.cs` (new) — 5 integration tests.

## Decision

No production code change was needed — the `UpdateActivityHandler` already handles the remote
author update case correctly. The owner guard (`IsAttributedTo`) accepts a remote actor's update
when that actor is the stored object's `attributedTo` (the Lemmy author of a federated copy), and
the `actorIsLocal` flag only controls re-propagation (not application). The 5 tests confirm this
behavior is correct for the archival use case.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test tests/Iris.Server.Tests` — 1256 passed, 16 skipped, 0 failed.
