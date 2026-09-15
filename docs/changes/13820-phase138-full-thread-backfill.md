# 138.20 — Full-thread backfill on first peer

## Problem

The community feed's peering branch (`CommunityFeedService.GetFeedAsync`) walked a followed
actor's remote outbox over the wire and returned items in the feed response, but did **not**
persist the embedded objects to the local object store or record the `Create` activities in the
community's local members' outboxes. This was a proxy read only: if the remote went offline,
the historical content would no longer be available.

## Solution

Added `PersistRemoteOutboxItemsAsync` to `CommunityFeedService`. After the peering-branch walk
collects remote outbox items, each item is processed:

1. **Unwrap the relay envelope**: `Announce` items whose `object` is an embedded `Create` are
   unwrapped (the Lemmy relay pattern). Bare `Create` items are used directly.
2. **Store the content object**: the embedded `Page`/`Note`/`Article` is stored in the local
   object store via `PutObjectAsync`, guarded by `TryGetObjectAsync` (idempotent — already-stored
   objects are skipped).
3. **Record the `Create` in members' outboxes**: the `Create` activity is added to each local
   member's outbox via `AddToOutboxAsync` (last-write-wins upsert keyed on activity IRI, so a
   second feed read does not duplicate).

The `MergeContributorOutboxAsync` method was updated to collect remote (wire-fetched) items into
an optional `remoteOutboxItems` list, passed from `GetFeedAsync`'s peering branch.

## Files changed

- `src/Iris.Server/Services/CommunityFeedService.cs` — `GetFeedAsync` peering branch now collects
  remote items and calls `PersistRemoteOutboxItemsAsync`; `MergeContributorOutboxAsync` gained an
  optional `remoteOutboxItems` parameter; new `PersistRemoteOutboxItemsAsync` method.
- `tests/Iris.Server.Tests/CommunityBackfillIntegrationTests.cs` (new) — 2 integration tests.

## Tests

- `Backfill_RemoteOutboxItems_PersistedToLocalStore` — verifies the backfilled `Page` is in the
  local object store and the `Create` is in the local member's outbox after the first feed read.
- `Backfill_IsIdempotent_SecondReadDoesNotDuplicate` — verifies a second feed read does not
  duplicate the `Create` in the member's outbox.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test tests/Iris.Server.Tests` — 1246 passed, 16 skipped, 0 failed.
