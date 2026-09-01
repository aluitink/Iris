# 154 — Community collections through the page cache, with a `?refresh=true` bypass

> 2026-09-01 · Slice 19.5.5 (community peers — feed correctness) · the last open item: `?refresh=true` cache bypass for the community feed (and, by consistency, the community collections)

## What was built

**19.5.5** asks that the community's unified feed "yields exactly the right activities … pagination, and
`?refresh=true` cache bypass." Two of those were already done in earlier slices:

- the **newest-first merge** (de-duplicated, `(outbox position, member IRI)` ordering) — fixed + pinned
  by change 149 (`CommunityFeedCorrectnessIntegrationTests`);
- the **remote-content half** (content delivered to the community inbox and propagated into member
  outboxes) — pinned by `RemoteContent_ToCommunityInbox_PropagatesToMemberAndAppearsInFeed`
  (`CommunityFollowingIntegrationTests`).

What remained open was the **`?refresh=true` cache bypass**. The community feed (and the other community
collections) were served *uncached*: every read re-rendered from the store and every response carried a
`max-age=60, stale-while-revalidate=300` `Cache-Control` header with **no** `?refresh=true` handling.
That was inconsistent with every other Iris collection — the actor collections
(`CollectionEndpointHandler`, `GET /u/{handle}/{collection}`) and the community outbox
(`CommunityOutboxHandler`) both serve through the `LocalCollectionPageCache` and honor
`?refresh=true`.

This slice closes the gap by routing **all** the community collections through the same cache, so the
community feed honors the identical `Cache-Control` + `?refresh=true` contract as the rest of the server:

- **The community collections are served through `LocalCollectionPageCache`.** The shared
  `CommunityCollectionEndpointAsync` core now takes the cache, renders the page document on a miss (or a
  `?refresh=true` read), and serves hits from the cache. It carries the collection `Cache-Control`
  (`max-age=60, stale-while-revalidate=300`) on a plain read and `no-cache` on a `?refresh=true` read.
  This applies to **members**, the **feed**, **following/followers**, and the **moderation** collections
  (`blocks`/`flags`/`mutes`) — consistent with the actor collections, which cache *all* of an actor's
  collections (outbox, following, followers, inbox, blocks, flags, mutes).
- **The cache key carries the feed's `?q` content filter (F-23).** A `?q=…` read of a page renders a
  different item set than an unfiltered read of the same page, so the filter is appended to the page IRI
  used as the cache key. Without this, a filtered read would serve a stale unfiltered page (or vice
  versa).
- **A newly-recorded item is visible within the TTL and immediately with `?refresh=true`.** As with the
  actor collections, a write (a member post, a new member, a new follow, a new moderation edge) does not
  invalidate the cached page; the page is re-rendered when it expires (≤ `max-age=60`) or when a client
  reads it with `?refresh=true` (which bypasses the read *and* writes back a fresh entry).

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `CommunityCollectionEndpointAsync` now takes a
  `LocalCollectionPageCache`, reads `?refresh=true`, builds the cache-key page IRI (with the `?q`
  suffix when present), serves via `collectionCache.GetAsync`, and emits `NoCacheCacheControl` /
  `CollectionCacheControl`. The four callers — `CommunityMembersHandler`, `CommunityFeedHandler`,
  `CommunityCollectionHandler` (following/followers), and `CommunityModerationCollectionHandler`
  (blocks/flags/mutes) — plus their four route registrations now inject the cache.
- `src/Iris.Server/Caching/LocalCollectionPageCache.cs` — unchanged (reused as-is; `TTL` 60s,
  `stale-while-revalidate` 300s, `GetAsync(pageIri, refresh, render, ct)` returns
  `(Value, WasStale, WasHit)`).
- `tests/Iris.Testing/JsonDoc.cs` — new `ItemIdsOf(string)` helper (parse a collection body → the item
  IRIs), a convenience over `GetItems` + `ItemId` for asserting on a raw response body.
- `tests/Iris.Server.Tests/CommunityModerationIntegrationTests.cs` — the post-mute/block feed reads now
  pass `?refresh=true` (the first read of a page is a cache miss; a read that must observe a just-made
  change must bypass the cache); **new** test
  `CommunityFeed_IsServedFromThePageCache_WithRefreshBypassAndCacheControl` (see below); a
  `CacheControlOf` helper reads the raw `Cache-Control` header.
- `tests/Iris.Server.Tests/CommunityMembershipManagementIntegrationTests.cs` — the post-Add/Remove
  feed + members reads now pass `?refresh=true` (the same pre-/post-mutation pattern).

## Tests

1248 → **1249** passing (+1: the community-feed cache-pinning integration test).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed files.

- `CommunityFeed_IsServedFromThePageCache_WithRefreshBypassAndCacheControl` — the central 19.5.5
  assertion: a plain first read is a cache miss that renders from the store and advertises
  `max-age=60, stale-while-revalidate=300`; a second plain read is a cache hit (still cacheable); after a
  community mute (the store changes) a **plain third read still serves the stale pre-mute page** within
  the TTL (a plain read never re-renders); a **`?refresh=true` read bypasses the cache and re-renders**
  (the muted member's post is now excluded) and advertises `no-cache`; and the refresh **write-back**
  replaced the stale entry, so a subsequent plain read now serves the fresh post-mute feed.
- `Mute_ExcludesMemberContentFromCommunityFeed_WithoutSeveringMembership` /
  `Block_ExcludesMemberContentFromCommunityFeed` — the post-mutation feed reads now use `?refresh=true`
  so they observe the fresh (post-mute/block) feed rather than the stale cached page.
- `Add_SignedByCommunity_…ReflectsInFeedAndMembers` / `Remove_SignedByCommunity_…ReflectsInFeedAndMembers`
  — the post-Add/Remove feed + members reads now use `?refresh=true` so they observe the fresh
  membership state rather than a stale cached page.

## Live verification (deferred — a UI/live item)

The cache + `?refresh=true` + `Cache-Control` behavior is pinned by the new test (status codes, the
stale-within-TTL read, the refresh bypass, the write-back, and the exact `Cache-Control` values). The
**UI** half (a community feed screen in the sample client that issues `?refresh=true` on a manual
refresh) is the remaining live/UI item for 19.5.5 — a client-side wiring detail, not a server concern.

## Decisions

- **All community collections are cached (not just the feed).** The actor `CollectionEndpointHandler`
  caches *every* actor collection (outbox, following, followers, inbox, blocks, flags, mutes); the
  community collections mirror that. Caching only the feed would leave the community's members /
  following / followers / moderation collections behaving differently from their actor-level
  counterparts for no reason. One shared core (`CommunityCollectionEndpointAsync`) keeps the contract
  identical across all five.
- **The `?q` filter is part of the cache key.** The community feed is the only community collection with
  a content filter (F-23), and a filtered read renders a different item set than an unfiltered read of
  the same page. Folding `?q` into the page IRI used as the key means a filtered and an unfiltered read
  of the same page are distinct cache entries — without it a `?q=…` read could return a stale unfiltered
  page (or vice versa).
- **No write-path invalidation; rely on TTL + `?refresh=true`.** This is the established Iris
  collection-cache contract: a write does not invalidate the cached page (the page is re-rendered when
  it expires, ≤ `max-age=60`, or on an explicit `?refresh=true`). It is consistent with the actor
  collections and the community outbox, and keeps the write path free of cache coupling. The trade-off
  (a plain reader sees a change up to 60s late) is exactly the one the actor collections already make,
  and `?refresh=true` gives a client an immediate bypass.
