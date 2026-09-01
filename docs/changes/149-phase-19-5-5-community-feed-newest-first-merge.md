# 149 — Phase 19.5.5: Community feed newest-first merge

> 2026-09-01 · Slice 19.5.5 (community feed correctness) · Phase 19.5 (community creation & management)

## What was fixed

The community's unified feed (`GET /ap/v1/c/{name}/feed`, backed by `ICommunityFeedService`) was
documented as "the union of the local members' outbox activities, **newest first**, de-duplicated" —
but the implementation did **not** produce a newest-first feed. `CommunityFeedService.GetFeedAsync`
read each member's outbox (which is individually newest-first) and **concatenated them in member-IRI
order** (grouped by member: all of alice's posts, then all of bob's posts, …). The result was a
member-grouped feed, not a time-ordered one: a member's *newest* post did not rank above a *different*
member's *older* post at the same outbox position.

This slice fixes the merge to be genuinely newest-first while keeping the de-duplication and the `?q`
content filter:

- Each member's outbox is already newest-first (position 0 = that member's newest post).
- The feed is now the **stable merge** of the members' outboxes ordered by **(outbox position, then
  member IRI)**: a member's newest post ranks above its older posts, and two posts at the same outbox
  position are ordered by member IRI (deterministic, so the feed is reproducible for a given set of
  outboxes).
- De-duplication by activity IRI is unchanged (keep the first = newest occurrence); the `?q` filter is
  unchanged (it filters the merged feed by content/name).

## Key types & files

- `src/Iris.Server/Services/CommunityFeedService.cs` — `GetFeedAsync` now builds
  `(outbox position, member IRI, item)` tuples per member and returns them ordered by
  `(position, memberIri.Value ordinal)` (stable newest-first merge) instead of concatenating in
  member order. Class `<remarks>` updated to describe the new ordering.
- `src/Iris.Server/Services/ICommunityFeedService.cs` — no signature change (the interface's "newest
  first" contract is now actually honored).
- `tests/Iris.Server.Tests/CommunityFeedCorrectnessIntegrationTests.cs` — **new** integration test
  class (4 tests): the newest-first merge, de-duplication of an activity in two members' outboxes,
  pagination (page 2 `OrderedCollectionPage`), and unknown community 404.
- `tests/Iris.Server.Tests/Services/CommunityFeedIntegrationTests.cs` — order assertions updated to the
  new merge (3 tests + doc comment).
- `tests/Iris.Server.Tests/CommunitySearchIntegrationTests.cs` — order assertions updated to the new
  merge (2 tests + comments).

## Tests

1204 → **1208** passing (the +4 are the new `CommunityFeedCorrectnessIntegrationTests`). Full
`dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`).

- `Feed_MergesMemberOutboxes_NewestFirst` — two members each with 2 posts; asserts the merged order is
  (alice create-2, bob create-2, alice create-1, bob create-1) — the two newest first, the two older
  after.
- `Feed_DeduplicatesActivity_RecordedInTwoMembersOutboxes` — the same activity IRI recorded in both
  members' outboxes appears exactly once (and `totalItems` reflects the de-duplicated count).
- `Feed_Page2_IsOrderedCollectionPage_WithPrevAndNext` — page 2 of a 2-page feed is an
  `OrderedCollectionPage` with `partOf`/`prev` (page 1) and no `next` (last page).
- `Feed_UnknownCommunity_Returns404` — `GET /c/nobody/feed` is a 404.

## Decisions

- **Newest-first by outbox position, not by a per-item timestamp.** An ActivityStreams outbox carries
  no reliable per-item `published` timestamp that Iris currently records at the boundary (the
  `CreateActivityHandler`/`CommunityContentRecorder` record activities into member outboxes without a
  normalized wall-clock field), and comparing across members by timestamp would be both unreliable and
  order-dependent. Outbox position is the authoritative recency signal the store already maintains
  (`InMemoryActivityStore.AddToOutboxAsync` inserts at index 0, newest first). Ordering by
  (outbox position, member IRI) is the cheapest deterministic "newest first" that matches the documented
  contract and is stable across recreations for a given set of outboxes. This mirrors the existing
  actor `FeedService` (F-14) approach (deterministic merge, de-dup by IRI) — the community feed now
  actually delivers the "newest first" its doc promised.
- **Tie-break by member IRI (not insertion order).** The membership set has no inherent order; using
  the member IRI as the secondary key makes the merge fully deterministic (independent of the
  `ConcurrentDictionary`/`HashSet` enumeration order of the member set), so the feed is reproducible.
- **Kept de-duplication + `?q` filter unchanged.** The merge fix is orthogonal to those two behaviors;
  both are preserved exactly (de-dup keeps the first/newest occurrence; the filter filters the merged
  feed). Existing `?q` tests (`Feed_WithQuery_*`) continue to pass without change.