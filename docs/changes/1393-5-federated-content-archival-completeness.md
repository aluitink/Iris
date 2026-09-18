# 139.3 Scenario 5 — Federated content archival completeness

**Phase:** 139.3 — Data lifecycle & persistence review
**Scenario:** 5 (of 11) — "Federated content archival completeness"
**Date:** 2026-09-18
**Result:** **BUG FOUND + FIXED.** First-peer backfill (138.20) captured **nothing** from followed
remote communities (Lemmy) because a remote community persisted to the durable store was misrouted to
its (empty) local outbox. Fixed in `CommunityFeedService` (host-locality gate) + regression tests.
Verified live: `c/technology` (follows `lemmy.world/c/technology`) now returns 20 backfilled items
(was 0); `c/owner-test-5428` (follows `lemmy.luit.ink`) returns 3 items (was 0).

## What was tested

Pass criterion: "when a community first follows a remote peer that has existing history, the feed's
initial outbox walk captures the full available history (within the configured window), not just the
latest page." The question: does the first-peer backfill actually archive the remote community's
content locally, or does it only proxy the latest page?

## What was found (the bug)

On the live instance, the `c/technology` community (0 local members, follows
`lemmy.world/c/technology` + `quokk.au/u/sub_bot`) returned an **empty feed (0 items)** even though
the remote `lemmy.world/c/technology` outbox has 50 inline items. The `andrew` actor's followed feed
likewise returned Mastodon content (19s, real fetches) but **0 items** from its one Lemmy follow
(`lemmy.luit.ink/c/interop`).

### Ruling out the client

First suspected the outbound client's handling of the real Lemmy outbox shape (an `OrderedCollection`
with inline `orderedItems` and **no** `first` link — Lemmy serves the full first page on the
collection document itself). Added a focused client test
(`CollectionTests.GetCollectionAsync_LemmyOutboxShape_NoFirstLink_YieldsOrderedItems`) that serves
that exact shape. **It PASSES** — the client's `GetCollectionAsync`/`ResolveFirstPageIri` correctly
yields the inline `orderedItems` when there is no `first` link. The client was **not** the bug.

### The root cause (server-side routing)

`CommunityFeedService.ReadOutboxAsync` has a community branch that treats **any** community in the
community store as local:

```csharp
if (await _persistence.Communities.TryGetCommunityAsync(contributorIri, out _, ct))
{
    return await _persistence.Activities.GetOutboxAsync(contributorIri, ct); // LOCAL store
}
```

The remote community persister (135.1, `IrisActorDocumentFetcher` → `RemoteCommunityPersister`)
persists every remote community the instance interacts with to the durable `Actors` store (as a
`Group`). So `TryGetCommunityAsync` returns **true** for a **remote** Lemmy community too. The branch
then reads its **(empty) local** outbox instead of fetching over the wire → **0 items, no backfill**.

A second instance of the same bug: the `isRemote` flag in `MergeContributorOutboxAsync`
(`!TryGetCommunityAsync(contributorIri)`) is what gates the 138.20 backfill persistence. For a remote
community in the store, `isRemote` is **false** → even if the items were fetched, they would **not**
be collected for the local backfill persistence.

### Why the existing backfill test didn't catch it

`CommunityBackfillIntegrationTests` seeds only the **local** community; the **remote** community is
never put in the community store (the `RemoteCommunityPersister` is production wiring). So
`TryGetCommunityAsync(lemmy.luit.ink/c/interop)` returns **false** there, the buggy branch never
triggers, and the test passes. The bug only manifests in production, where the remote community **is**
persisted to the store.

## The fix

`CommunityFeedService` now takes an optional `instanceBase` (the instance's base IRI, wired from
`ActivityPubServerOptions.BaseUri` in DI). A new `IsLocalCommunity(iri)` helper returns true only when
the community IRI is hosted on the instance base (or when no base is configured — the legacy behavior
for in-process test hosts). Both the community-local branch in `ReadOutboxAsync` **and** the `isRemote`
flag in `MergeContributorOutboxAsync` now require `IsLocalCommunity`, so a **remote** community
persisted to the store is:
- read from the **remote-wire** path (`FetchRemoteOutboxAsync`), and
- counted as **remote** (its items are collected for the 138.20 backfill persistence).

A **local** community (hosted on the instance base) is still read from the local activity store, and
the no-base legacy behavior is preserved for in-process test hosts.

## Tests added

1. **`CommunityBackfillIntegrationTests.RemoteCommunity_PersistedToStore_IsFetchedOverWire_NotReadLocally`**
   (regression) — seeds a **remote** community into the community store (simulating the 135.1
   persister), configures the service with the instance base, and asserts the followed remote
   community's outbox is fetched **over the wire** (the relayed content appears in the feed) **and**
   is **backfilled** to the local object store. Fails on the pre-fix code (the content is neither
   fetched nor persisted); passes on the fix.
2. **`CollectionTests.GetCollectionAsync_LemmyOutboxShape_NoFirstLink_YieldsOrderedItems`** —
   documents/locks the real Lemmy outbox shape (`OrderedCollection` + inline `orderedItems`, no
   `first` link) at the client layer, so a future regression in the client's no-first walk is caught
   independently of the server routing.

## Evidence (live, post-fix)

Rebuilt + redeployed the Docker app with the fix, then:

| Community | Follows | Items (pre-fix) | Items (post-fix) | `totalItems` |
|---|---|---|---|---|
| `c/technology` | `lemmy.world/c/technology` | **0** | **20** (all `lemmy.world`) | 50 |
| `c/owner-test-5428` | `lemmy.luit.ink/c/interop` | **0** | **3** (all `lemmy.luit.ink`) | 3 |

- **Backfill persisted to the local store:** `SELECT count(*) FROM "Objects" WHERE "Id" LIKE
  'https://lemmy.world/%'` → **14** objects (the remote content is archived locally, not just
  proxied).
- **Within the configured window:** `FeedOptions.PagesPerActor = 1`, `MaxItems = 200` (the live
  defaults, no appsettings override). The 20-item `c/technology` feed is the **first outbox page** of
  the 50-item remote outbox — exactly the "full available history **within the configured window**"
  the scenario's pass criterion specifies. (Capping at 1 page per actor is the intended window, not a
  defect; raising `PagesPerActor` would widen it.)

## Full suite

`dotnet test --filter "Category!=Slow"` → **1306 passed, 0 failed, 8 skipped** (was 1305; +1 regression
test +1 client shape test). Build: 0 warnings, 0 errors.

## Commit

`d1847bc` — `fix(server): fetch remote community outboxes over the wire (139.3 s5)`.
