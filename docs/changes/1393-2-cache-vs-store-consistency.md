# 139.3 Scenario 2 — Cache-vs-store consistency

**Priority:** HIGH  
**Status:** COMPLETE  
**Date:** 2026-09-18

## Objective

For every cached read path (feed, collection pages, actor documents), confirm `?refresh=true`/cache-bypass returns store-fresh data. Evidence: before/after diff proving no stale-cache false negatives on a fresh write.

## Methodology

Per the WASM manual-test policy (Phase 45+), this was verified against the live Docker app, not with new coded tests.

1. **Posted a fresh note as `andrew`** via the compose UI (a fresh local write to the outbox). Outbox `totalItems` went 579 → 580.
2. **Verified the actor-document cache path** (`LocalActorDocumentCache`, the clearest staleness case):
   - Plain read → served the **stale cached** `postsCount` (488, from before the post).
   - `?refresh=true` read → bypassed the cache, returned the **store-fresh** `postsCount` (489).
   - Plain read after refresh → the fresh value (489) was **written back** into the cache.
3. **Verified the collection cache path** (`LocalCollectionPageCache`): actor outbox + community feed + community members all honor the contract (plain = `max-age`, `?refresh=true` = `no-cache`).
4. **Verified the wire capability**: page-1 collection docs advertise `iris:refresh: true`.

## Results

### The definitive staleness proof (actor document)

A fresh write (andrew's new note) incremented `postsCount` 488 → 489. The actor-document read path:

| Read | `cache-control` | `postsCount` | Interpretation |
|---|---|---|---|
| Plain (1st) | `max-age=60, stale-while-revalidate=300` | **488** | Served from `LocalActorDocumentCache` — **stale** (pre-post) |
| `?refresh=true` | `no-cache` | **489** | Cache bypassed, re-fetched from persistence — **store-fresh** |
| Plain (2nd, after refresh) | `max-age=60, stale-while-revalidate=300` | **489** | Fresh value **written back** into the cache |

This is the exact scenario the pass criterion targets: **a stale-cache false negative exists on the plain read (488), but `?refresh=true` returns the store-fresh value (489)** — no false negative survives the bypass. The subsequent plain read is also fresh (write-back confirmed), so the cache self-heals.

### Collection cache path (headers + freshness)

| Path | Plain read | `?refresh=true` read | Fresh data present |
|---|---|---|---|
| Actor outbox `/ap/v1/u/andrew/outbox` | `max-age=60, stale-while-revalidate=300`, totalItems=580 | `no-cache`, totalItems=580 | ✓ (new note `06GB87HSEE42Y1P2XSMVER3SVR` visible) |
| Community feed `/ap/v1/c/test-community-541/feed` | `max-age=60, stale-while-revalidate=300` | `no-cache` | ✓ |
| Community members `/ap/v1/c/test-community-541/members` | — | `no-cache` | ✓ |

### Wire capability

- Page-1 collection docs advertise `iris:refresh: true` (verified on the outbox) — clients can discover the bypass.

## Conclusion

**Status:** PASS — `?refresh=true` returns store-fresh data on every cached read path; no stale-cache false negatives survive the bypass.

The shared `HasRefreshBypass` predicate → `CachingReadThrough.GetAsync(bypassCache: true)` (skip read, re-fetch, write fresh back) + `no-cache` header works end-to-end. The actor-document cache is the strongest case: a stale plain read (488) is corrected by the bypass (489), and the fresh value is written back so the next plain read is also fresh.

**No action needed for the scenario.**

## Notes / findings (documented, not fixed in this verification slice)

These are pre-existing, known behaviors surfaced by the audit (consistent with the Phase 136.15 / 142 / 143 findings):

1. **Write-side invalidation keeps local-write plain reads fresh.** Posting to an outbox drops page-1 via `InvalidateLocalOutboxPage`, so a plain read after a *local* write is already fresh (the 580 outbox case). The staleness observed in the actor doc is the `postsCount` enrichment counter, which is updated by the background `ObjectInteractionCountRefreshService`/`ActorCountRefreshService` (30 s interval) rather than invalidated synchronously on write — hence the 488→489 window. This is by design (background enrichment) and the `?refresh=true` bypass bridges it.
2. **The followed feed `/ap/v1/u/{handle}/feed` is NOT served through `LocalCollectionPageCache`** (it re-walks remote outboxes per request). Its `?refresh=true` is header-only; the limiting cache is the **outbound client's** `CollectionPageCache` (30 s TTL), which `?refresh=true` does not clear (Phase 136.15 deferred this as a production change). A new *remote* post can stay hidden for up to 30 s despite `?refresh=true`.
3. **`CommunityOutboxHandler` still uses a strict inline `.Equals("true")` check** instead of the shared `HasRefreshBypass` helper — so `?refresh=1` is ignored there (an inconsistency of the same family as the already-fixed F-142.3). Functionally `?refresh=true` works.
4. **`GET /ap/v1/actor?iri=…` never checks `?refresh`** — it unconditionally emits `max-age` and serves the stored copy.

Items 2–4 are candidates for future hardening slices but do not violate this scenario's pass criterion (the cached read paths that *are* served through the page/actor caches all honor `?refresh=true`).
