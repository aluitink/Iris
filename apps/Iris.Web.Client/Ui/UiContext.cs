using System.Collections.Concurrent;
using Iris.Client;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Web.Client.Accounts;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Client.Ui;

/// <summary>
/// UI-layer context that caches ActivityPub reads the Blazor components need repeatedly
/// (follow-state, actor documents) so multiple components sharing a circuit don't each
/// walk the same collections. Sits above the general-purpose <see cref="IActivityPubClient"/>
/// and adds the UI-specific caching/prefetch concerns the client shouldn't know about.
/// </summary>
public sealed class UiContext
{
    private static readonly TimeSpan FollowingTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActorTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MembershipTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LemmyScoreTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ContentObjectTtl = TimeSpan.FromMinutes(2);

    private sealed record FollowingEntry(HashSet<string> Set, List<Iri> List, DateTime At);

    /// <summary>
    /// The signed-in user's cached blocks + mutes sets (case-insensitive IRI values) with the
    /// time the sets were walked. Returned by <see cref="GetModerationSetsAsync"/>; the
    /// <see cref="GetModerationStateAsync"/> convenience wraps it in per-target booleans.
    /// </summary>
    public sealed record ModerationEntry(HashSet<string> BlockedSet, HashSet<string> MutedSet, DateTime At);
    private sealed record ActorEntry(IObject Doc, DateTime At);
    private sealed record MembershipEntry(HashSet<string> Set, DateTime At);
    private sealed record LemmyScoreEntry(LemmyPostScore? Score, DateTime At);
    private sealed record ContentObjectEntry(IObject Doc, DateTime At);

    /// <summary>
    /// The result of walking a content object's <c>/likes</c> + <c>/shares</c> collections (72.1):
    /// the like/boost counts, the signed-in viewer's net like/boost state, and the minted
    /// Like/Announce activity ids an unlike / un-boost (an <c>Undo</c>) references. Cached per
    /// object IRI on <see cref="UiContext"/> so a re-created <c>EngagementBar</c> (the page
    /// re-renders when an <c>ActorAvatar</c> fetch completes) reuses the walk instead of re-firing
    /// both collection round-trips. The counts are the <em>server-derived</em> values; an
    /// optimistic like/boost delta is applied on top by the component, never cached here.
    /// </summary>
    public sealed record EngagementCounts(
        int LikeCount,
        int BoostCount,
        bool Liked,
        bool Boosted,
        string? LikeActivityIri,
        string? AnnounceActivityIri);

    private readonly ConcurrentDictionary<string, FollowingEntry> _following = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActorEntry> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MembershipEntry> _memberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContentObjectEntry> _contentObjects = new(StringComparer.OrdinalIgnoreCase);
    // In-flight actor fetches, keyed by actor IRI value. Coalesces concurrent requests for the
    // same actor (e.g. N feed cards rendering the same author at once) into a single network
    // call — without this, each concurrent caller misses the TTL cache and fires its own GET
    // (local) / POST-proxy (remote) for the same IRI.
    private readonly ConcurrentDictionary<string, Task<IObject?>> _actorInFlight = new(StringComparer.OrdinalIgnoreCase);
    // In-flight content-object fetches, keyed by object IRI value (147.1). Coalesces concurrent
    // requests for the same content object (e.g. a reply parent + an announce target pointing at
    // the same remote post) into a single network call.
    private readonly ConcurrentDictionary<string, Task<IObject?>> _contentInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Per-object engagement counts (72.1): the /likes + /shares collection walk for a content
    // object, keyed by object IRI. A feed renders one EngagementBar per post; when the page
    // re-renders (e.g. ActorAvatar's async actor-fetch completes -> StateHasChanged) the bar is
    // re-created as a fresh instance and would otherwise re-walk both collections. Caching the
    // walk's result (counts + the viewer's like/boost state + the minted activity ids) on the
    // per-circuit UiContext means a re-created bar for the same post reuses the already-loaded
    // counts instead of re-firing the /likes + /shares round-trips. Mirrors the 64.1 actor gate.
    private readonly ConcurrentDictionary<string, Task<EngagementCounts>> _engagement = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LemmyScoreEntry> _lemmyScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<LemmyPostScore?>> _lemmyScoreInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _membershipGate = new(1, 1);
    // The signed-in user's blocks + mutes sets, keyed by the user's actor IRI value. A collection
    // page renders one ModerationActions per actor card, and each card must know whether the
    // signed-in user has blocked/muted its target — without this cache, N cards re-walk the user's
    // /blocks + /mutes collections N times (2N collection reads for the same data). Caching the
    // full sets on the per-circuit UiContext means the page's first card walks both collections
    // once; every other card (and any re-render that re-creates a card) is an O(1) HashSet lookup.
    private readonly ConcurrentDictionary<string, ModerationEntry> _moderation = new(StringComparer.OrdinalIgnoreCase);
    // In-flight moderation walks, keyed by the user's actor IRI value. Coalesces concurrent callers
    // (N cards rendering at once, all hitting a cold cache) into a single pair of collection walks —
    // mirrors the _actorInFlight / _contentInFlight coalescing for actor / content-object fetches.
    private readonly ConcurrentDictionary<string, Task<ModerationEntry>> _moderationInFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly IActorSessionAccessor _session;
    private readonly SemaphoreSlim _followingGate = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;

    public UiContext(IActorSessionAccessor session, IHttpClientFactory httpClientFactory)
    {
        _session = session;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// The signed-in actor's IRI, or null when signed out.
    /// </summary>
    public Iri? CurrentActorId => _session.ActorId;

    /// <summary>
    /// Whether the signed-in actor follows <paramref name="targetIri"/>. Consults the per-circuit
    /// following-set cache; on a miss, walks the follower's following collection once and caches
    /// the full set for the TTL window. Subsequent checks for other targets are O(1) lookups.
    /// </summary>
    public async Task<bool> IsFollowingAsync(Iri targetIri)
        => (await GetFollowingActorIrisAsync()).Contains(targetIri);

    /// <summary>
    /// Resolves the IRIs of the actors the signed-in actor follows (the following collection),
    /// de-duplicated, in first-seen order. Consults the per-circuit following-set cache; on a miss,
    /// walks the following collection once and caches the full set for the TTL window. Returns an
    /// empty list when signed out, the client is unavailable, or the collection cannot be read.
    /// </summary>
    /// <remarks>
    /// The compose <c>@handle</c> autocomplete (71.5) uses this as the default candidate list (the
    /// accounts the signed-in user actually knows), merged with live instance-search results as the
    /// user types. The result is also the source of truth for <see cref="IsFollowingAsync"/>.
    /// </remarks>
    /// <returns>The followed actors' IRIs; possibly empty.</returns>
    public async Task<IReadOnlyList<Iri>> GetFollowingActorIrisAsync()
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return [];
        }

        if (_following.TryGetValue(me.Value, out var entry)
            && DateTime.UtcNow - entry.At < FollowingTtl)
        {
            return entry.List;
        }

        await _followingGate.WaitAsync();
        try
        {
            // Re-check under the gate (another task may have refreshed while we waited).
            if (_following.TryGetValue(me.Value, out var fresh)
                && DateTime.UtcNow - fresh.At < FollowingTtl)
            {
                return fresh.List;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Iri>();
            try
            {
                await foreach (var item in client.GetCollectionItemsAsync(me.FollowingOf()))
                {
                    var iri = item.ResolveObjectIri()?.Value;
                    if (iri is not null && set.Add(iri))
                    {
                        list.Add(new Iri(iri));
                    }
                }
            }
            catch
            {
                // Non-fatal: return the (possibly empty) list we have so far.
            }

            _following[me.Value] = new FollowingEntry(set, list, DateTime.UtcNow);
            return list;
        }
        finally
        {
            _followingGate.Release();
        }
    }

    /// <summary>
    /// Finds the server-minted IRI of the <c>Follow</c> activity the signed-in actor sent to
    /// <paramref name="targetIri"/> by scanning the actor's outbox. Returns null when no such
    /// activity is found or the outbox cannot be read.
    /// </summary>
    public async Task<string?> GetFollowActivityIriAsync(Iri targetIri)
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return null;
        }

        try
        {
            await foreach (var item in client.GetCollectionItemsAsync(me.OutboxOf()))
            {
                if (item is Follow follow && follow.Id is { Length: > 0 } id)
                {
                    var target = follow.Object?.FirstOrDefault()?.ResolveObjectIri();
                    if (target is not null && target == targetIri)
                    {
                        return id;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: return null.
        }

        return null;
    }

    /// <summary>
    /// Invalidates the cached following set for the signed-in actor (call after a follow/unfollow
    /// so the next <see cref="IsFollowingAsync"/> re-reads the server).
    /// </summary>
    public void InvalidateFollowing()
    {
        if (_session.ActorId is { } me)
        {
            _following.TryRemove(me.Value, out _);
        }
    }

    /// <summary>
    /// The cached moderation state of the signed-in actor: whether the user has blocked or muted
    /// <paramref name="targetIri"/>. Consults the per-circuit blocks/mutes-set cache; on a miss,
    /// walks the user's <c>/blocks</c> + <c>/mutes</c> collections once and caches both full sets for
    /// the TTL window. Subsequent checks for other targets are O(1) lookups — the N actor cards on a
    /// collection page each used to re-walk both collections (2N reads for the same data), but with
    /// this cache the page's first card pays the walk and the rest (and any re-render that
    /// re-creates a card) hit the cache. Returns (false, false) when signed out or the client is
    /// unavailable.
    /// </summary>
    public async Task<(bool IsBlocked, bool IsMuted)> GetModerationStateAsync(Iri targetIri)
    {
        if (_session.ActorId is not { } me)
        {
            return (false, false);
        }

        var entry = await GetModerationSetsAsync();
        return (
            entry.BlockedSet.Contains(targetIri.Value),
            entry.MutedSet.Contains(targetIri.Value));
    }

    /// <summary>
    /// Resolves the signed-in actor's full blocks + mutes sets (de-duplicated, case-insensitive).
    /// Consults the per-circuit moderation cache; on a miss, walks the user's <c>/blocks</c> and
    /// <c>/mutes</c> collections once and caches the sets for the TTL window. Concurrent callers are
    /// coalesced into a single walk (the first caller starts it and publishes its
    /// <see cref="Task"/>; later callers await the same task). Returns empty sets when signed out,
    /// the client is unavailable, or a collection cannot be read.
    /// </summary>
    public async Task<ModerationEntry> GetModerationSetsAsync()
    {
        if (_session.ActorId is not { } me)
        {
            return new ModerationEntry([], [], DateTime.UtcNow);
        }

        if (_moderation.TryGetValue(me.Value, out var cached)
            && DateTime.UtcNow - cached.At < FollowingTtl)
        {
            return cached;
        }

        if (_session.Client is not { } client)
        {
            // No client (signed out / key still loading): report empty. Not cached — a later call
            // with a client can still walk.
            return new ModerationEntry([], [], DateTime.UtcNow);
        }

        var fetchTask = _moderationInFlight.GetOrAdd(me.Value, _ => WalkModerationAsync(client, me));
        try
        {
            var entry = await fetchTask;
            _moderation[me.Value] = entry;
            return entry;
        }
        finally
        {
            // Clear the in-flight marker so a later call (e.g. after InvalidateModeration) can re-walk.
            _moderationInFlight.TryRemove(me.Value, out _);
        }
    }

    /// <summary>
    /// Performs a single <c>/blocks</c> + <c>/mutes</c> walk for the signed-in actor. Returns a
    /// <see cref="ModerationEntry"/>; on a walk failure it returns the (possibly partial) sets
    /// derived so far rather than throwing (a failure is non-fatal — the buttons default to the
    /// unmoderated state).
    /// </summary>
    private static async Task<ModerationEntry> WalkModerationAsync(IActivityPubClient client, Iri me)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await foreach (var item in client.GetBlocksAsync(me))
            {
                if (ItemIri(item) is { } iri)
                {
                    blocked.Add(iri.Value);
                }
            }

            await foreach (var item in client.GetMutesAsync(me))
            {
                if (ItemIri(item) is { } iri)
                {
                    muted.Add(iri.Value);
                }
            }
        }
        catch
        {
            // Non-fatal: return the sets derived so far.
        }

        return new ModerationEntry(blocked, muted, DateTime.UtcNow);
    }

    /// <summary>
    /// Invalidates the cached blocks/mutes sets for the signed-in actor (call after a block/unblock
    /// or mute/unmute so the next <see cref="GetModerationStateAsync"/> re-reads the server).
    /// </summary>
    public void InvalidateModeration()
    {
        if (_session.ActorId is { } me)
        {
            _moderation.TryRemove(me.Value, out _);
        }
    }

    /// <summary>
    /// Gets an actor document by IRI, consulting the per-circuit actor cache (5-minute TTL).
    /// Returns null when the actor is not found or the client is unavailable.
    /// Concurrent calls for the same IRI are coalesced into a single network request, so N feed
    /// cards rendering the same author at once issue one GET (local) / POST-proxy (remote) rather
    /// than N.
    /// </summary>
    public async Task<IObject?> GetActorAsync(Iri actorIri)
    {
        if (_actors.TryGetValue(actorIri.Value, out var cached)
            && DateTime.UtcNow - cached.At < ActorTtl)
        {
            return cached.Doc;
        }

        // The session's key-load fetches the current user's actor document (147.1): reuse it instead
        // of issuing a redundant GET /u/{handle}. Match on the document's id (not the claim-derived
        // ActorId) to avoid IRI normalization mismatches. When the state is loaded but the key-load
        // is still in progress (ActorDocument null), await EnsureReadyAsync (idempotent) to let it
        // finish before falling through to a network fetch.
        if (_session.HasLoadedState)
        {
            if (_session.ActorDocument is not { Id: { Length: > 0 } })
            {
                await _session.EnsureReadyAsync();
            }

            if (_session.ActorDocument is { Id: { Length: > 0 } docId }
                && string.Equals(docId, actorIri.Value, StringComparison.OrdinalIgnoreCase))
            {
                var doc = _session.ActorDocument;
                _actors[actorIri.Value] = new ActorEntry(doc, DateTime.UtcNow);
                return doc;
            }
        }

        // Coalesce in-flight fetches for this IRI: the first caller starts the fetch and publishes
        // its Task; concurrent callers await the same Task instead of each hitting the network.
        var fetchTask = _actorInFlight.GetOrAdd(actorIri.Value, _ => FetchActorAsync(actorIri));
        try
        {
            return await fetchTask;
        }
        finally
        {
            // Clear the in-flight marker so a later call (e.g. after the TTL expires) can re-fetch.
            // TryRemove returns the value that was present; we only care that the marker is gone
            // by the time this caller finishes, which is safe because any concurrent caller that
            // already grabbed `fetchTask` holds its own reference to it.
            _actorInFlight.TryRemove(actorIri.Value, out _);
        }
    }

    /// <summary>
    /// Fetches a content object (a Note, Article, …) by IRI, with per-circuit caching and
    /// concurrent-request coalescing (147.1). Mirrors <see cref="GetActorAsync"/> for non-actor
    /// objects: a reply parent and an announce target pointing at the same remote post collapse
    /// into a single network call. Returns null when the fetch fails or the object is not found.
    /// </summary>
    public async Task<IObject?> GetContentObjectAsync(Iri objectIri)
    {
        if (_contentObjects.TryGetValue(objectIri.Value, out var cached)
            && DateTime.UtcNow - cached.At < ContentObjectTtl)
        {
            return cached.Doc;
        }

        if (_session.Client is null)
        {
            return null;
        }

        var fetchTask = _contentInFlight.GetOrAdd(objectIri.Value, async _ =>
        {
            // 138: a remote (cross-origin) object is read through the home instance's proxy endpoint
            // (POST /ap/v1/proxy/{target}), which is cache-first: it serves the object's cached
            // document when fresh (no live fetch, and the per-object /likes+/shares sync walk is
            // skipped) and otherwise fetches it live and refreshes the cache. On a proxy miss the
            // read falls through to the session's signing client (which proxies a remote read itself).
            if (IsRemoteObjectIri(objectIri))
            {
                try
                {
                    var viaProxy = await FetchViaProxyAsync(objectIri);
                    if (viaProxy is not null)
                    {
                        return viaProxy;
                    }
                }
                catch
                {
                    // The proxy read failed; fall through to the signed-client live fetch.
                }
            }

            try
            {
                return await _session.Client!.GetObjectAsync(objectIri, CancellationToken.None);
            }
            catch
            {
                return null;
            }
        });
        try
        {
            var doc = await fetchTask;
            if (doc is not null)
            {
                _contentObjects[objectIri.Value] = new ContentObjectEntry(doc, DateTime.UtcNow);
            }
            return doc;
        }
        finally
        {
            _contentInFlight.TryRemove(objectIri.Value, out _);
        }
    }

    /// <summary>
    /// Performs a single actor fetch for <paramref name="actorIri"/>: reads the document and, on
    /// success, stores it in the per-circuit actor cache for the TTL window.
    /// <para>
    /// For a <em>remote</em> (cross-origin) actor, the home instance's cached-actor endpoint
    /// (<c>GET /ap/v1/actor?iri=…</c>, 134.1) is consulted first: it serves the actor document the
    /// instance stored in its database during federation, so a known actor's profile renders even
    /// when its home instance is unreachable or the account has been deactivated (a live fetch would
    /// 410). When the instance has no cached copy (404), the fetch falls through to the live path.
    /// </para>
    /// <para>
    /// The live path: when the session's signing client is available (signed in) the fetch is made
    /// through it (signed requests, routed through the home proxy for a remote IRI); when the
    /// session's client is null (signed out) it falls back to a plain (unsigned) <c>HttpClient</c> —
    /// actor documents are public, so an anonymous read succeeds. Returns null when the fetch fails
    /// or the actor is not found (nothing is cached on failure, so a later call can retry).
    /// </para>
    /// </summary>
    private async Task<IObject?> FetchActorAsync(Iri actorIri)
    {
        IObject? doc = null;

        // 138: a remote (cross-origin) actor is read through the home instance's proxy endpoint
        // (POST /ap/v1/proxy/{target}), which is cache-first: it serves the actor's cached document
        // when fresh (no live fetch — a deactivated / unreachable account still renders) and otherwise
        // fetches it live and refreshes the cache. This is the single seam for every remote actor /
        // object read. A local actor is NOT routed here: its canonical document (via /u/{handle})
        // carries the Iris-local extensions (capabilities, feed, …) the cached copy does not.
        if (IsRemoteActorIri(actorIri))
        {
            try
            {
                doc = await FetchViaProxyAsync(actorIri);
            }
            catch
            {
                // The proxy read failed (network / parse). Fall through to the live fetch.
                doc = null;
            }
        }

        if (doc is null)
        {
            try
            {
                if (_session.Client is { } client)
                {
                    // Signed-in: use the session's signing client (signed requests, routed through
                    // the home proxy for a remote IRI by the client's own proxy fallback).
                    doc = await client.GetObjectAsync(actorIri);
                }
                else
                {
                    // Signed-out: use a plain (unsigned) HttpClient (actor documents are public).
                    doc = await FetchActorDocumentAnonymousAsync(actorIri);
                }
            }
            catch
            {
                return null;
            }
        }

        if (doc is not null)
        {
            _actors[actorIri.Value] = new ActorEntry(doc, DateTime.UtcNow);
        }

        return doc;
    }

    /// <summary>
    /// True when <paramref name="actorIri"/> is a remote (cross-origin) actor — its host differs from
    /// the home instance's origin (the "iris" <c>HttpClient</c>'s <c>BaseAddress</c>). A local actor
    /// (same origin, e.g. <c>https://iris.luit.ink/ap/v1/u/alice</c>) is not remote. When the home
    /// origin cannot be determined, the IRI is treated as remote (the cached lookup is a safe no-op
    /// that 404s and falls through to the live fetch).
    /// </summary>
    private bool IsRemoteActorIri(Iri actorIri)
    {
        if (!Uri.TryCreate(actorIri.Value, UriKind.Absolute, out var actorUri)
            || actorUri.Scheme != Uri.UriSchemeHttp && actorUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var homeBase = _httpClientFactory.CreateClient("iris").BaseAddress;
        if (homeBase is null)
        {
            return true;
        }

        return !string.Equals(actorUri.Host, homeBase.Host, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRemoteObjectIri(Iri objectIri)
    {
        if (!Uri.TryCreate(objectIri.Value, UriKind.Absolute, out var objUri)
            || objUri.Scheme != Uri.UriSchemeHttp && objUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var homeBase = _httpClientFactory.CreateClient("iris").BaseAddress;
        if (homeBase is null)
        {
            return true;
        }

        return !string.Equals(objUri.Host, homeBase.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a remote (cross-origin) actor or object document through the home instance's proxy
    /// endpoint (<c>POST /ap/v1/proxy/{target}</c>, 138). The proxy is cache-first: it serves the
    /// target's cached document when fresh (no live fetch — a deactivated / unreachable account still
    /// renders, and the per-object /likes+/shares walk is skipped) and otherwise fetches it live and
    /// refreshes the cache. The request is same-origin (the browser dials its own instance), so the
    /// transport attaches the site cookie and the proxy identifies the actor from it — no Basic
    /// credentials are needed. Returns null on a non-success (404 unknown, 410 gone, 403/429 policy,
    /// or any failure) so the caller falls through to its live-fetch path.
    /// </summary>
    private async Task<IObject?> FetchViaProxyAsync(Iri targetIri)
    {
        var http = _httpClientFactory.CreateClient("iris");
        var path = $"/ap/v1/proxy/{Uri.EscapeDataString(targetIri.Value)}";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // The proxy relays the REAL method (X-Iris-Proxy-Method); default is a GET read.
            Headers = { { "X-Iris-Proxy-Method", "GET" } },
        };
        request.Headers.Accept.ParseAdd(ActivityJson.ActivityJsonContentType);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            // 404 (unknown target), 410 (gone), 403/429 (policy), or any failure: the caller falls
            // through to a live fetch.
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return ActivityJson.Deserialize<IObjectOrLink>(json) as IObject;
    }

    /// <summary>
    /// Fetches an actor document via plain HTTP (no ActivityPub signing). Used when signed out
    /// (the session's signing client is null). The actor document is public, so an unsigned
    /// <c>GET</c> succeeds. Returns null when the fetch fails or the actor is not found.
    /// </summary>
    private async Task<IObject?> FetchActorDocumentAnonymousAsync(Iri actorIri)
    {
        var http = _httpClientFactory.CreateClient("iris");
        using var response = await http.GetAsync(actorIri.Value);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return ActivityJson.Deserialize<IObjectOrLink>(json) as IObject;
    }

    /// <summary>
    /// Invalidates a cached actor document (e.g. after the actor's profile is updated).
    /// </summary>
    public void InvalidateActor(Iri actorIri)
    {
        _actors.TryRemove(actorIri.Value, out _);
    }

    /// <summary>
    /// Resolves a content object's engagement counts by walking its <c>/likes</c> + <c>/shares</c>
    /// collections once, and caches the result per object IRI for the circuit's lifetime (72.1).
    /// The cached value carries the like/boost counts, the viewer's (<paramref name="viewerIri"/>)
    /// net like/boost state, and the minted Like/Announce activity ids an unlike / un-boost (an
    /// <c>Undo</c>) references. Concurrent callers for the same object are coalesced into a single
    /// walk (the first caller starts it and publishes its <see cref="Task"/>; later callers await the
    /// same task) — without this, each re-created <c>EngagementBar</c> for the same post would
    /// re-fire both collection round-trips. The counts returned are the <em>server-derived</em>
    /// values; the component applies any optimistic like/boost delta on top and calls
    /// <see cref="InvalidateEngagement"/> after a mutation so a later re-render re-walks (the cached
    /// value goes stale once the viewer changes their like/boost, since the minted id + net state
    /// change with it).
    /// </summary>
    /// <param name="objectIri">The content object whose engagement to resolve.</param>
    /// <param name="viewerIri">
    /// The signed-in viewer whose like/boost state + minted activity ids to resolve (the bar's
    /// <c>Session.ActorId</c>); <see langword="null"/> when signed out (the counts are still walked,
    /// but the like/boost state + minted ids are reported absent).
    /// </param>
    public async Task<EngagementCounts> GetEngagementCountsAsync(Iri objectIri, Iri? viewerIri)
    {
        if (_session.Client is not { } client)
        {
            // No client (signed out / unavailable): report empty. Not cached — a later call with a
            // client can still walk.
            return new EngagementCounts(0, 0, false, false, null, null);
        }

        // Remote (cross-origin) objects: the /likes and /shares collections live on the remote
        // instance. Walking them through the proxy returns 401 (the remote does not expose these
        // without auth) and floods the console with errors. The user cannot like/boost remote
        // objects from Iris anyway, so report empty counts and skip the walk entirely.
        if (IsRemoteObjectIri(objectIri))
        {
            return new EngagementCounts(0, 0, false, false, null, null);
        }

        var fetchTask = _engagement.GetOrAdd(objectIri.Value, _ => WalkEngagementAsync(client, objectIri, viewerIri));
        try
        {
            return await fetchTask;
        }
        finally
        {
            // Clear the in-flight marker so a later call (e.g. after InvalidateEngagement) can re-walk.
            _engagement.TryRemove(objectIri.Value, out _);
        }
    }

    /// <summary>
    /// Performs a single <c>/likes</c> + <c>/shares</c> walk for <paramref name="objectIri"/>: counts
    /// the items, resolves <paramref name="viewerIri"/>'s net like/boost state, and captures the
    /// minted Like/Announce activity ids (for an unlike / un-boost <c>Undo</c>). Returns an
    /// <see cref="EngagementCounts"/>; on a walk failure it returns the (possibly partial) counts
    /// derived so far rather than throwing (the bar treats a failure as non-fatal).
    /// </summary>
    private static async Task<EngagementCounts> WalkEngagementAsync(IActivityPubClient client, Iri objectIri, Iri? viewerIri)
    {
        var me = viewerIri;
        int likes = 0;
        int boosts = 0;
        bool iLiked = false;
        bool iBoosted = false;
        string? likeIri = null;
        string? announceIri = null;

        try
        {
            await foreach (var item in client.GetLikesAsync(
                objectIri, new CollectionQuery { Limit = 100, BypassCache = true }, CancellationToken.None))
            {
                likes++;
                if (!iLiked && me is { } meIri && LikeActorIri(item) is { } liker && IriEquals(meIri, liker))
                {
                    iLiked = true;
                    likeIri = ItemIri(item)?.Value;
                }
            }

            await foreach (var item in client.GetSharesAsync(
                objectIri, new CollectionQuery { Limit = 100, BypassCache = true }, CancellationToken.None))
            {
                boosts++;
                if (!iBoosted && me is { } meIri && AnnounceActorIri(item) is { } booster && IriEquals(meIri, booster))
                {
                    iBoosted = true;
                    announceIri = ItemIri(item)?.Value;
                }
            }
        }
        catch
        {
            // Non-fatal: return the counts derived so far (the bar treats a failure as "no data").
        }

        return new EngagementCounts(likes, boosts, iLiked, iBoosted, likeIri, announceIri);
    }

    /// <summary>
    /// Invalidates the cached engagement counts for a content object (call after the viewer likes,
    /// un-likes, boosts, or un-boosts it so the next <see cref="GetEngagementCountsAsync"/> re-walks —
    /// the cached minted id + net state are stale once the viewer's engagement changes).
    /// </summary>
    public void InvalidateEngagement(Iri objectIri)
    {
        _engagement.TryRemove(objectIri.Value, out _);
    }

    /// <summary>
    /// Resolves the IRI an <see cref="IObjectOrLink"/> collection item points at (a <c>Link</c>'s
    /// <c>href</c>, or an embedded object's <c>id</c>) — null when neither is present.
    /// </summary>
    private static Iri? ItemIri(IObjectOrLink item)
        => item switch
        {
            ILink { Href: { } href } => new Iri(href),
            IObject { Id: { Length: > 0 } id } => new Iri(id),
            _ => null,
        };

    /// <summary>
    /// Resolves the IRI of the actor who issued a <see cref="Like"/> collection item (the liker).
    /// Null when the item is not a <c>Like</c> (or carries no resolvable <c>actor</c>).
    /// </summary>
    private static Iri? LikeActorIri(IObjectOrLink item)
        => item is Like like && like.Actor is { } actors
            ? ResolveActorIri(actors)
            : null;

    /// <summary>
    /// Resolves the IRI of the actor who issued an <see cref="Announce"/> (boost) collection item (the
    /// booster). Null when the item is not an <c>Announce</c> (or carries no resolvable <c>actor</c>).
    /// </summary>
    private static Iri? AnnounceActorIri(IObjectOrLink item)
        => item is Announce announce && announce.Actor is { } actors
            ? ResolveActorIri(actors)
            : null;

    /// <summary>
    /// Resolves the first resolvable IRI from an ActivityStreams actor/object reference collection.
    /// </summary>
    private static Iri? ResolveActorIri(IEnumerable<IObjectOrLink> refs)
    {
        foreach (var reference in refs)
        {
            if (reference is ILink { Href: { } href })
            {
                return new Iri(href);
            }

            if (reference is IObject { Id: { Length: > 0 } id })
            {
                return new Iri(id);
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two IRIs for the same resource, tolerating trailing-slash differences.
    /// </summary>
    private static bool IriEquals(Iri a, Iri b)
        => a == b || a.Value.TrimEnd('/') == b.Value.TrimEnd('/');

    /// <summary>
    /// Whether the signed-in actor is a member of <paramref name="communityIri"/>. Consults the
    /// per-circuit membership cache (keyed by community IRI); on a miss, walks the community's
    /// <c>members</c> collection once and caches the full set for the TTL window.
    /// </summary>
    public async Task<bool> IsMemberAsync(Iri communityIri)
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return false;
        }

        if (_memberships.TryGetValue(communityIri.Value, out var entry)
            && DateTime.UtcNow - entry.At < MembershipTtl)
        {
            return entry.Set.Contains(me.Value);
        }

        await _membershipGate.WaitAsync();
        try
        {
            if (_memberships.TryGetValue(communityIri.Value, out var fresh)
                && DateTime.UtcNow - fresh.At < MembershipTtl)
            {
                return fresh.Set.Contains(me.Value);
            }

            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Capability-aware: resolve the members IRI from the community document (137.2).
                // A Lemmy community exposes members via /followers; Iris/Mastodon via /members.
                var communityDoc = await GetActorAsync(communityIri);
                var membersIri = communityDoc is not null
                    ? communityDoc.ResolveMembersIri(communityIri)
                    : AppendSegment(communityIri, "members");
                await foreach (var item in client.GetCollectionItemsAsync(membersIri, new CollectionQuery(BypassCache: true)))
                {
                    var iri = item.ResolveObjectIri()?.Value;
                    if (iri is not null)
                    {
                        members.Add(iri);
                    }
                }
            }
            catch
            {
                // Non-fatal: return the (possibly empty) set we have so far.
            }

            _memberships[communityIri.Value] = new MembershipEntry(members, DateTime.UtcNow);
            return members.Contains(me.Value);
        }
        finally
        {
            _membershipGate.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached membership set for a community (call after a join/leave so the next
    /// <see cref="IsMemberAsync"/> re-reads the server).
    /// </summary>
    public void InvalidateMembership(Iri communityIri)
    {
        _memberships.TryRemove(communityIri.Value, out _);
    }

    /// <summary>
    /// Fetches a Lemmy post's score data (upvotes, downvotes, net score, comment count) from the
    /// Lemmy REST API, with a per-circuit TTL cache. The ActivityPub document for a Lemmy post
    /// does not carry vote information; this is the only way to surface it in the Iris client.
    /// Returns null when the post IRI is not a recognizable Lemmy post IRI or the fetch fails.
    /// </summary>
    public async Task<LemmyPostScore?> GetLemmyPostScoreAsync(Iri postIri, CancellationToken ct = default)
    {
        if (_lemmyScores.TryGetValue(postIri.Value, out var cached)
            && DateTime.UtcNow - cached.At < LemmyScoreTtl)
        {
            return cached.Score;
        }

        var fetchTask = _lemmyScoreInFlight.GetOrAdd(postIri.Value, _ => FetchLemmyScoreAsync(postIri, ct));
        try
        {
            var score = await fetchTask;
            _lemmyScores[postIri.Value] = new LemmyScoreEntry(score, DateTime.UtcNow);
            return score;
        }
        finally
        {
            _lemmyScoreInFlight.TryRemove(postIri.Value, out _);
        }
    }

    private async Task<LemmyPostScore?> FetchLemmyScoreAsync(Iri postIri, CancellationToken ct)
    {
        try
        {
            await _session.EnsureReadyAsync();
        }
        catch
        {
            return null;
        }

        if (_session.Client is not { } client)
        {
            return null;
        }

        try
        {
            return await client.GetLemmyPostScoreAsync(postIri, ct);
        }
        catch
        {
            return null;
        }
    }

    private static Iri AppendSegment(Iri iri, string segment)
    {
        var baseUri = iri.Uri;
        var builder = new UriBuilder(baseUri);
        var path = builder.Path;
        if (path.Length == 0 || !path.EndsWith('/'))
        {
            path += "/";
        }

        builder.Path = path + segment;
        return new Iri(builder.Uri);
    }
}
