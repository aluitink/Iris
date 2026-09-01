# 159 — Cache behavior at the boundary: a new outbox activity is visible only via a `?refresh=true` bypass

> 2026-09-01 · Slice 19.6.6 (Phase 19.6 — Architectural expectations: client↔server interaction) · a
> cached read serves stale until a bypass re-fetches; no stale-forever

## What was built

**19.6.6** asks: "Cached reads (collections, actor documents) expose `bypassCache`/`?refresh=true` and a
new activity is visible after a bypass (the UI's refresh path actually re-fetches); no stale-forever
behavior."

Reading the path confirmed the mechanism is **already implemented end-to-end**, and the missing piece was
a dedicated pin for the **combined** scenario the adjacent tests cover only in pieces:

- The local collection endpoints (`GET /ap/v1/u/{handle}/{outbox|followers|following|liked|blocks|…}`)
  and the community collections are served through `LocalCollectionPageCache` (`max-age=60,
  stale-while-revalidate=300`, LRU, keyed by page IRI). `CollectionEndpointHandler` reads the items **live
  from persistence on every request**, then reads (or renders on a miss) the **rendered document** through
  the cache — so a plain read within the fresh TTL serves the stale cached page, and a brand-new activity
  added after the page was cached is not yet visible.
- `?refresh=true` is parsed by `HasRefreshBypass` (`ActivityPubServerExtensions.cs`) and passed to
  `CachingReadThrough.GetAsync` as `bypassCache`. A bypass **skips the read, re-renders from the live
  factory, and writes the fresh entry back** (so the next plain read is fresh — no stale-forever), and the
  response emits `no-cache`.
- The actor-document endpoint (`GET /ap/v1/u/{handle}`) is cached via `LocalActorDocumentCache` (public
  document only; the owner-only document is always `no-store`), with the same `?refresh=true` bypass.
- The client supports bypass via `CollectionQuery(BypassCache: true)` → `GetCollectionPageFromNetworkAsync`
  appends `?refresh=true` (bypassing the client's own page cache **and** the server's). The UI's refresh
  path (`ActorDetail.razor`, `ObjectPage.razor`) passes `BypassCache: true` after mutations.

What was **not** pinned was the exact 19.6.6 boundary as one scenario: prime the page cache → add a *new*
activity → assert a **plain** read still serves the stale page (new activity absent) → assert a
`?refresh=true` read makes it visible (+ `no-cache`) → assert a subsequent plain read now sees it. The
adjacent tests cover the pieces in isolation (`Outbox_RefreshTrue_BypassesCache` on stable content;
`CommunityFeed_…RefreshBypassAndCacheControl` staleness on a community feed;
`PostNoteSurfacesInOutboxIntegrationTests` visibility on a **cold** outbox), but none couple the three
behaviors. This slice adds that pin.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — **unchanged** (`CollectionEndpointHandler` reads
  items live, serves through `LocalCollectionPageCache`, and honors `?refresh=true`; `ActorDocumentHandler`
  does the same for the actor document).
- `src/Iris.Server/Caching/LocalCollectionPageCache.cs`, `src/Iris.Server/Security/LocalActorDocumentCache.cs`
  — **unchanged** (the cached reads; `?refresh=true` → `CachingReadThrough.GetAsync(bypassCache: true)`).
- `src/Iris.Core/Caching/CachingReadThrough.cs` — **unchanged** (bypass skips the read, re-renders, and
  writes the fresh entry back).
- `src/Iris.Client/Collections/CollectionQuery.cs`, `src/Iris.Client/ActivityPubClient.cs` — **unchanged**
  (`CollectionQuery.BypassCache` → `?refresh=true` on the wire).
- `tests/Iris.Server.Tests/OutboxCacheBypassIntegrationTests.cs` — **new** (two integration tests; see
  below).

## Tests

1256 → **1258** passing (+2: the two cache-boundary integration tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file.

Topology: a single instance (alice) hosting the real collection endpoints, read over the in-process HTTP
stack (no signing — public read endpoints).

- `NewOutboxActivity_IsStaleOnPlainRead_VisibleAfterRefreshBypass` — the central 19.6.6 assertion:
  1) a plain read of page 1 **primes** the cache (holding only the initial item); 2) a **new** activity is
  added to the outbox in the store; 3) a **plain** read within the fresh TTL still serves the **stale**
  page (the new activity absent; `Cache-Control` is the normal cacheable collection value); 4) a
  `?refresh=true` read **bypasses** the cache — the new activity is visible and the response emits
  `no-cache`; 5) a subsequent **plain** read now sees the new activity (the bypass wrote the fresh entry
  back — **no stale-forever**).
- `ActorDoc_PlainReadServesCached_RefreshBypassRefetches` — the same boundary on the actor-document read:
  a plain read of the public actor document is cached (`max-age=60, stale-while-revalidate=300`), and a
  `?refresh=true` read re-reads the actor from the store and emits `no-cache`.

## Live verification (deferred — a live item)

The server-side boundary is pinned by the new tests (stale→bypass→fresh, end-to-end over the wire). The
**live** half — driving a write through the **UI** in the two-instance Docker environment and confirming
the UI's refresh path actually re-fetches (a new activity is visible after the bypass) — is the remaining
live-verification item for 19.6.6. It requires the two-instance Docker environment (dev1-public host
unreachable from CI), so it is deferred as a live item; the server-side boundary it exercises is already
covered in CI by the new tests + the existing bypass/staleness pins.

## Decisions

- **The pin couples the three behaviors; the pieces were already covered in isolation.** Rather than
  duplicate the existing bypass-mechanic test (`ForceRefresh_BypassesReadAndWritesBack`), the staleness
  test on a community feed, or the cold-outbox visibility test, this slice adds the single scenario 19.6.6
  actually describes — a *new* activity that is **stale on a plain read**, **visible after a bypass**, and
  **still visible on the next plain read** (the "no stale-forever" guarantee). Each assertion is one the
  adjacent tests could not make on their own (they each hold the other variables constant).
- **The page-1 cache key is stable across reads.** `CollectionEndpointHandler` keys the cache by the page
  IRI (`{actor}/outbox` for page 1); the `?limit` does not affect the key. So the priming read, the
  stale read, the bypass, and the post-bypass read all hit the same entry — which is what makes the
  stale→bypass→fresh sequence observable in one test.
- **No production change.** The `?refresh=true` bypass (server) and `CollectionQuery.BypassCache` (client)
  were already implemented end-to-end. The slice is a verification pin, consistent with how 19.6.2 (change
  156), 19.6.3 (change 157), and 19.6.5 (change 158) were closed.
