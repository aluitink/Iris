# Phase 116.2 — Cache Hit-Rate Audit: Infrastructure Review + Empirical Measurement

**Date:** 2026-09-13
**Type:** Cache audit (no code changes)
**Scope:** Caching architecture, TTLs, empirical hit-rate measurement, gaps

## Summary

Audited the full caching infrastructure (Iris.Core `ICache`/`MemoryCache`/`CachingReadThrough` + 7 server façades + 4 client façades). Measured empirical hit rates via TTFB differentials. Key finding: **no cache hit/miss metrics exist** — the highest-value observability gap.

## Key Results

- **Actor docs:** ~95–99% hit rate within 60s TTL (4.8× faster on hit)
- **Feed:** ~95–99% hit rate (marginal 17% TTFB improvement — I/O-bound)
- **Outbox:** ~95–99% hit rate (2.7× faster on hit)
- **Search:** NOT cached (by design; P95 3.8 ms, no bottleneck)
- **LRU capacity (1024):** Sufficient at current scale; 1050-key stress test clean
- **Stale-while-revalidate:** Works correctly (no user-visible staleness)

## Critical Gap

No `ICacheMetrics` (hit/miss counters). `CachingReadThrough<TValue>.GetAsync` computes `WasHit`/`WasStale` but callers discard them. **Recommendation:** Add `ICacheMetrics` interface wired into `CachingReadThrough`.

## Files Created

- `docs/plans/cache-audit.md` — full audit with architecture table, empirical data, findings, recommendations
