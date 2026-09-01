# 148 — Phase 19.5.1: Community outbox READ surface

> 2026-09-01 · Slice 19.5.1 (community READ surface) · Phase 19.5 (community creation & management)

## What was built

Closed the last gap in the community **READ** surface: the community document advertises an
`outbox` IRI (`GET /ap/v1/c/{name}/outbox`), and the community outbox **publish** endpoint
(`POST /ap/v1/c/{name}/outbox`, `CommunityOutboxPublishHandler`) stores each community-authored
activity in the community's outbox — but there was **no READ route** for that outbox, so the
advertised link 404'd. A remote client resolving the community's `outbox` link (the standard way to
find a community's authored activities) hit a dead end.

This slice adds the missing route: **`GET /ap/v1/c/{name}/outbox`** now serves the community's
authored activities (currently a `Follow` and the `Undo` of a `Follow` — the only activity kinds the
publish endpoint accepts) as a paged collection, exactly mirroring the actor outbox collection
endpoint (`GET /u/{handle}/outbox`) for a `Group`:

- Page 1 is an `OrderedCollection` (with `first`); page N > 1 is an `OrderedCollectionPage`
  (with `partOf`/`prev`/`next`).
- Paged via the shared `?page`/`?limit` shape; out-of-range pages clamp to the last page.
- Served through the local collection-page response cache (`LocalCollectionPageCache`), so
  `?refresh=true` re-renders and emits a `no-cache` `Cache-Control` (the shared cache-bypass shape,
  Resolved Decision #6).
- An unknown community 404s (the community-existence check runs before the outbox read).

The community document's advertised `outbox` IRI and this route's path are the same, so the document
is now honest: following the `outbox` link from the community document finds the community's
authored activities.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` —
  - New route registration `group.MapGet("/c/{name}/outbox", CommunityOutboxHandler)` (the READ
    counterpart of the existing `MapPost("/c/{name}/outbox", CommunityOutboxPublishHandler)`).
  - New handler `CommunityOutboxHandler`: resolves the community IRI, 404s if unknown, reads
    `persistence.Activities.GetOutboxAsync(communityIri)` (the same store the publish endpoint writes
    via `AddToOutboxAsync`), and serves a paged collection through the `LocalCollectionPageCache`
    with the shared `Cache-Control` semantics. Mirrors `CollectionEndpointHandler`'s actor-outbox
    branch (the `refresh`/page/limit/cache shape).
- `tests/Iris.Server.Tests/CommunityOutboxCollectionIntegrationTests.cs` — **new** integration test
  class with 5 tests (page 1, page 2, empty outbox, `?refresh=true` cache bypass, unknown community
  404).

## Tests

1199 → **1204** passing (the +5 are the new community-outbox READ tests). Full `dotnet test` green;
`dotnet build` clean (`TreatWarningsAsErrors`).

- `Outbox_Page1_IsOrderedCollection_WithNewestFirstItems` — seeds a 5-item community outbox, reads
  `?limit=2`, asserts the page is an `OrderedCollection` with the two newest items (`-5`, `-4`),
  `totalItems == 5`, and the collection `Cache-Control` header.
- `Outbox_Page2_IsOrderedCollectionPage_WithPrevAndNext` — reads `?limit=2&page=2`, asserts an
  `OrderedCollectionPage` with `partOf`/`prev` (page 1)/`next` (page 3) and items `-3`/`-2`.
- `Outbox_Empty_ServesEmptyCollection` — a freshly-seeded community (no outbox) serves an empty
  `OrderedCollection` with `totalItems == 0`.
- `Outbox_RefreshTrue_BypassesCache` — a primed non-refresh read emits the collection `Cache-Control`;
  a `?refresh=true` read emits `no-cache` (the cache-bypass shape).
- `Outbox_UnknownCommunity_Returns404` — `GET /c/nobody/outbox` is a 404 (the community-existence
  check runs before the outbox read).

## Decisions

- **The outbox is the community's *authored* activities, not its feed.** The actor outbox
  (`/u/{handle}/outbox`) lists the actor's own posted activities; the community's analogous collection
  lists the community's own authored activities — the `Follow`/`Undo` it publishes via its outbox
  publish endpoint. The community *feed* (`/c/{name}/feed`, the union of member outboxes) is a
  different surface and is served by its own route. Conflating them would make the community outbox a
  duplicate of the feed, which is not what the `outbox` IRI on the document means (a community, like a
  person, *authors* activities distinct from the content it surfaces for members).
- **Served through the local collection-page cache (with `?refresh=true` bypass), matching the actor
  outbox.** The community outbox is a local read (no remote fetch), so it is cacheable exactly like
  the actor's local collections. The `?refresh=true` bypass emits `no-cache` so a client that just
  published a follow can re-read the fresh outbox without waiting on the cache TTL. This is the shared
  shape the actor collections and the community feed already use.
- **The test seeds the outbox directly (via `AddToOutboxAsync`) rather than through a signed `Follow`
  POST.** The write path (a signed `Follow` recorded in the community's outbox) is already pinned by
  `CommunityOutboxPublishIntegrationTests`. This slice's question is "does the READ route serve what
  the publish endpoint stored?" — so seeding the outbox with the same `AddToOutboxAsync` the publish
  endpoint calls is the faithful, focused check (it exercises the read path and its pagination/cache
  shape without re-testing the write path's signature/validation).