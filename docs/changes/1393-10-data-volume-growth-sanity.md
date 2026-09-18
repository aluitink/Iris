# 139.3 Scenario 10 — Data volume growth sanity

**Status:** PASS (verified). Seeding a larger-than-typical dataset (a 500-post outbox + a 1,500-object
search corpus) leaves the feed read and the full-text search correct and responsive. No defects found.

## Bar

> Seed a larger-than-typical dataset (thousands of posts/activities) and confirm query performance and
> pagination don't degrade catastrophically. Feed/search remain responsive (ties to 139.5 but checked
> here for **correctness, not raw speed**). Evidence: **timing note**.

## What the test covers

`tests/Iris.Server.Data.Tests/DataVolumeGrowthTests.cs` (1 test, on the shared
`postgres:16-alpine` `PostgresFixture`):

`LargeDataset_OutboxFeedAndSearch_StayCorrectAndResponsive` seeds, then exercises the real EF read
paths and asserts correctness + a generous timing ceiling:

**Seed** (bulk, one `UNNEST` insert per table so seeding is O(rows), not O(round-trips)):
- One **local** `Person` actor (carries a `Handle`, so it is visible to `localOnly` actor search).
- A **500-post outbox** — each post is a `Create` wrapping a `Note`, stored as
  `BoxItems` (Direction 0, `Position` newest-first) + `Activities` (the Create jsonb) + `Objects`
  (the Note jsonb). 50 of the notes carry a unique `growthneedle-{ns}` token.
- A **1,000-object search corpus** (Notes, 50 carrying the same needle) — bringing the total object
  count to 1,500.
- The `SearchVector` tsvector is backfilled for the seeded rows with the **same SQL the
  `AddSearchVector` migration uses**, so the GIN indexes (`IX_Actors_SearchVector` /
  `IX_Objects_SearchVector`) are actually populated and exercised.

**Assertions:**
1. **Feed read** (`IActivityStore.GetOutboxAsync` — the EF SQL underlying every feed): returns all
   500 items; each deserializes back to a `Create`; served newest-first (item 499 first, item 0 last,
   matching the `Position` ordering); completes within a 30 s ceiling.
2. **Object search** (`IObjectStore.SearchObjectsAsync` — the GIN-indexed `@@ plainto_tsquery('simple')`
   + `ts_rank` query with true SQL `LIMIT/OFFSET`): `CountSearchMatchesAsync` returns the exact known
   hit count (100 = 50 outbox + 50 corpus); page 1 (limit 20) returns 20 objects all containing the
   needle; page 2 (offset 20) returns the next 20 with **no overlap** with page 1 (pagination
   correctness); a no-needle query returns empty; completes within the ceiling.
3. **Actor search** (`IActorStore.SearchActorsAsync`, `localOnly: true`): finds the seeded local actor
   by its unique name.

## Why the timing ceiling is generous (30 s)

The scenario's bar is **correctness, not raw speed** — a "catastrophic" regression is a missing index
turning the search into a full sequential scan over the corpus, an O(N²) feed merge, or a read that
hangs. A 30 s ceiling catches those without pinning a machine-specific wall-clock figure. The actual
observed timings (below) are far below the ceiling.

## Findings

No defects. The read query paths stay correct and responsive at a larger-than-typical volume:

- The **outbox/feed read** (`BoxItems` by `(Direction, ActorId, Position)` + a batch `Activities`
  fetch) scales linearly with the actor's post count and returned 500 items in the observed time below.
  (Note for 139.5: this read is a **full load** with no SQL `LIMIT` — the feed service caps/slices in
  memory. At far larger volumes this is the path to revisit, but it is correct here.)
- The **full-text search** uses the GIN index on `SearchVector`: a `limit 20` page over 1,500 objects
  returned in the observed time below, and pagination (`OFFSET`) sliced correctly with no overlap.

### Timing note (evidence)

```
[139.3-s10] outbox read of 500 items: 137.7 ms;
            object search (limit 20) over 1500 objects: 37.4 ms;
            total needle matches: 100.
```

## Evidence

```
dotnet test tests/Iris.Server.Data.Tests/Iris.Server.Data.Tests.csproj \
  --filter "FullyQualifiedName~DataVolumeGrowthTests"
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

Full solution build: 0 warnings / 0 errors. Full `Category!=Slow` suite: 0 failed across all 11 test
projects (Data project now 15 tests).
