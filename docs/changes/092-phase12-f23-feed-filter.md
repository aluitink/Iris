# 092 — Phase 12: F-23 — `?q=` content filter on the community feed endpoint

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-23)

## What was built

The community feed endpoint (`GET /c/{name}/feed`) now accepts a `?q=` content filter: a filtered
view of the feed. The planned `FeedFilter?` parameter (J-14, "accepted but ignored") was a misleading
API surface — the `FeedFilter` type was never built, so the parameter did not actually exist. The fix
implements the filter on the existing surface rather than dropping a nonexistent parameter.

## The fix

Three changes in `src/Iris.Server`:

1. **`ICommunityFeedService.GetFeedAsync`** gained an optional `string? query = null` parameter. A
   non-empty/whitespace query is **filtered** to the items that match it (the same content/name match
   as `SearchCommunityAsync`); a null/empty query returns the feed unfiltered. The existing callers
   (the no-`?q` path, and `SearchCommunityAsync`'s internal call) are unaffected — they pass `null`.

2. **`CommunityFeedService.GetFeedAsync`**: when `query` is non-empty, it delegates to the existing
   `SearchCommunityAsync(communityIri, query, ct)` (the community-search content/name matcher over the
   feed surface). Otherwise it runs the original unfiltered union-of-outboxes path.

3. **`CommunityFeedHandler`** reads `context.Request.Query["q"]` and threads it into
   `feedService.GetFeedAsync(communityIri, query, ct)`. The filtered and unfiltered responses are
   identical in shape (the same paged `OrderedCollection`/`OrderedCollectionPage`, paged via
   `?page`/`?limit`), so the client's `GetCommunityFeedAsync` reads both identically — no client change
   needed.

## Tests

**`CommunityFeedIntegrationTests`** (4 new integration tests on the `GET /c/{name}/feed?q=...`
endpoint):

- `Feed_WithQuery_FiltersToMatchingItems` — `?q=bob` returns only bob's 2 posts (not alice's 3);
  `totalItems` reflects the filtered count (2), not the full feed (5).
- `Feed_WithQuery_MatchesCaseInsensitive` — `?q=BOB` still matches bob's posts (case-insensitive).
- `Feed_WithQuery_NoMatch_ReturnsEmptyCollection` — `?q=zzz` returns an empty collection,
  `totalItems` 0.
- `Feed_WithQuery_StillPages` — `?q=alice&limit=2&page=2` returns alice's oldest post on page 2, with
  `totalItems` = the filtered count (3) — the filter and the existing `?page`/`?limit` paging compose.

## Files changed

- `src/Iris.Server/Services/ICommunityFeedService.cs` — `GetFeedAsync` optional `query` param.
- `src/Iris.Server/Services/CommunityFeedService.cs` — filter delegation to `SearchCommunityAsync`;
  internal call updated to pass `null`.
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `CommunityFeedHandler` reads `?q`.
- `tests/Iris.Server.Tests/Services/CommunityFeedIntegrationTests.cs` — 4 new integration tests.

## Decisions

- **Implement the filter rather than drop it.** The J-14 fix plan offered two options ("implement the
  filter or drop the parameter"). The parameter did not actually exist (the `FeedFilter` type was never
  built), so "drop it" had nothing to drop. Implementing the filter adds real value (a client can
  filter a community's feed by content via `?q=`) and reuses the existing `SearchCommunityAsync`
  matcher — no new type, no new endpoint.
- **Reuse the community-search match.** The feed's `?q=` and the `/search` endpoint's `?q=` share the
  same content/name matcher (`SearchCommunityAsync`), so a user gets consistent matching across both
  endpoints. The only difference is the response shape: `/feed` is a paged collection (the feed
  surface), `/search` is the search collection (with the `iris:searchQuery` extension).

## Test count

952 → 956 (+4), 0 failures.
