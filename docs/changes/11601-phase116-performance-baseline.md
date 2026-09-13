# Phase 116.1 — Performance Baseline: Measurement + Documentation

**Date:** 2026-09-13
**Type:** Performance measurement (no code changes)
**Scope:** TTFB, throughput, and concurrency for all key API endpoints

## Summary

Measured TTFB, throughput, and concurrency for all key API endpoints on the production Docker app. All endpoints are **well under the 200 ms P95 target** — the slowest (home feed) has a P95 of 9.3 ms. No performance optimization is currently needed at this scale.

## Key Results

- **Feed:** P95 9.3 ms, 112 req/s sequential, 1,219 req/s concurrent
- **Search:** P95 3.8 ms, 174 req/s sequential, 1,851 req/s concurrent
- **Actor doc:** P95 0.9 ms, 297 req/s sequential
- **WASM shell:** <1 ms TTFB (client-side WASM startup is the real bottleneck, not server TTFB)

## Files Created

- `docs/plans/performance-baseline.md` — full baseline with methodology, per-endpoint data, percentiles, throughput, and observations

## Next Steps

- **116.2** — Cache hit-rate audit (measure which reads hit in-memory cache vs. Postgres)
- **116.3** — Federation stress test (10 instances, 1k follows, 10k objects)
- **WASM startup time** — profile client-side WASM load (out of scope for server perf baseline)
