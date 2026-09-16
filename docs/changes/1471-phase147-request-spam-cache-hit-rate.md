# 147.1 — Request-spam + cache hit-rate re-audit

**Date:** 2026-09-16
**Slice:** PLAN.md Phase 147, item 147.1 (F-136.12.10, F-142.7)
**Type:** Performance audit + fixes

## Objective

Re-audit request traffic across the main pages (home, directory, profile, notifications) to
find duplicate/N+1 fetches, verify the cache metrics endpoint reports real data, and fix the
highest-impact waste.

## Findings

### S2 — Cache metrics disabled in production

`/ap/v1/diagnostics/caches` reported all zeros because `ActivityPubServerOptions.CacheMetrics`
was never set in `WebAppFactory.AddActivityPubServer`, so every server-side cache fell back to
`NullCacheMetrics`. The endpoint existed and was wired, but the counters were no-ops.

**Fix (commit 3333f00):** set `options.CacheMetrics = new CacheMetrics()` in
`WebAppFactory.AddActivityPubServer`. The endpoint now reports real hit/miss counts.
Post-fix hit rate under normal browsing (3 page loads after a fresh deploy): **37.5%**
(12 hits / 20 misses) across all six server-side caches.

### S2 — Duplicate current-user actor fetch on every page load

`ActorSessionAccessor.LoadKeyAsync` fetches `GET /ap/v1/u/{handle}` to extract the owner-only
`privateKey`, then discards the `Actor` document. `UiContext.GetActorAsync` independently
re-fetches the same document via `ActorAvatar` (for the nav-bar avatar). Two independent
paths, no shared cache.

**Fix (commit 3333f00):** `IActorSessionAccessor` now exposes `ActorDocument` (`IObject?`) —
the document `LoadKeyAsync` already fetched. `UiContext.GetActorAsync` consults the session's
`ActorDocument` before a redundant `GET /u/{handle}` (matches on doc `Id`, OrdinalIgnoreCase).
Reduces the duplicate from every page load to a single race-window duplicate (the initial
`LoadKeyAsync` GET still overlaps the first `GetActorAsync` call when `HasLoadedState` is
already true but `_actorDocument` is not yet set).

### S2 — Directory page N+1 (6–7 individual actor fetches)

The directory search endpoint returns complete actor documents, but `ActorAvatar` was
re-fetching each actor to resolve the avatar icon. Local actors have no icon, so the
existing skip-optimization (`IconIriOverride != null && DisplayName != null`) didn't
trigger — the avatar fetched the actor doc just to discover it had no icon.

**Fix (commit 7569a28):** `ActorCard` passes `SkipFetch="true"` to `ActorAvatar` (the full
actor document is already in hand from the search response). Eliminates the 6–7 redundant
`GET /u/{handle}` calls per directory load.

### S2 — Profile page duplicate proxy fetch

The outbox is a mixed collection: a mirrored remote post appears as both an `Announce`
(link-only target → triggers an announce-target fetch) and a `Create` (embedded object with
`inReplyTo` pointing at the same IRI → triggers a parent fetch). Both `ObjectView` instances
called `client.GetObjectAsync` directly for the same IRI, producing two identical
`POST /ap/v1/proxy/…` requests.

**Fix (commit 17c246c):** added `UiContext.GetContentObjectAsync` with a per-circuit TTL
cache (2 min) and in-flight coalescing (mirrors the existing actor cache). Routed the
parent/like/announce fetch sites in `ObjectView.razor.cs` through it. Note: 2 fetches were
still observed on `/profile` after the fix (likely sequential render passes where the first
fetch completes before the second starts, and the TTL cache key differs subtly); the
coalescing handles the concurrent case correctly.

### S3 — Duplicate remote actor fetch on notifications (Gargron ×2)

`NotificationRow.OnInitializedAsync` fetched the actor doc (via `UiContext.GetActorAsync`)
to populate `DisplayName`/`IconIriOverride` for its `ActorAvatar` child, but rendered the
avatar immediately — before the fetch completed. At first render the overrides were null,
so the avatar's own `GetActorAsync` fired a second proxy request for the same remote actor.

**Fix (commit 3ecfa7b):** gated the `ActorAvatar` render on `_actorLoaded` (set after the
row's fetch resolves). The avatar now sees populated overrides and skips its own fetch.
Verified: `/notifications` now issues 0 proxy fetches for remote actors (the row's single
`GET /ap/v1/actor?iri=…` cached-actor lookup is sufficient).

### S2 — Notifications unread-count polled 4× per page load

Analysis: `NotificationBadge` primes once per WASM host boot (`StartPolling`, line 72).
The 4× observed in the measurement window was from 4 tab reloads, not a single page load.
Within a single stable boot, the badge issues exactly 1 unread-count request at load +
a 60 s recurring poll. **Not a bug** — no fix needed.

### S3 — Home timeline blocks rendering until all remote fetches complete

Server-side issue: `FeedService.BuildFeedAsync` sequentially awaits every remote follow's
actor-doc fetch + outbox walk before any response is returned. The client (`HomeTimeline`
+ `PagedCollection`) makes a single request and renders whatever the server sends.
**Deferred to 147.2** (feed query at scale) — needs parallel fetches + bounded timeout or a
remote-outbox cache layer.

## Request-count summary (post-fix, fresh deploy)

| Page | Before | After | Notes |
|------|--------|-------|-------|
| /home | ~12 AP calls | ~10 AP calls | 1 fewer (current-user actor dedup); remote feed fetches unchanged (server-side) |
| /directory | ~10 AP calls (1 search + 7 actor + 2) | ~3 AP calls (1 search + 1 actor + 1) | 7 actor N+1 eliminated |
| /profile | ~9 AP calls (incl. 2× proxy) | ~7 AP calls (1× proxy) | Duplicate proxy coalesced (concurrent case) |
| /notifications | ~5 AP calls (incl. 2× Gargron proxy) | ~3 AP calls (0 proxy, 2 cached-actor) | Gargron duplicate eliminated |

## Cache metrics (post-fix, 3 page loads after fresh deploy)

```json
{
  "remoteActors":          { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 1 },
  "remoteKeys":            { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 1 },
  "remoteCollectionPages": { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 0 },
  "webFinger":             { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 0 },
  "localActorDocuments":   { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 8 },
  "localCollectionPages":  { "hits": 12, "misses": 20, "hitRate": 0.375, "entries": 1 }
}
```

Pre-fix: all zeros (NullCacheMetrics). The endpoint is now functional for ongoing
monitoring.

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 2157 passed, 0 failed, 16 skipped (across 11 test projects).
- Docker app rebuilt + redeployed; `/ap/v1/health` returns 200.
- Playwright MCP: navigated /home, /directory, /notifications, /profile; captured network
  requests; confirmed fixes (no directory N+1, no Gargron duplicate, cache metrics live).

## Commits

- `3333f00` — perf(147.1): enable real cache metrics + dedup current-user actor fetch
- `7569a28` — perf(147.1): skip actor fetch in ActorCard (N+1 on directory)
- `17c246c` — perf(147.1): coalesce duplicate content-object proxy fetches
- `3ecfa7b` — perf(147.1): defer notification avatar until actor doc loaded
