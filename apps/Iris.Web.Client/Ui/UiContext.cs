using System.Collections.Concurrent;
using Iris.Client;
using Iris.Client.Collections;
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

    private sealed record FollowingEntry(HashSet<string> Set, List<Iri> List, DateTime At);
    private sealed record ActorEntry(IObject Doc, DateTime At);
    private sealed record MembershipEntry(HashSet<string> Set, DateTime At);

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
    // In-flight actor fetches, keyed by actor IRI value. Coalesces concurrent requests for the
    // same actor (e.g. N feed cards rendering the same author at once) into a single network
    // call — without this, each concurrent caller misses the TTL cache and fires its own GET
    // (local) / POST-proxy (remote) for the same IRI.
    private readonly ConcurrentDictionary<string, Task<IObject?>> _actorInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Per-object engagement counts (72.1): the /likes + /shares collection walk for a content
    // object, keyed by object IRI. A feed renders one EngagementBar per post; when the page
    // re-renders (e.g. ActorAvatar's async actor-fetch completes -> StateHasChanged) the bar is
    // re-created as a fresh instance and would otherwise re-walk both collections. Caching the
    // walk's result (counts + the viewer's like/boost state + the minted activity ids) on the
    // per-circuit UiContext means a re-created bar for the same post reuses the already-loaded
    // counts instead of re-firing the /likes + /shares round-trips. Mirrors the 64.1 actor gate.
    private readonly ConcurrentDictionary<string, Task<EngagementCounts>> _engagement = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _membershipGate = new(1, 1);

    private readonly IActorSessionAccessor _session;
    private readonly SemaphoreSlim _followingGate = new(1, 1);

    public UiContext(IActorSessionAccessor session)
    {
        _session = session;
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
    /// Gets an actor document by IRI, consulting the per-circuit actor cache (5-minute TTL).
    /// Returns null when the actor is not found or the client is unavailable.
    /// Concurrent calls for the same IRI are coalesced into a single network request, so N feed
    /// cards rendering the same author at once issue one GET (local) / POST-proxy (remote) rather
    /// than N.
    /// </summary>
    public async Task<IObject?> GetActorAsync(Iri actorIri)
    {
        if (_session.Client is not { } client)
        {
            return null;
        }

        if (_actors.TryGetValue(actorIri.Value, out var cached)
            && DateTime.UtcNow - cached.At < ActorTtl)
        {
            return cached.Doc;
        }

        // Coalesce in-flight fetches for this IRI: the first caller starts the fetch and publishes
        // its Task; concurrent callers await the same Task instead of each hitting the network.
        var fetchTask = _actorInFlight.GetOrAdd(actorIri.Value, _ => FetchActorAsync(client, actorIri));
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
    /// Performs a single actor fetch for <paramref name="actorIri"/>: reads the document from the
    /// network and, on success, stores it in the per-circuit actor cache for the TTL window.
    /// Returns null when the fetch fails or the actor is not found (nothing is cached on failure,
    /// so a later call can retry).
    /// </summary>
    private async Task<IObject?> FetchActorAsync(IActivityPubClient client, Iri actorIri)
    {
        IObject? doc;
        try
        {
            doc = await client.GetObjectAsync(actorIri);
        }
        catch
        {
            return null;
        }

        if (doc is not null)
        {
            _actors[actorIri.Value] = new ActorEntry(doc, DateTime.UtcNow);
        }

        return doc;
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
                var membersIri = AppendSegment(communityIri, "members");
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
