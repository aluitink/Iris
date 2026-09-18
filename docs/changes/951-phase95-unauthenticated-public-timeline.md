# 95.1 — Unauthenticated public timeline

**Status:** COMPLETE
**Phase:** 95 — Unauthenticated Public Timeline

## Objective

The user reported (PLAN.md Up Next 95): *"This seems to be showing in an odd order — alice items
from 3 days ago are first and then it switches to a correct feed style output — review how this
feed is generated, check if unauthenticated users can access content/media, check if there is a
problem with rendering the user icons, I only see the initials."*

Three distinct sub-issues:

1. **Feed ordering** — the public timeline displayed posts in an odd order (an actor's older posts
   before another actor's newer posts), rather than a consistent newest-first chronology.
2. **Unauthenticated content/media access** — verify that logged-out visitors can view the public
   timeline and access its content and media.
3. **User icon rendering** — logged-out visitors only saw initials (letter fallbacks) instead of
   actor avatar icons.

## Root-cause analysis

### 1. Feed ordering

`PublicFeedService.GetPublicFeedAsync` merged the outboxes of up to 5 local actors by:

```
fetch each actor's outbox (newest-first per actor)
  → merge in actor-IRI alphabetical order
  → dedup
  → truncate to page size
```

Because the merge was **grouped by actor** (not sorted by date), an actor whose IRI sorted
earlier (alphabetically) had all of its posts emitted before the next actor's posts — regardless
of the posts' dates. For example, if `alice`'s IRI sorted before `bob`'s, alice's post from 3
days ago would appear before bob's post from 5 minutes ago. This is exactly the "odd order" the
user described.

### 3. User icon rendering

`UiContext.GetActorAsync` had an early return:

```csharp
if (_session.Client is null)
    return null;  // ← signed-out: no signing client → actor doc not fetched → no icon
```

When signed out, `_session.Client` is null (no signing keys), so the actor document was never
fetched. `ActorIdentityHelper.IconIri(actor)` received `null`, and `ActorAvatar` fell back to
initials.

The fix: when `_session.Client` is null, fall back to a plain (unsigned) `HttpClient` to fetch the
actor document. This works for **local** actors (same-origin, no CORS). For **remote** actors
(mastodon.social, mas.to, etc.), the browser blocks the cross-origin fetch (no
`Access-Control-Allow-Origin` header) — this is expected and unavoidable without a server-side
proxy.

## What was built

### 1. `PublicFeedService` — date-sort the merged feed

Added a global sort by the activity's `published` date (newest-first) **after** merging the
per-actor outboxes and **before** dedup/truncate.

```csharp
public async Task<IEnumerable<IObjectOrLink>> GetPublicFeedAsync(Iri? after, int? count)
{
    ...
    var merged = new List<IObjectOrLink>(mergedSoFar);
    merged = SortByDateNewestFirst(merged);   // ← NEW: global date sort
    var truncated = TruncateDedup(merged, after, count, max);
    ...
}

private static List<IObjectOrLink> SortByDateNewestFirst(IReadOnlyList<IObjectOrLink> feed)
    => feed
        .OrderByDescending(static item => ExtractPublishedDate(item))
        .ToList();

private static DateTime? ExtractPublishedDate(IObjectOrLink item)
{
    switch (item)
    {
        case Activity activity: return activity.Published;
        case IObject obj:       return obj.Published;
        default:                return null;
    }
}
```

- `OrderByDescending` with a `null`-key selector: items with a `null` date sort **last**
  (before the items with the earliest dates in a descending order, nulls are treated as
  "less than" any date, so they sink to the end of a descending sort).
- The sort is **stable**: items with the same `published` date preserve their original
  (merged) order.
- `Activity.Published` is always set: `InboxProcessor` sets `delivery.Activity.Published =
  DateTime.UtcNow` when null (line 74); `CreateActivityHandler` sets
  `embedded.Published = activity.Published ?? DateTime.UtcNow` (line 219).

### 2. `UiContext` — anonymous actor-document fetch

`UiContext` now takes an `IHttpClientFactory` and, when the session's signing client is null,
fetches the actor document with a plain (unsigned) `HttpClient`.

```csharp
// Constructor: added IHttpClientFactory
public UiContext(
    ActorSession session,
    IActorDocumentCache cache,
    IHttpClientFactory httpClientFactory)  // ← NEW
{
    ...
    _httpClientFactory = httpClientFactory;
}

// GetActorAsync: removed the early-return-null for signed-out
public Task<IObject?> GetActorAsync(Iri iri, CancellationToken ct = default)
{
    // ← NO LONGER: if (_session.Client is null) return Task.FromResult<IObject?>(null);
    // Now proceeds to cache check + in-flight coalescing regardless of auth state.

    return Task.Run(async () =>
    {
        var cached = await _cache.GetOrCreateAsync(iri, async ct =>
            await FetchActorAsync(iri, ct), ct).ConfigureAwait(false);
        return cached is null ? null : cached as IObject;
    }, CancellationToken.None);
}

// FetchActorAsync: uses session client when available, else plain HttpClient
private async Task<IObject?> FetchActorAsync(Iri iri, CancellationToken ct)
{
    try
    {
        string json;
        if (_session.Client is not null)
        {
            json = await _session.Client.GetActorDocumentAsync(iri, ct).ConfigureAwait(false);
        }
        else
        {
            json = await FetchActorDocumentAnonymousAsync(iri, ct).ConfigureAwait(false);
        }

        return ActivityJson.Deserialize<IObjectOrLink>(json) as IObject;
    }
    catch (Exception ex)
    {
        _logger?.LogDebug(ex, "Failed to fetch actor document for {Iri}", iri);
        return null;
    }
}

// NEW: plain unsigned fetch for signed-out visitors
private async Task<string> FetchActorDocumentAnonymousAsync(Iri iri, CancellationToken ct)
{
    var client = _httpClientFactory.CreateClient("iris");
    var response = await client.GetAsync(iri, ct).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
}
```

- **DI:** `UiContext` is registered as `AddScoped<UiContext>()` in `Program.cs`. `IHttpClientFactory`
  is a singleton registered by `AddHttpClient()` (via the `"iris"` named client). It resolves
  automatically — no DI registration change needed.
- **Signed-in path:** unchanged (still uses the session's signing client, which adds HTTP signatures
  for remote actor documents).
- **Signed-out path:** new (plain `HttpClient`, no signature). Works for local actors (same-origin);
  remote actors fail on CORS (expected).

### 3. Unit tests — `PublicFeedServiceTests`

6 new unit tests pinning the date-sort contract:

| Test | Pins |
|------|------|
| `Feed_MergesActorOutboxes_SortedNewestFirstByDate` | 3 actors with interleaved dates → global newest-first |
| `Feed_MultipleActors_AllPostsSortedNewestFirst` | 2 actors, 2 posts each → 4 posts sorted by date |
| `Feed_PostsSameDate_StableMergeOrder` | Same-date posts preserve merge order (stable) |
| `Feed_NoPublishedDate_SortsLast` | Undated posts sink to the end |
| `Feed_EmptyOutboxes_ReturnsEmpty` | No actors → empty feed |
| `Feed_ExcludesNonPersonActors` | `Group`/`Organization` actors are excluded (only `Person`) |

## Verification

**Build:** `dotnet build -c Release` → 0 warnings / 0 errors (`TreatWarningsAsErrors` on).

**New coded tests (6):** `PublicFeedServiceTests` — all 6 pass.

**Full suite:** `dotnet test -c Release --filter "Category!=Slow"` → **1874 passed / 1 failed**.
The single failure is the pre-existing flaky federation test
(`FollowEdgeConvergenceIntegrationTests.Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections`)
that intermittently fails under full-suite parallel load and passes in isolation — unrelated to this
change.

**Live Playwright verification:**

1. **Ordering:** Published a test post as `andrew` ("Phase 95 test post: verifying public timeline
   ordering and icon rendering."). Navigated to the public timeline (`/`) as a **logged-out**
   visitor. The test post appeared **first** (1m ago), followed by older remote posts in correct
   newest-first order (17m, 1h, 2h, …). The "odd order" (an actor's older posts before another
   actor's newer posts) is gone.

2. **Icon rendering:** Inspected the DOM for `andrew`'s post card. The `ActorAvatar` component
   rendered an `<img>` with `src="/ap/v1/media/proxy?url=https%3A%2F%2Fpicsum.photos%2Fseed%2Firis-
   avatar%2F200%2F200"` — a same-origin media-proxy URL fetching the avatar from picsum.photos.
   The icon renders correctly for logged-out visitors (local actors). Remote actors' icons still
   fail (CORS: browser blocks cross-origin fetches without `Access-Control-Allow-Origin`) —
   expected and unavoidable without a server-side proxy.

3. **Unauthenticated content/media access:** The public timeline loads without login. Content is
   served directly from the feed JSON (same-origin). Media (images in posts) is served via the
   same-origin media proxy (`/ap/v1/media/proxy?url=…`), which works for both local and remote
   media. No authentication required.

## Notes / follow-ups

- **Remote actor icons** (for logged-out visitors) cannot render in the browser due to CORS. The
  server's media proxy (`/ap/v1/media/proxy`) could be extended to proxy actor documents, but
  that's a larger change (caching, rate-limiting, MIME handling) and out of scope for this phase.
- The **community feed** (`CommunityFeedService`) uses an outbox-position round-robin merge (by
  design, tested in `CommunityFeedCorrectnessIntegrationTests`). The public feed now uses true
  date-sort. These are intentionally different.
- No WASM/client changes beyond `UiContext` (which is server-side WASM — the Blazor WebAssembly
  app runs in the browser but `UiContext` is a scoped service in the WASM DI container). No new
  NuGet packages.
