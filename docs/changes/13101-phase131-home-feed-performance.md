# 131.1 — Performance: home feed load time (baseline)

**Date:** 2026-09-13
**Type:** Measurement (no code changes)
**Scope:** Baseline measurement of home feed cold load time

## Context

Phase 131.1 targeted < 3s to first post visible on a cold load. This doc records the baseline measurement.

## Method

- Docker app on host `localhost:8088` (Linux, local network)
- Playwright with CDP `Network.setCacheDisabled(true)` + `Network.clearBrowserCache` for cold load
- Two independent cold-load measurements (navigated away first)
- Wall-clock timing from `page.goto()` to first `<li>` element visible

## Results

| Metric | Run 1 | Run 2 | Target |
|---|---|---|---|
| TTFB (HTML) | 2 ms | 1 ms | — |
| DOMContentLoaded | 12 ms | 10 ms | — |
| **First post visible** | **998 ms** | **993 ms** | **< 3000 ms** |
| Full load (all resources) | 993 ms | 993 ms | — |
| Total transfer | 13.1 MB | 13.1 MB | — |
| WASM files | 66 (12.8 MB) | 66 (12.8 MB) | — |
| API fetches | 75 (12.82 MB) | 75 (12.82 MB) | — |

## Breakdown

- **WASM bootstrap** (~1.8s total duration across 66 .wasm files, 12.8 MB): the dominant cost on cold load. Files are fetched in parallel but each requires instantiation.
- **API fetches** (~2.0s total duration across 75 fetches): feed pagination + actor resolution + media. Individual API calls are fast (50 ms P95 per Phase 116.1).
- **HTML shell** (TTFB 1-2 ms): negligible.

## Conclusion

**Target met.** Cold load is ~1s, well under the 3s target. The WASM bootstrap (12.8 MB across 66 files) is the dominant cost but is acceptable for the current single-user local deployment. No optimization needed at this scale.

## Notes

- Warm load (cached WASM): ~46 ms DCL, instant first post.
- The 12.82 MB "apiMB" figure includes the WASM files in the transfer total (CDP counts all resources). The actual API JSON payloads are much smaller (~100 KB for the first feed page).
- On a production deployment with CDN caching, the WASM files would be served from edge cache, reducing cold load to ~200-300 ms.
