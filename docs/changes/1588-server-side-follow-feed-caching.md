# 1588 — Server-side follow-feed caching (feed load feel)

**Slice:** ① Minor UI fixes — feed load feel (item 4 of `docs/changes/1000-inbox-1-card-header-polish.md`)
**Date:** 2026-09-20
**Status:** COMPLETE

## Problem

`GET /u/{handle}/feed` re-walked every follow's outbox (local store reads + remote HTTP fetches) on
every request. For an actor with N follows, the first page load and every subsequent poll (tab switch,
pull-to-refresh) paid the full merge cost. The client-side `ActorCache` + `CollectionPageCache` (added
in 147.2) reduced remote re-fetches, but local store reads and the merge/dedup pass still ran on every
request.

## Solution

Added a **server-side per-actor feed cache** in `FeedService`:

- `ConcurrentDictionary<Iri, (List<IObjectOrLink> Items, DateTime BuiltUtc)>` — one entry per actor.
- **TTL: 30 seconds.** Within the window, repeated `GetFeedAsync` calls for the same actor return the
  cached list without re-reading any outbox.
- The cached list is the **pre-thread-filter, pre-query-filter, pre-visibility-filter** union (the full
  merged feed). All per-request filters (threadDepth, query, activityType, visibility, source) are
  applied to the cached list per-call, so a single cached entry serves all filter combinations.
- **`?refresh=true` bypass:** the `FollowFeedHandler` passes `bypassCache: true` when the request
  carries `?refresh=true`, forcing a rebuild and cache refresh. This is the escape hatch for
  moderation edge changes (block/mute/unblock/unmute) and new posts.
- **Instance-level (non-static):** each `FeedService` instance holds its own cache. In the production
  DI registration (`TryAddSingleton`), there is one instance per process. In tests, each test constructs
  its own `FeedService` (fresh cache), eliminating cross-test pollution.
- `ClearFeedCache()` instance method for test isolation.

### Files changed

| File | Change |
|------|--------|
| `src/Iris.Server/Services/FeedService.cs` | Added `_feedCache` field, `BuildFeedAsync` (cache check), `BuildFeedUncachedAsync` (original logic, no thread filtering), `ApplyThreadFilter` (per-request), `ClearFeedCache`. `GetFeedAsync` gains `bypassCache` parameter. |
| `src/Iris.Server/Services/IFollowFeedService.cs` | `GetFeedAsync` gains `bool bypassCache = false` parameter. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | `FollowFeedHandler` reads `?refresh=true` via `HasRefreshBypass` and passes it as `bypassCache`. |
| `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` | 5 new cache tests + 1 fix (mute-unmute test clears cache). |
| `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` | `FeedNoteIrisAsync` uses `?refresh=true`. |
| `tests/Iris.Server.Tests/MutesCollectionIntegrationTests.cs` | `FeedNoteIrisAsync` uses `?refresh=true`. |
| `tests/Iris.Server.Tests/FlagsCollectionIntegrationTests.cs` | `FeedNoteIrisAsync` uses `?refresh=true`. |
| `tests/Iris.Server.Tests/CrossInstanceBlockedContentIntegrationTests.cs` | `FeedContainsNoteAsync` uses `?refresh=true`. |

## Tests

- 5 new unit tests in `FeedServiceTests`: same-actor cache hit, different-actor isolation,
  threadDepth per-request, `ClearFeedCache` forces rebuild, query filter per-request.
- 4 integration test files updated to use `?refresh=true` for feed reads after moderation changes.
- Full suite: **1390 passed, 0 failed, 8 skipped.**

## Performance impact

- **First request:** unchanged (full merge).
- **Subsequent requests within 30s:** O(1) cache read + per-request filtering (no store reads, no
  remote fetches, no merge/dedup).
- **After TTL or `?refresh=true`:** full rebuild (same as before).
- For a typical session (poll every 30–60s), the majority of feed requests are now cache hits.
