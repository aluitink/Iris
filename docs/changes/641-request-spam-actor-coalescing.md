# 64.1 — actor-fetch in-flight coalescing (request spam)

Phase 64 cuts the *number* of calls the WASM client makes. 64.1 is the first
slice: eliminate the **per-card actor N+1** that fires on every feed load —
the same author's document fetched once per card instead of once per page.

## Problem (620 tracker topics #1 + #2)

On `/home` initial load, the actor-document fetch fired once **per card** that
showed the same author:

- **Local actor** (`GET /ap/v1/u/{actor}`): andrew fetched **5×** (own post +
  3 own-boosts + session).
- **Remote actor** (`POST /ap/v1/proxy/{remote}/users/RayvenMX`): RayvenMX
  proxied **4×** (3 posts + 1 boost).

The root cause is **not** a missing cache — a per-circuit actor cache
(`UiContext._actors`, IRI-keyed, 5-min TTL) already exists. The root cause is
that the cache had **no in-flight coalescing**: when N cards render
concurrently and each calls `UiContext.GetActorAsync(sameIri)`, every one of
them checks the TTL cache *before any of them has populated it* (all N are
async and none has completed), so all N miss and each fires its own network
GET / POST-proxy. The `ConcurrentDictionary` write was last-write-wins.

## Fix

`apps/Iris.Web.Client/Ui/UiContext.cs` — added a per-IRI **in-flight gate** to
`GetActorAsync`:

- New field:
  `private readonly ConcurrentDictionary<string, Task<IObject?>> _actorInFlight =
     new(StringComparer.OrdinalIgnoreCase);`
- On a TTL-cache miss, `GetActorAsync` now does:
  ```csharp
  var fetchTask = _actorInFlight.GetOrAdd(actorIri.Value, _ => FetchActorAsync(client, actorIri));
  try { return await fetchTask; }
  finally { _actorInFlight.TryRemove(actorIri.Value, out _); }
  ```
  The first caller starts the fetch and publishes its `Task`; concurrent callers
  for the same IRI `GetOrAdd` the **same** `Task` and await it — one network
  call, N awaiters.
- The actual fetch moved to a private `FetchActorAsync(IActivityPubClient, Iri)`
  that does `client.GetObjectAsync` + TTL-cache population (unchanged behavior:
  returns null on failure, caches only on success so a later call can retry).
- The `finally` clears the in-flight marker when the first caller finishes.
  This is safe: any concurrent caller already holds its own reference to the
  task (captured from `GetOrAdd`), so removing the marker only affects *new*
  callers — which either hit the now-populated TTL cache (success) or, after
  the TTL expires / on a failed fetch, correctly start a fresh fetch.

This is the minimal, centralized change: every actor read in the WASM client
funnels through `UiContext.GetActorAsync` (verified by the explore pass —
`ActorAvatar.razor:70`, `NotificationRow.razor:125`, and the page-level detail
fetches all call it), and the local (GET) vs remote (POST-proxy) routing is
transparent to it (handled by `ProxyFallbackHandler`), so one gate covers both.

## Verified live (fresh origin `:8091`)

Hard reload of `/home` as `andrew` (who has the content that triggers the N+1:
own posts + boosts referencing remote RayvenMX), counting AP requests:

| Pattern | Before (620 baseline) | After (64.1) |
|---|---|---|
| `GET /ap/v1/u/{actor}` (local, andrew) | 5× | **1×** |
| `POST /ap/v1/proxy/{host}/users/RayvenMX` (remote) | 4× | **1×** |

- **No regression:** home timeline renders correctly — andrew's posts (incl.
  "Boosted" + "View boosted post →") and RayvenMX's post (remote actor, initial-letter
  "R" avatar fallback + resolved name) all display. **0 console errors** on `/home`.
- Build 0 warn/0 err; full suite green (`Iris.Web.Tests.dll` 62/62; one
  known-flaky delivery test in `Iris.Server.Tests.dll` fails in the full run,
  passes in isolation — unrelated to this client-only change).

## Remaining 64 topics (separate slices)

After 64.1, the dominant request spam on `/home` is the **per-item
likes/shares fan-out** (topic #4 + #5): 10 `.../likes` + 10 `.../shares` calls
on one load (3 andrew + 3 alice + 4 RayvenMX items × likes + shares). These
live in `EngagementBar.razor` (fast path 172/185, fallback 217/228) and
`ObjectDetail.razor` (446/452). Plus:

- **#3** (dup): one RayvenMX `.../likes` fires 2× — a re-render/double-init of
  the engagement-state loader.
- **#6** (dup): public feed (`GET /ap/v1/public/feed?limit=20`) fired 2× on
  mount (double component init) — in `PagedCollection.razor`.
- **#7** (410 + N+1): deleted-account notifications fire an avatar proxy fetch
  that 410-Gones — in `NotificationRow.razor` (skip when no cached avatar).
