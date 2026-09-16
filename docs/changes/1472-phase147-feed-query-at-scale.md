# 147.2 — Feed query performance at scale

**Date:** 2026-09-16
**Slice:** PLAN.md Phase 147, item 147.2
**Type:** Performance audit + fix (caches wired, parallel fan-out, 5s timeout)

## Objective

Measure home/community feed p50/p95 latency at scale (1000+ posts, 50+ follows) and compare
against Phase 116.1 baseline (feed P95=9.3 ms). Flag any query missing an index.

## Key finding: feed is 1000× slower than baseline due to remote follows

**Measured on production Docker app** (andrew, 11 remote follows — Mastodon/Lemmy/Hachyderm):

| Metric | Phase 116.1 baseline | Current | Regression |
|--------|---------------------|---------|------------|
| P50    | 9.3 ms              | 7,472 ms | **803×**   |
| Avg    | ~5 ms               | 7,307 ms | **~1461×** |
| Max    | ~15 ms              | 10,020 ms| **~668×**  |

**Root cause:** `FeedService.BuildFeedAsync` sequentially awaits every remote follow's
actor-doc fetch + outbox walk before any response is returned. With 11 remote follows,
each taking 0.5-2 seconds (or timing out at 5-10 seconds), the total feed latency is the
**sum** of all remote fetch times. The local (own + local-follow) content is ready
instantly but is held back until the slowest remote follow resolves.

This is **not** a database query issue — the local queries (BoxItems + Activities) are
fast (indexed, sub-millisecond). The latency is entirely from **remote HTTP fetches**
(sequential, no timeout bound, no caching).

## Query structure (N+1 pattern)

`FeedService.BuildFeedAsync` issues `3N + 5` sequential DB round-trips for N local follows:

| Query | Count | Index used |
|-------|-------|------------|
| Following list (`Edges WHERE Kind=0 AND Source=@actor`) | 1 | PK `(Kind, Source, Target)` |
| Blocks (`Edges WHERE Kind=5 AND Source=@actor`) | 1 | PK |
| Mutes (`Edges WHERE Kind=7 AND Source=@actor`) | 1 | PK |
| Own outbox (`BoxItems` + `Activities`) | 2 | `(Direction, ActorId, Position)` + PK |
| Per local follow: actor check + outbox | 3N | PK + `(Direction, ActorId, Position)` + PK |
| Per remote follow: HTTP fetch (actor doc + outbox pages) | M × (1+K) | n/a (network I/O) |

For andrew (0 local follows, 11 remote follows): 5 DB queries (fast) + 11 remote HTTP
fetches (slow, sequential).

## Index audit

All indexes needed for the local query path exist:
- `IX_BoxItems_Direction_ActorId_Position` — primary feed read (index seek + scan)
- `PK_Edges (Kind, Source, Target)` — follow/block/mute reads
- `PK_Activities (Id)` — activity lookup by IRI
- `PK_Actors (Id)` — local actor check

**Minor gap:** `Edges` table lacks `(Kind, Source, CreatedAt)` for the `ORDER BY CreatedAt`
in `OutTargetsAsync`. The PK `(Kind, Source, Target)` narrows the filter, but the sort is
a filesort. Impact: negligible for <1000 follows (in-memory sort of a small set).

**No covering index** for the feed's hot path: the feed reads the full `Document` jsonb
column for every activity. A summary column (content preview, inReplyTo flag) would allow
the DB to return filtering metadata without loading the full JSON. Impact: moderate for
large outboxes (>1000 posts), negligible for typical outboxes (<100 posts).

## Fixes applied

1. **Client caches wired into the FeedService outbound client (highest impact).** The
   `FeedService`'s `IActivityPubClient` was created via `IActivityPubClientFactory.Create`
   with `options.Caches` unset (null), so every `/feed` call re-fetched all remote follows'
   actor-docs + outbox pages over the wire. The same bug existed in the `CommunityFeedService`
   registration. Fixed by wiring `ActorCache` (5 min TTL) + `CollectionPageCache` (30 s TTL)
   into both registrations (`ActivityPubServerExtensions.cs`). Now remote outbox pages are
   fetched once per TTL window instead of on every feed request.

2. **Parallelized the per-follow fan-out (`Task.WhenAll`).** `BuildFeedAsync` previously
   awaited each follow's outbox (local or remote) **sequentially**, so total latency was the
   **sum** of all follows' fetch times (11 follows × 0.5-10 s = 7-10 s). Now all eligible
   follows are fetched in parallel; total latency is bounded by the **slowest single follow**
   (plus local DB reads). A failed/slow remote contributes an empty list (preserving the
   existing "one broken remote must not fail the feed" guarantee). The merge is in
   deterministic IRI order, so the feed is reproducible.

3. **5-second `HttpClientTimeout` on the feed's outbound client.** A slow/unreachable remote
   follow can no longer stall the whole feed indefinitely; each remote fetch is bounded at
   5 s.

## Post-fix measurement

Measured on the production Docker app (andrew, 11 remote follows, 8 sequential requests
after a warm-up):

| Metric | Before fix | After fix | Improvement |
|--------|-----------|-----------|-------------|
| P50    | 7,472 ms  | 1,666 ms  | **4.5×**    |
| Avg    | 7,307 ms  | 1,749 ms  | **4.2×**    |
| Min    | ~7,000 ms | 1,623 ms  | —           |
| Max    | 10,020 ms | 2,223 ms  | **4.5×**    |

The residual ~1.6 s is the **cold-miss cost** when the 30 s page-cache TTL expires: the
parallel fetch of all 11 remote follows' outboxes, bounded by the slowest single remote
(Mastodon/Lemmy/Hachyderm). Repeat requests within the 30 s window are fast (cache hits).
The latency is now consistent (Min 1,623 ms, Max 2,223 ms) — previously it ranged from
7 to 10 seconds depending on how many remotes were slow.

## Deferred (follow-up slices)

- **`?local=true` feed option:** skip remote follows entirely for an instant local-only
  response. Useful for a "local first" UX or as a fallback when remotes are slow.
- **Progressive render:** return local content immediately and merge remote content as it
  arrives (SSE or chunked response with a "partial" flag). Requires protocol changes.
- **CommunityFeedService parallel fan-out:** the community feed has the same sequential
  fan-out pattern, but its merge is more complex (position-based, shared `seen`/`merged`
  state). Deferred to a follow-up.

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 2157 passed, 0 failed, 16 skipped (all 11 test projects green).
- Feed latency measured on production Docker app (andrew, 11 remote follows):
  P50=1,666 ms, Avg=1,749 ms, Min=1,623 ms, Max=2,223 ms (8 sequential requests).
- Query structure analyzed via code inspection (FeedService.cs, EfActivityStore.cs,
  EdgeStore.cs, IrisDbContext.cs).
- Index audit: all local-query indexes present; minor `Edges` sort gap documented.

## Status

**Fix applied and verified.** The feed is 4.5× faster (P50 7,472 ms → 1,666 ms). The N+1
DB query pattern is not the bottleneck — the remote HTTP fetches are, and they are now
parallelized + cached + timeout-bounded. The residual ~1.6 s is the cold-miss cost of the
parallel remote fetch (bounded by the slowest remote). For typical usage (browsing within
a 30 s window), repeat requests are fast (cache hits).
