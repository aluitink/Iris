# Phase 116.6 — ICacheMetrics: Per-Cache Hit/Miss/Stale Counters

**Date:** 2026-09-13
**Type:** Feature (observability)
**Scope:** `Iris.Core/Caching/`, `Iris.Server/`, `Iris.Client/`

## What Was Built

Added the missing cache hit/miss metrics identified in the Phase 116.2 cache audit. Before this change, `CachingReadThrough<TValue>.GetAsync` computed `WasHit`/`WasStale` but callers discarded them — there was no way to verify caching was effective in production.

### New Types (Iris.Core/Caching/)

| Type | Purpose |
|---|---|
| `ICacheMetrics` | Interface: `Hits`, `Misses`, `StaleHits`, `HitRate`, `RecordHit()`, `RecordMiss()`, `RecordStaleHit()` |
| `CacheMetrics` | Thread-safe `Interlocked`-based implementation |
| `NullCacheMetrics` | No-op singleton (default when no metrics supplied) |

### Wiring

- `CachingReadThrough<TValue>` accepts optional `ICacheMetrics?` (defaults to `NullCacheMetrics`). Records a hit on fresh cache read, a miss on factory invocation, a stale hit on stale-while-revalidate. Exposes `Metrics` property.
- All **7 server** cache façades (`RemoteActorCache`, `RemoteKeyCache`, `CollectionPageCache`, `WebFingerCache`, `LocalActorDocumentCache`, `LocalCollectionPageCache`) accept optional `ICacheMetrics?` and expose `Metrics`.
- All **4 client** cache façades (`ActorCache`, `KeyCache`, `WebFingerCache`, `CollectionPageCache`) same pattern.
- `ActivityPubServerOptions.CacheMetrics` — set a shared `CacheMetrics` instance to collect counters across all server caches.
- DI registrations in `ActivityPubServerExtensions` wire `options.CacheMetrics` into each cache.

### New Endpoint

`GET /ap/v1/diagnostics/caches` — no auth (like `/health`). Returns:

```json
{
  "caches": {
    "remoteActors": { "hits": 142, "misses": 8, "staleHits": 2, "hitRate": 0.942, "entries": 5 },
    "remoteKeys": { ... },
    "remoteCollectionPages": { ... },
    "webFinger": { ... },
    "localActorDocuments": { ... },
    "localCollectionPages": { ... }
  }
}
```

## Files Changed

**Iris.Core/Caching/:**
- `ICacheMetrics.cs` (new)
- `CacheMetrics.cs` (new)
- `NullCacheMetrics.cs` (new)
- `CachingReadThrough.cs` (modified — metrics param + recording)

**Iris.Server/:**
- `ActivityPubServerOptions.cs` (modified — `CacheMetrics` property)
- `ActivityPubServerConstants.cs` (modified — `DiagnosticsRouteSegment`)
- `ActivityPubServerExtensions.cs` (modified — DI wiring + `CacheDiagnosticsHandler`)
- `Security/RemoteActorCache.cs`, `Security/RemoteKeyCache.cs`, `Security/LocalActorDocumentCache.cs`, `Caching/CollectionPageCache.cs`, `Caching/LocalCollectionPageCache.cs`, `Caching/WebFingerCache.cs` (modified — metrics param + `Metrics` property)

**Iris.Client/:**
- `Caching/ActorCache.cs`, `Caching/KeyCache.cs`, `Discovery/WebFingerCache.cs`, `Collections/CollectionPageCache.cs` (modified — metrics param + `Metrics` property)

**Tests:**
- `tests/Iris.Core.Tests/Caching/CacheMetricsTests.cs` (new — 12 tests)
- `tests/Iris.Server.Tests/Observability/CacheDiagnosticsEndpointTests.cs` (new — 3 tests)

## Verification

- Build clean (0 warnings, 0 errors)
- 1,819 tests pass (0 failures; 1 known flaky federation test passes in isolation)
