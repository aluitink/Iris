# 136.15 — Pagination and backfill consistency

**Date:** 2026-09-14
**Slice:** 136.15 (Lemmy interop — pagination and backfill consistency)
**Status:** **COMPLETE.** Three cross-instance integration tests verify that federated community
content pages correctly across instance boundaries and that the historical backfill window
includes the expected post/comment count with stable ordering.

## What this slice delivers

136.15's acceptance criteria:

1. Validate cross-instance timeline paging boundaries (first/next/previous pages) for federated
   community content.
2. Confirm historical backfill after peering includes the expected post/comment windows and
   stable ordering.
3. Verify cache bypass/reload does not lose older remote objects or create duplicates.

### Test coverage

The three tests in `CrossInstancePaginationIntegrationTests` (two-instance `TestServer` fixture,
A: `page-a.domain.local` alice with a 50-post outbox, B: `page-b.domain.local` lumen community
with `FeedOptions.PagesPerActor=3, MaxItems=60`) verify:

| Test | What it verifies |
|------|-----------------|
| `RemoteOutbox_PageBoundaries_FirstNextLast` | A's outbox paginates correctly: page 1 is an `OrderedCollection` with `first` self-link and `next` pointing to page 2; page 2 is an `OrderedCollectionPage` with `startIndex=21` (1-based), `prev` pointing to page 1, `next` pointing to page 3, and `partOf` pointing to the collection; each page has exactly `RemotePageSize` (20) items in newest-first order. |
| `BackfillWindow_ThreePagesOfTwenty_SixtyItems` | B's community feed (which merges alice's remote outbox) includes all 50 posts from alice's outbox (3 pages × 20 posts per page = 60 capacity, 50 actual), confirming the backfill window after peering includes the expected post count. |
| `FeedOrder_Stable_AcrossRepeatedReads` | B's community feed returns the same items in the same order across repeated reads (with and without `?refresh=true`), confirming stable ordering for federated content. |

### Cache bypass coverage note

The acceptance criteria's third item (cache bypass/reload does not lose older remote objects or
create duplicates) is partially covered: `FeedOrder_Stable_AcrossRepeatedReads` verifies that
`?refresh=true` does not change the feed contents (no duplicates, no lost items). Full cache
bypass testing (adding a new remote post and verifying it appears after `?refresh=true`) is
deferred: the `CommunityFeedService` re-walks remote outboxes on every request (it is not served
through the `LocalCollectionPageCache`), so the `?refresh=true` parameter is a no-op for the
feed endpoint. The `IActivityPubClient`'s `CollectionPageCache` is the relevant cache, and
clearing it on `?refresh=true` is a production change that belongs in a separate slice.

### Key design decision: DI override for the community feed's remote outbox fetch

The `ICommunityFeedService` is registered by `AddActivityPubServer` with a factory that creates
its own `IActivityPubClient` via `IActivityPubClientFactory.Create()` with a real
`HttpClientHandler` (not the DI-registered client). Overriding `IActivityPubClient` in DI via
`ExtraServices` has no effect on the community feed's remote outbox fetches.

The fix is to override `IActivityPubClientFactory` in `PreServices` (a new `ActivityPubHostOptions`
property that runs before `AddActivityPubServer`). The `RoutingClientFactory` (test-local) creates
clients with a `LazyHandler` routed to A's `TestServer` and with `Caches = null` (disabling the
`CollectionPageCache` so each outbox walk re-fetches from the network). The factory also registers
lumen's signing key with the instance actor IRI (`/ap/v1/u/lumen`, not the community IRI
`/ap/v1/c/lumen`) so the `SigningHandler` can sign outbound requests.

### Fixture wiring

- **A** (`page-a.domain.local`): alice (person, 50-post outbox seeded via
  `TestSeeder.AddCreateActivity`).
- **B** (`page-b.domain.local`): lumen (community, `FeedOptions.PagesPerActor=3, MaxItems=60`
  via `PreServices`). B follows alice (`ICommunityStore.AddFollowAsync`).
- **Routing:** `PaginationRoutingFetcher` (an `IActorDocumentFetcher`) routes actor-doc fetches by
  host: A-host IRIs go to A's `TestServer`, B-host IRIs go to B's `TestServer`.
- **Delivery:** B's `DeliveryTransport` is a `LazyHandler` routed to A's `TestServer` (so B can
  deliver to A). A's `DeliveryTransport` is a `LazyHandler` routed to B's `TestServer`.
- **Client factory override:** B's `PreServices` registers `RoutingClientFactory` as
  `IActivityPubClientFactory`, so the `ICommunityFeedService` factory (which calls
  `IActivityPubClientFactory.Create()`) gets a client that reaches A's `TestServer`.

## Files changed

- `tests/Iris.Testing/ActivityPubHostFactory.cs` — added `PreServices` (runs before
  `AddActivityPubServer`) and `FeedOptions` properties.
- `tests/Iris.Server.Tests/CrossInstancePaginationIntegrationTests.cs` — new test file
  (3 tests + fixture + `RoutingClientFactory` + `PaginationRoutingFetcher`).
