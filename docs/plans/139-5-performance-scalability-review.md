# 139.5 — Performance & scalability review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: end-to-end performance validation —
> query/index efficiency, request-spam elimination, delivery throughput, and client (WASM) startup
> cost. Builds on Phase 57.1 (feed query profiling), Phase 61.1 (WASM performance audit), Phase
> 64.x/72.x (request-spam + engagement-cache work), Phase 49.2 (load testing), and Phase 116.1/116.2
> (performance baseline, cache audit).

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Feed query performance at scale | Seed a large dataset (thousands of posts/follows); measure home/community feed p50/p95 latency | No regression vs. Phase 116.1 baseline; flag any query missing an index | timing capture |
| 2 | Request-spam re-audit | Re-run the Phase 64.x network-spam checklist across all pages post-138/139 UI additions | No new duplicate/N+1 fetches introduced | request count table |
| 3 | Delivery worker throughput | Measure outbound delivery throughput under a burst of posts to many followers/peers (including the new Lemmy peer from Phase 138) | Bounded concurrency (Phase 16.1) holds, no unbounded queue growth | metrics dump |
| 4 | Load test — realistic mixed workload | Run `scripts/load-test-iris.py` against a representative read/write mix | Meets or exceeds the Phase 49.2 baseline; no error-rate spike | load-test report |
| 5 | WASM cold-start | Measure first-load time (bundle size, loading-screen duration, Phase 53.9) on a throttled connection | No regression vs. Phase 61.1 baseline | timing capture |
| 6 | Cache hit-rate sanity | Inspect `ICacheMetrics` (Phase 116.6) under normal browsing | Hit rate consistent with the Phase 116.2 cache audit's expectations | metrics dump |
| 7 | Pagination/backfill cost | Measure the cost of a first-peer historical backfill (Phase 138.20) against a community with substantial history | Completes in a bounded, documented time; doesn't block the UI thread/request | timing capture |
| 8 | Media proxy overhead | Measure latency added by the media/content proxy for cross-instance media vs. direct fetch | Overhead within an acceptable, documented bound | timing capture |
| 9 | Search performance | Measure full-text search latency (Phase 61.2 indexing) at realistic content volume | No regression vs. Phase 61.2 baseline | timing capture |
| 10 | Circuit breaker / retry cost under a flapping peer | Simulate a peer that intermittently fails; confirm the circuit breaker (Phase 131.3) prevents cascading latency into unrelated requests | Unrelated requests unaffected by one flapping peer | metrics dump |

## Deliverable check

All 10 scenarios executed with recorded timings/metrics compared against the nearest prior baseline
(116.1, 61.1, 49.2, etc.); any regression is triaged as a perf finding (class=perf) with severity, not
silently accepted.

## Progress tracking

- [x] 1  - [x] 2  - [x] 3  - [ ] 4  - [ ] 5
- [x] 6  - [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
