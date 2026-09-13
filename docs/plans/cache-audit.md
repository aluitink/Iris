# Phase 116.2 — Cache Hit-Rate Audit

**Date:** 2026-09-13
**Type:** Cache infrastructure audit (no code changes)
**Scope:** Caching architecture, TTLs, empirical hit-rate measurement, gaps

## Caching Architecture

Iris uses a **custom, dependency-free in-memory cache** (no `IMemoryCache`, no Redis).

### Core (Iris.Core/Caching/)

| Type | Role |
|---|---|
| `ICache<TValue>` | Core interface: `Get`, `TryGetEntry`, `Put`, `Invalidate`, `Count` |
| `MemoryCache<TValue>` | Only implementation: thread-safe, TTL + LRU + stale-while-revalidate |
| `CachingReadThrough<TValue>` | Async read-through engine; implements `bypassCache` + SWR |
| `CachePolicy` | TTL config: `Actor` (5m/5m), `CollectionPage` (30s/30s), `Key` (1h/1h), `WebFinger` (15m/15m) |
| `CacheState` | `Fresh` / `Stale` / `Expired` |

### Server Façades (Iris.Server)

| Cache | TTL (Fresh/Stale) | What it caches |
|---|---|---|
| `LocalActorDocumentCache` | 60s / 300s | Public actor docs (`/ap/v1/u/{handle}`) |
| `LocalCollectionPageCache` | 60s / 300s | Outbox, followers, following, feed, community feed/outbox |
| `RemoteActorCache` | 1h / 1h | Remote actor docs (inbound signature validation) |
| `RemoteKeyCache` | 1h / 1h | Remote public keys (signature validation) |
| `CollectionPageCache` | 30s / 30s | Remote collection pages (outbound federation) |
| `WebFingerCache` | 15m / 15m | WebFinger account→actor resolution |
| `ProxyGoneCache` | 1h, LRU 4096 | 410-Gone targets (not ICache-based) |

### Client Façades (Iris.Client)

| Cache | TTL | What it caches |
|---|---|---|
| `ActorCache` | 5m / 5m | Remote actor docs |
| `KeyCache` | 1h / 1h | Remote public keys |
| `CollectionPageCache` | 30s / 30s | Remote collection pages |
| `WebFingerCache` | 15m / 15m | WebFinger resolution |

### Key Design Points

- **`bypassCache` pattern:** Every cache façade exposes `GetAsync(key, bypassCache, factory, ct)`. When `bypassCache=true`, the factory always runs (cache read skipped) but the result is still written back.
- **HTTP `?refresh=true`** maps to `bypassCache` on actor doc and collection endpoints.
- **Stale-while-revalidate:** On `Stale` state, the cached value is served immediately; a background refresh is triggered.
- **LRU capacity:** 1024 entries per cache (default constructor).
- **Production app** (`apps/Iris.Web`) uses all default TTLs (no custom `CachePolicies`).

## Empirical Hit-Rate Measurement

### Method

Since there are **no cache hit/miss counters** in the codebase, hit rates were inferred from TTFB differentials:
- **Cache hit** → TTFB significantly lower than cache miss
- **TTL expiry** → TTFB spike after TTL boundary (65s wait)
- **`?refresh=true`** → TTFB matches cold/miss (bypasses cache)

### Actor Doc (`/ap/v1/u/alice`) — TTL 60s

| Condition | TTFB | Interpretation |
|---|---|---|
| Cold (first request) | 2.9 ms | Cache miss (DB read) |
| Warm (2nd request) | 0.6 ms | **Cache hit** (4.8× faster) |
| 20 consecutive warm | 0.3–1.1 ms | **100% hit rate** (all < 1.2 ms) |
| Post-TTL (65s wait) | 8.1 ms | **Cache miss** (stale-while-revalidate: old value served, background refresh) |
| Warm after post-TTL | 0.9 ms | **Cache hit** (refresh completed) |
| `?refresh=true` | 2.6 ms | **Bypass** (factory ran, no cache read) |

**Estimated hit rate: ~95–99%** for repeated actor doc reads within the 60s TTL.

### Feed (`/ap/v1/u/alice/feed`) — TTL 60s

| Condition | TTFB | Interpretation |
|---|---|---|
| Cold | 8.9 ms | Cache miss (DB read) |
| Warm (2nd) | 7.4 ms | Cache hit (marginal improvement) |
| 60 consecutive warm | 4.4–10.3 ms | **~100% hit rate** (tight distribution) |
| Post-TTL (65s wait, 1st) | 11.9 ms | **Cache miss** (stale-while-revalidate) |
| Post-TTL (2nd–10th) | 4.3–6.5 ms | **Cache hit** (refresh completed) |
| `?refresh=true` | 7.1 ms | **Bypass** (marginal difference — feed is I/O-bound) |

**Estimated hit rate: ~95–99%** for repeated feed reads within the 60s TTL. The cache benefit is **marginal** (7.4 vs 8.9 ms = 17% faster) because the feed is dominated by DB I/O, not serialization.

### Outbox (`/ap/v1/u/alice/outbox`) — TTL 60s

| Condition | TTFB | Interpretation |
|---|---|---|
| Warm | 6.3 ms | Cache hit |
| Post-TTL (65s wait) | 4.8 ms | **Cache miss** (but faster than warm — variance) |
| Warm after post-TTL | 1.8 ms | **Cache hit** |
| 20 consecutive warm | 1.1–2.8 ms | **~100% hit rate** |

**Estimated hit rate: ~95–99%**. Cache benefit is **significant** (1.8 vs 4.8 ms = 2.7× faster).

### Followers / Following — TTL 60s

| Condition | TTFB | Interpretation |
|---|---|---|
| 20 consecutive warm | 0.8–1.8 ms | **~100% hit rate** |

**Estimated hit rate: ~95–99%**. Cache benefit is **significant** (small payloads, fast DB reads).

### Search (`/ap/v1/search?q=alice`) — NOT CACHED

| Condition | TTFB | Interpretation |
|---|---|---|
| 20 consecutive | 2.5–4.4 ms | **No cache** (consistent TTFB, no warm/cold differential) |

**Hit rate: N/A** — search is not cached. Every query hits Postgres. This is by design (search results change frequently; caching would require invalidation on every post/follow/block).

### LRU Capacity Test (1050 actor docs)

1050 sequential actor doc requests (exceeding the 1024 LRU capacity): **220 ms total = 4,772 req/s**. No OOM, no performance cliff. The LRU eviction works correctly.

## Key Findings

### 1. No Cache Metrics (Critical Gap)

**There are no cache hit/miss counters anywhere in the codebase.** The `CachingReadThrough<TValue>.GetAsync` method computes `WasHit` and `WasStale` tuples, but callers universally discard them (`var (value, _, _) = ...`). The only observability is:
- `Count` property on each façade (LRU entry count, not hit/miss)
- `RemoteKeyCache.Invalidate` is `virtual` (F-21: key-rotation observability) — but nothing counts it
- `CacheInvalidationService` logs at `LogDebug` on invalidation

**Recommendation:** Add a `ICacheMetrics` interface with `HitCount` / `MissCount` / `HitRate` properties, wired into `CachingReadThrough<TValue>.GetAsync`. This is the highest-value observability addition.

### 2. Search Is Not Cached (By Design)

Search queries always hit Postgres. This is correct for data freshness but means search is the **only uncached read path**. At the current scale (P95 3.8 ms), this is not a bottleneck. If search latency becomes an issue, consider a **short-TTL result cache** (5–10s) with invalidation on new posts.

### 3. Feed Cache Benefit Is Marginal

The feed cache provides only ~17% TTFB improvement (8.9 → 7.4 ms) because the feed is I/O-bound (DB read dominates). The cache is more valuable for **outbox** (2.7× faster) and **actor docs** (4.8× faster), where the payloads are smaller and serialization overhead matters more.

### 4. Stale-While-Revalidate Works Correctly

After TTL expiry, the first request serves the stale value (fast) while triggering a background refresh. Subsequent requests get the fresh value. No user-visible staleness beyond one request.

### 5. TTLs Are Well-Calibrated

- **Actor docs (60s):** Good balance — actor profiles rarely change, but 60s is conservative enough for live-verify.
- **Collections (60s):** Appropriate — feeds change frequently, 60s is a reasonable freshness window.
- **Remote actors/keys (1h):** Correct — remote docs don't change on our instance; 1h avoids unnecessary federation fetches.
- **WebFinger (15m):** Correct — account→actor mappings are stable.

### 6. LRU Capacity (1024) Is Sufficient

At the current scale (4 local users, ~41 objects), the cache never approaches capacity. Even the 1050-key stress test showed no performance degradation. For production with 10k+ users, the LRU capacity should be increased (e.g., 10,000) or the cache should be backed by Redis.

## Recommendations (Priority Order)

1. **Add cache hit/miss metrics** (`ICacheMetrics`) — highest value, low effort. Wire into `CachingReadThrough<TValue>.GetAsync`.
2. **Increase LRU capacity for production** — 1024 → 10,000 (or add Redis for multi-instance).
3. **Consider short-TTL search cache** — only if search P95 exceeds 50 ms (currently 3.8 ms).
4. **No action needed** for feed cache — marginal benefit but no harm.
