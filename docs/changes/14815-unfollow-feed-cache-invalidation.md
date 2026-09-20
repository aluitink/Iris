# 14815 — Un-follow: invalidate the per-actor feed cache

**Status:** done
**Slice:** Dev Queue Feature — ⑤ Mastodon un-follow fan-out (un-follower's home feed still shows the unfollowed user's posts)
**Owner:** dev

## Problem

When a local actor un-follows a remote actor (an `Undo` of a `Follow`), the un-follower's **home
feed** (`GET /ap/v1/u/{handle}/feed`) kept showing the unfollowed user's posts for up to the feed
cache's TTL (30 s). QA observed the un-follower "still sees messages from an unfollowed user" after
an un-follow.

The home feed is built by `FeedService` (F-14) from the actor's **current** `following` set (the
union of the follows' outboxes). It is served through a per-actor server-side cache
(`FeedService._feedCache`, `ConcurrentDictionary<Iri, (items, builtUtc)>`, 30 s TTL, with a
`?refresh=true` bypass). When the un-follow removed the follow edge, **nothing invalidated that
actor's cache entry**, so the next non-`?refresh` `/feed` read served the stale (pre-un-follow)
merged feed for the rest of the TTL. The same gap applied to a *new* follow (the new follow's posts
were absent for up to the TTL) and, transitively, to any follow-edge change (block/mute a follow).

The delivery side of the un-follow was already correct (the QA re-verify in change 993 confirmed the
forward edge is removed, the `following` collection drops to `totalItems: 0`, the `Undo` is
delivered, and Mastodon's `StatusReachFinder` no longer targets the un-follower). The remaining
defect was purely the **client-facing feed cache**.

## Fix

Add a feed-cache invalidation seam and call it at the follow-edge write sites in the local outbox
handler (the path that records/removes a local actor's own follow edge).

1. **Interface** — `IFollowFeedService.InvalidateActorFeedCache(Iri actorIri, CancellationToken ct)`
   (a `virtual` no-op default, so an implementation without a server-side cache — or a test double —
   need not override it).

2. **Implementation** — `FeedService.InvalidateActorFeedCache` drops the actor's entry from
   `_feedCache` (`TryRemove`), so the next `/feed` read rebuilds from the stores.

3. **Call sites** — `OutboxPublishHandler` (the person outbox write surface). In the
   moderation/collection-invalidation switch (which already resolves the undone sub-activity):
   - `case Follow` → `followFeed.InvalidateActorFeedCache(actorIri, ct)` (the following set gained a
     target).
   - `case Undo` → when the undone sub-activity is a `Follow` → `followFeed.InvalidateActorFeedCache(actorIri, ct)`
     (the following set **lost** a target — the un-follow case).

   `actorIri` is the un-follower (the viewer whose `/feed` is read), the actor whose edge changed.
   The remote target's cache is not on this instance (the target is remote), so only the local
   viewer's entry is dropped.

The community outbox handler and the inbound handlers are not touched: the per-actor **person** feed
is keyed by the local viewer, and the un-follower scenario (QA ⑤) is the local actor un-following a
remote actor — exactly the person-outbox path. Inbound handlers record a *remote* actor's edge
(their feed is on the remote instance), not a local viewer's.

## Verification

- **Regression test** `tests/Iris.Server.Tests/Services/FeedServiceTests.cs`
  (`Feed_Cache_InvalidateActorFeedCache_DropsOnlyThatActor`): two local actors (alice, carol) both
  follow bob (who has one post). Warms both feed caches, adds a second bob post, and asserts:
  neither sees the new post (both cached) → after `InvalidateActorFeedCache(alice)`, **alice's**
  feed rebuilds with 2 items while **carol's** stays cached at 1. Confirms the invalidation drops
  only the named actor's entry (the un-follower seam). Also asserts a no-op invalidation of an
  unknown actor does not throw.
- **No regressions:** all `FeedServiceTests` pass (53), and the full fast suite is green.
- **Full fast suite** (`dotnet test --filter "Category!=Slow"`): **1402 passed, 0 failed** (1 new test).

## Deploy

Rebuilt + redeployed `iris-web` (healthy). The un-follower's `/feed` now rebuilds immediately on the
next read after an un-follow, instead of serving the stale merged feed for the TTL.

## Files

- `src/Iris.Server/Services/IFollowFeedService.cs` — `InvalidateActorFeedCache` (default no-op).
- `src/Iris.Server/Services/FeedService.cs` — `InvalidateActorFeedCache` (`_feedCache.TryRemove`).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `OutboxPublishHandler` new `IFollowFeedService`
  parameter + invalidation in the `Follow` and `Undo`(of `Follow`) cases.
- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` — `Feed_Cache_InvalidateActorFeedCache_DropsOnlyThatActor`.
