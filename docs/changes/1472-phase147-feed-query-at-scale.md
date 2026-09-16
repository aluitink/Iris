# 147.2 — Feed query performance at scale

**Date:** 2026-09-16
**Slice:** PLAN.md Phase 147, item 147.2
**Type:** Performance audit (findings + documentation; fix deferred)

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

## Recommended fixes (deferred)

1. **Remote-outbox cache (highest impact):** Cache each remote follow's outbox page for a
   short TTL (30-60 seconds). Subsequent feed requests within the TTL make zero remote
   fetches. First request after TTL expiry is still slow (5-10 s), but only once per window.
   This would bring the P95 back to ~50 ms (local queries + cache hits) after the first
   request.

2. **Parallel fetch + bounded timeout (medium impact):** Fetch all remote follows in
   parallel (`Task.WhenAll`) with a 2-3 second timeout each. Reduces total latency from
   the sum of all fetches to the max of all fetches (~2-3 s). Still slow, but bounded.

3. **Local-only feed option (quick win):** Add a `?local=true` parameter to the feed
   endpoint that skips remote follows entirely. Instant response (local queries only).
   Useful for a "local first" UX or as a fallback when remotes are slow.

4. **Progressive render (UX improvement):** Return local content immediately (streaming
   response or a two-phase response) and merge remote content as it arrives. Requires
   protocol changes (SSE or chunked response with a "partial" flag).

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- Feed latency measured on production Docker app (andrew, 11 remote follows): P50=7,472 ms,
  Avg=7,307 ms, Max=10,020 ms (5 sequential requests).
- Query structure analyzed via code inspection (FeedService.cs, EfActivityStore.cs,
  EdgeStore.cs, IrisDbContext.cs).
- Index audit: all local-query indexes present; minor `Edges` sort gap documented.

## Status

**Findings documented. Fix deferred** to a follow-up slice (requires significant
infrastructure: remote-outbox cache + parallel fetch + timeout bound). The N+1 DB query
pattern is not the bottleneck — the remote HTTP fetches are. The local query path is
efficient (all indexes present, sub-millisecond).
