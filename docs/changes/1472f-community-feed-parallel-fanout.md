# 147.2 follow-up — CommunityFeedService parallel fan-out

**Date:** 2026-09-19
**Status:** COMPLETE

## Summary

Phase 147.2 parallelized the main follow feed's per-follow fan-out with `Task.WhenAll`, reducing latency from the SUM of all remote fetch times to the SLOWEST single fetch. The community feed (`CommunityFeedService`) had the same sequential pattern: it read member + followed-actor outboxes in `foreach` loops, so a community with N remote members/follows took the SUM of all fetch latencies.

This change applies the same parallelization to the community feed. Both the member branch and the follows branch now use `Task.WhenAll`, so total latency is bounded by the slowest single contributor, not the sum.

## Design

- **Thread-safety:** The original `MergeContributorOutboxAsync` mutated shared state (`seen` HashSet + `merged` List). Refactored to return `(List<(int Position, IObjectOrLink Item)> Items, List<IObjectOrLink> RemoteItems)` instead. The caller merges the results sequentially in deterministic IRI order, applying cross-contributor dedup by activity IRI (keep the first, i.e. newest, occurrence).
- **Error handling:** A failed/slow contributor contributes an empty list (the `try/catch` in the `Task.WhenAll` lambda), preserving the existing "one broken remote must not fail the feed" guarantee.
- **Deterministic merge:** Members are merged first (in IRI order), then follows (in IRI order). The final feed is ordered by (outbox position, then contributor IRI), same as before.
- **Remote backfill:** Remote outbox items (from the follows branch) are still collected for the 138.20 backfill persistence, unchanged.

## Files changed

- `src/Iris.Server/Services/CommunityFeedService.cs`:
  - `GetFeedAsync`: replaced the two `foreach` loops (members + follows) with `Task.WhenAll`. Each contributor's outbox is fetched in parallel; results are merged sequentially afterward with dedup.
  - `MergeContributorOutboxAsync`: refactored from a void method that mutated shared state to a method that returns `(List<(int Position, IObjectOrLink Item)> Items, List<IObjectOrLink> RemoteItems)`. No longer takes `seen`/`merged`/`remoteOutboxItems` parameters.

## Tests

No new tests required — the existing 38 community feed tests (35 in `Iris.Server.Tests`, 2 in `Iris.Client.Tests`, 1 in `SampleServer.Tests`) all pass, covering:
- Local member outbox merging
- Remote member outbox fetching (wire)
- Community-tag filtering (40.3)
- Peering (followed-actor content admitted without the community-tag filter)
- Moderation (blocked/muted members excluded)
- Dedup across contributors (same activity IRI appears only once)
- Remote backfill persistence (138.20)

Full suite: **1357 passed, 0 failed, 25 skipped** (Iris.Server.Tests).

## Performance impact

A community with N remote members/follows previously took `sum(latency_i)` for the outbox fan-out. Now it takes `max(latency_i)` (bounded by the slowest single contributor, plus the local DB reads for members). For a community with 10 remote follows at 0.5–10 s each, this is a 5–10× latency reduction in the worst case.
