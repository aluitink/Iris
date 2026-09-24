using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="IGlobalSearchService"/>: a case-insensitive substring search over the
/// instance's local actors and stored content objects (F-13 global search / directory).
/// </summary>
/// <remarks>
/// The search runs over the local surface only (no remote instances are queried). For each local actor
/// it matches the actor's <c>name</c>, <c>preferredUsername</c>, and IRI; for each stored content object
/// it matches the object's <c>content</c> and <c>name</c>. <see cref="Tombstone"/>s are skipped (a deleted
/// object has no searchable content), and objects that are actors are skipped in the content pass (they
/// are matched by the actor pass, not duplicated). An empty/whitespace query matches everything. The
/// combined result is ordered deterministically: actors first (sorted by IRI), then content objects
/// (sorted by IRI).
/// </remarks>
/// <para>
/// <strong>Local/remote discrimination.</strong> When the instance base IRI is available, a "local" actor
/// is one whose IRI starts with the instance base IRI (e.g. an actor at
/// <c>https://iris.example/ap/v1/u/alice</c> is local to <c>https://iris.example</c>). This is more
/// reliable than the <c>preferredUsername</c> heuristic (remote actors from other platforms often carry
/// a <c>preferredUsername</c> too). When the instance base IRI is not available, the store's
/// <c>preferredUsername</c> heuristic is used as a fallback.
/// </para>
public sealed class GlobalSearchService : IGlobalSearchService
{
    private readonly IPersistenceProvider _persistence;
    private readonly Iri? _instanceBase;
    private readonly IFollowFeedService? _followFeed;

    /// <summary>
    /// Initializes a new global search service over the given persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the actor + object stores). Must not be null.</param>
    /// <param name="instanceBase">The instance's base IRI (e.g. <c>https://iris.example</c>), used to
    /// distinguish local actors from cached remote actors by IRI prefix. When null, the store's
    /// <c>preferredUsername</c> heuristic is used as a fallback.</param>
    /// <param name="followFeed">The followed-feed service (S96 cross-instance post search). When non-null
    /// and the request carries a signed-in requester, the search additionally returns the requester's
    /// followed <em>remote</em> posts that match the query (walked from the follows' outboxes over the
    /// wire by the feed service). When null (a host without the feed service, or a unit test), the search
    /// is local-only (the prior behavior).</param>
    public GlobalSearchService(IPersistenceProvider persistence, Iri? instanceBase = null, IFollowFeedService? followFeed = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _instanceBase = instanceBase;
        _followFeed = followFeed;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> SearchAsync(string? query, CancellationToken ct = default, string? type = null, bool localOnly = false, Iri? requesterIri = null)
    {
        var (items, _) = await SearchPagedAsync(query, ct, type, int.MaxValue, 0, localOnly, requesterIri).ConfigureAwait(false);
        return items;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<IObjectOrLink> Items, int Total)> SearchPagedAsync(
        string? query,
        CancellationToken ct,
        string? type,
        int limit,
        int offset,
        bool localOnly = false,
        Iri? requesterIri = null)
    {
        var normalized = query?.Trim();

        var typeFilter = type?.Trim();
        var hasType = !string.IsNullOrWhiteSpace(typeFilter);
        var actorPass = !hasType || string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);
        var contentPass = !hasType || !string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);

        // Audience/visibility filter (139.2-s5): the content results always drop non-public items not
        // addressed to the requester (a null requester is anonymous and sees public content only), and
        // the total reflects only the visible content. The store's count helpers
        // (CountSearchMatchesAsync) have no visibility predicate, so the content is loaded, filtered in
        // memory (the objects are already deserialized, so their to/cc audience is readable), and the
        // total is computed from the filtered set. This replaces the previous unfiltered path, which
        // let a direct message surface in global search to anyone (the Phase 136.18 / 139.2-s5 gap).
        return await SearchPagedWithVisibilityAsync(
            normalized, ct, typeFilter, hasType, actorPass, contentPass, limit, offset, localOnly,
            requesterIri).ConfigureAwait(false);
    }

    /// <summary>
    /// The audience-aware search path (139.2-s5): the content results are filtered by
    /// <see cref="VisibilityFilter"/> so a non-public (followers-only or direct) item is returned only
    /// to its intended recipient (a null <paramref name="requesterIri"/> is anonymous and sees public
    /// content only), and the content total reflects only the visible content. Because the store's
    /// count helpers carry no visibility predicate, the content is loaded in full (it is already
    /// deserialized, so its <c>to</c>/<c>cc</c> audience is readable), filtered, and the total is
    /// computed from the filtered set. Actors are unaffected (a directory entry is about a person, not
    /// a specific post).
    /// </summary>
    private async Task<(IReadOnlyList<IObjectOrLink> Items, int Total)> SearchPagedWithVisibilityAsync(
        string? normalized,
        CancellationToken ct,
        string? typeFilter,
        bool hasType,
        bool actorPass,
        bool contentPass,
        int limit,
        int offset,
        bool localOnly,
        Iri? requesterIri)
    {
        var results = new List<IObjectOrLink>();

        // Actor pass (always visible — a directory entry is not gated by a post's audience).
        var actorMatches = new List<IObjectOrLink>();
        var actorTotal = 0;
        if (actorPass)
        {
            List<Actor> rawActors;
            if (localOnly && _instanceBase is not null)
            {
                // The local-only path already filters to instance-base IRIs (GetLocalActorsFilteredAsync),
                // so no further origin filter is needed here.
                rawActors = (await GetLocalActorsFilteredAsync(normalized, ct).ConfigureAwait(false)).Actors;
            }
            else
            {
                // The mixed path (the Search page, localOnly=false) scans the whole stored actor surface
                // (local + cached remote). Drop stale LOCAL actors whose IRI does NOT originate on this
                // instance (S5): a row persisted under a stale/dev base (e.g. http://localhost:8088, the
                // dev default when Iris:AdvertiseBase is unset) would otherwise surface as a ghost
                // duplicate next to the same handle's canonical public-base row. Remote actors (a
                // different origin) are kept — only same-handle local IRIs on a foreign base are the
                // defect. A stale local row is identified by a preferredUsername that matches a LOCAL
                // actor's handle (its canonical row); a remote actor's handle is not local. When the
                // instance base is unavailable there is no origin to compare against, so the store's
                // heuristic result is returned as-is.
                var allActors = await _persistence.Actors.SearchActorsAsync(normalized, int.MaxValue, 0, ct, localOnly).ConfigureAwait(false);
                var baseValue = _instanceBase?.Value.TrimEnd('/');
                var localHandles = new HashSet<string>(
                    allActors
                        .Where(a => a.Id is { Length: > 0 } id
                            && baseValue is { Length: > 0 } bv
                            && id.StartsWith(bv, StringComparison.OrdinalIgnoreCase))
                        .Where(a => a.PreferredUsername is { Length: > 0 })
                        .Select(a => a.PreferredUsername!),
                    StringComparer.OrdinalIgnoreCase);
                rawActors = allActors
                    .Where(a => IsSameInstanceActor(a, localHandles))
                    .ToList();
            }

            actorMatches = rawActors.Cast<IObjectOrLink>().ToList();

            // S30 (A8.2 — federated community discovery): the Directory's "All known" communities tab
            // (and the Search page, both localOnly=false) must list every community the instance knows.
            // Local communities live in the community store (provisioned, with an instance-base IRI) and
            // a CACHED remote community Group is ALSO persisted there — by RemoteCommunityPersister, via
            // the actor-document fetch path — but a Group is NOT an Actor, so it is absent from the actor
            // store the pass above reads. Without this the "All known" communities surface is empty for a
            // peer instance (the exact S30 A8.2 symptom: B cannot discover A's community to join it).
            // Merge the community-store Groups into the actor results: a local community (IRI on the
            // instance base) is excluded (it is listed by the Communities page's "All on this instance"
            // tab, not the federated directory), a cached remote community is kept, and a duplicate of a
            // row the actor store already surfaced is dropped. localOnly=true ("This instance") skips the
            // merge entirely (a remote community is, by definition, not on this instance).
            if (!localOnly)
            {
                actorMatches.AddRange(
                    await GetCachedRemoteCommunitiesAsync(rawActors, normalized, ct).ConfigureAwait(false));
            }

            actorTotal = actorMatches.Count;
        }

        // Content pass: load all matching content, apply the audience/visibility filter, then the type
        // filter. The total is the count of the visible, type-matching content. A stored actor (a
        // <c>Person</c>/<c>Organization</c> — matched by the actor pass) is excluded, as is a stored
        // community <c>Group</c> (S30 A8.2: a community is surfaced by the actor pass via the
        // community-store merge, not duplicated as content).
        var contentMatches = new List<IObject>();
        if (contentPass)
        {
            var contentAll = await _persistence.Objects.SearchObjectsAsync(normalized, int.MaxValue, 0, ct).ConfigureAwait(false);
            contentMatches = contentAll
                .Where(o => o is not Group)
                .Where(o => VisibilityFilter.IsVisibleTo(o, requesterIri))
                .Where(o => !hasType || ItemMatchesType(o, typeFilter!))
                .ToList();

            // S96 (cross-instance post search): the local content pass above only sees what THIS instance
            // has stored (its own posts + remote posts delivered into the inbox). A post a user follows on
            // a remote instance that has NOT been delivered here (e.g. a community post a user follows,
            // which the remote delivers as an Announce the inbox does not store) is absent from the local
            // store, so the search would miss it. When there is a signed-in requester AND the followed-feed
            // service is available, add the requester's followed REMOTE posts that match the query: the
            // feed service already walks the follows' outboxes over the wire (F-14 / S92), filtered by the
            // same content/name substring and the same audience/visibility rules. Only items NOT already in
            // the local content results (de-duplicated by object IRI) and authored by a REMOTE actor (not on
            // this instance's base) are appended — the requester's own posts and local follows are already
            // in the local pass (or are the requester's own outbox) and must not be double-counted.
            if (requesterIri is { } requester && _followFeed is { } feed)
            {
                contentMatches.AddRange(
                    await GetCrossInstanceContentAsync(requester, normalized, hasType, typeFilter, contentMatches, ct).ConfigureAwait(false));
            }
        }

        var total = actorTotal + contentMatches.Count;

        // Paginate: actors first (sorted by IRI), then content (already in the store's IRI order).
        var actorTaken = 0;
        if (actorPass && offset < actorMatches.Count)
        {
            var actorLimit = Math.Min(limit, actorMatches.Count - offset);
            actorTaken = actorLimit;
            results.AddRange(actorMatches.Skip(offset).Take(actorLimit));
        }

        var contentOffset = Math.Max(0, offset - actorMatches.Count);
        var remaining = limit - actorTaken;
        if (contentPass && contentOffset < contentMatches.Count && remaining > 0)
        {
            var contentLimit = Math.Min(remaining, contentMatches.Count - contentOffset);
            results.AddRange(contentMatches.Skip(contentOffset).Take(contentLimit));
        }

        return (results, total);
    }

    /// <summary>
    /// S96 (cross-instance post search): returns the requester's followed <em>remote</em> posts that match
    /// <paramref name="normalized"/> and are NOT already in <paramref name="existing"/>. These are the posts the
    /// requester follows on remote instances that have not been delivered into this instance's object store
    /// (so the local content pass does not see them). The followed-feed service walks the follows' outboxes
    /// over the wire (F-14 / S92) and applies the same content/name substring and audience/visibility
    /// filters, so only genuinely new remote content is returned.
    /// </summary>
    /// <remarks>
    /// Each feed item is unwrapped to its embedded content object (a <c>Create</c>/<c>Announce</c> carries
    /// the note in its <c>object</c>; a delivered post is wrapped in a synthetic <c>Create</c>). Items that
    /// are tombstones, actors (matched by the actor pass), or communities <c>Group</c> (matched by the
    /// community pass) are skipped. An item whose content-object IRI is already in
    /// <paramref name="existing"/> (the local content results) is dropped (de-duplication — the local copy
    /// is preferred). An item authored by a LOCAL actor (IRI on the instance base, or no instance base to
    /// compare against and the author is a local store actor) is dropped — only REMOTE posts are the new
    /// surface the local pass does not cover. When the instance base is unknown, every feed item is treated
    /// as remote (the conservative stance: better a possible duplicate the client de-dups by IRI than a
    /// missed remote post). A feed failure contributes nothing (a broken follow must not fail the search).
    /// </remarks>
    /// <param name="requester">The signed-in requesting actor (whose follows' outboxes are walked).</param>
    /// <param name="normalized">The normalized (trimmed) query, or null/whitespace for "list everything".</param>
    /// <param name="hasType">Whether a type filter is active.</param>
    /// <param name="typeFilter">The active type filter (e.g. <c>"Note"</c>), when <paramref name="hasType"/>.</param>
    /// <param name="existing">The local content results already matched (used to de-duplicate by object IRI
    /// and to decide which items are genuinely new).</param>
    /// <param name="ct">A cancellation token.</param>
    private async Task<IReadOnlyList<IObject>> GetCrossInstanceContentAsync(
        Iri requester,
        string? normalized,
        bool hasType,
        string? typeFilter,
        IReadOnlyList<IObject> existing,
        CancellationToken ct)
    {
        IReadOnlyList<IObjectOrLink> feed;
        try
        {
            feed = await _followFeed!.GetFeedAsync(
                requester,
                string.IsNullOrWhiteSpace(normalized) ? null : normalized!,
                requesterIri: requester,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A feed failure (a broken/unreachable follow, a signing error) must not fail the search —
            // the local results are returned as-is (the same "contributes nothing" stance the feed itself
            // takes for a single broken follow, 147.2).
            return [];
        }

        var instancePrefix = _instanceBase?.ToString().TrimEnd('/');
        var seen = new HashSet<string>(
            existing.Select(o => o.ResolveObjectIri()?.ToString() ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var matches = new List<IObject>();
        foreach (var item in feed)
        {
            // Unwrap the feed item to its embedded content object (a Create/Announce carries the note in
            // its `object`; a plain object is its own content). Skip non-content (social activities such as
            // Like/Follow have no embedded object to surface as a post).
            var content = UnwrapContentObject(item);
            if (content is null || content is Tombstone || content is Group)
            {
                continue;
            }

            var objectIri = content.ResolveObjectIri();
            if (objectIri is not { } objectIriValue)
            {
                continue;
            }

            // De-duplicate against the local content results (the local copy is preferred) and against
            // items already collected in this pass.
            if (!seen.Add(objectIriValue.ToString()))
            {
                continue;
            }

            // Keep only REMOTE posts: an author on this instance's base is local (already covered by the
            // local content pass or the requester's own outbox). When the instance base is unknown, every
            // author is treated as remote (conservative — see the remarks).
            if (instancePrefix is { Length: > 0 } prefix && IsLocalAuthor(content, prefix))
            {
                continue;
            }

            // Apply the same audience/visibility + type filters the local content pass applies, so a
            // remote followers-only / direct post does not surface to an unintended viewer.
            if (!VisibilityFilter.IsVisibleTo(content, requester))
            {
                continue;
            }

            if (hasType && !ItemMatchesType(content, typeFilter!))
            {
                continue;
            }

            matches.Add(content);
        }

        return matches;
    }

    /// <summary>
    /// Unwraps a feed item (an ActivityStreams activity or a plain object) to the embedded content object
    /// it represents: for a <c>Create</c>/<c>Announce</c> (or any activity), the first embedded
    /// <see cref="IObject"/> that is itself not an activity (the note); for a plain non-activity object,
    /// the object itself. Returns null when the item carries no embedded content (a link-only reference, a
    /// social activity with no object, or a <see cref="Link"/>).
    /// </summary>
    private static IObject? UnwrapContentObject(IObjectOrLink item)
    {
        if (item is not IObject obj)
        {
            return null; // a bare Link (no embedded document) has no searchable content
        }

        if (obj is not Activity activity)
        {
            return obj; // a plain object (a delivered note) is its own content
        }

        foreach (var referenced in activity.Object ?? [])
        {
            if (referenced is IObject refObj && refObj is not Activity)
            {
                return refObj;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the content object's author (<c>attributedTo</c>) is a LOCAL actor — its IRI starts with the
    /// instance base <paramref name="prefix"/>. An author that is a local <see cref="Actor"/>/
    /// <see cref="Group"/> object (not just a link) is local by construction.
    /// </summary>
    private static bool IsLocalAuthor(IObject content, string prefix)
    {
        foreach (var author in content.AttributedTo ?? [])
        {
            if (author is Actor || author is Group)
            {
                return true;
            }

            var iriValue = author.ResolveObjectIri()?.ToString() ?? string.Empty;
            if (iriValue.Length > 0 && iriValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all actors from the store and filters to those whose IRI starts with the instance base IRI
    /// (the true "local" actors). Applies the query match and returns the filtered list + total count.
    /// </summary>
    private async Task<(List<Actor> Actors, int Total)> GetLocalActorsFilteredAsync(string? query, CancellationToken ct)
    {
        var all = await _persistence.Actors.SearchActorsAsync(null, int.MaxValue, 0, ct, localOnly: false).ConfigureAwait(false);
        var baseValue = _instanceBase is { } ib ? ib.ToString() : string.Empty;
        var prefix = baseValue.TrimEnd('/');

        var matches = all
            .Where(a => a.Id is not null && a.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(a => string.IsNullOrWhiteSpace(query) || MatchesActor(a, query.Trim()))
            .OrderBy(a => a.Id ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        return (matches, matches.Count);
    }

    /// <summary>
    /// S30 (A8.2): returns the CACHED REMOTE community Groups that the instance knows (persisted to the
    /// community store by <c>RemoteCommunityPersister</c> during federation) and that are not already in
    /// <paramref name="rawActors"/>. A local community (IRI on the instance base) is excluded — it is
    /// listed by the Communities page's "All on this instance" tab, not the federated directory. This is
    /// the "All known" communities facet: a peer instance can discover (and therefore join/follow) a
    /// remote community it has cached, instead of the tab being empty.
    /// </summary>
    /// <param name="rawActors">The actor-store matches for this query (used only to de-duplicate by IRI
    /// — a community already surfaced there is not re-added).</param>
    /// <param name="query">The normalized (trimmed) query, or null/whitespace for "list everything". A
    /// community matches when its <c>name</c>, <c>preferredUsername</c>, or IRI contains the query — the
    /// same case-insensitive substring rule the actor pass applies. This keeps a no-match query empty
    /// (a community is never surfaced when nothing matches it).</param>
    /// <param name="ct">A cancellation token.</param>
    private async Task<IReadOnlyList<IObjectOrLink>> GetCachedRemoteCommunitiesAsync(
        IReadOnlyList<Actor> rawActors,
        string? query,
        CancellationToken ct)
    {
        var instancePrefix = _instanceBase?.Value.TrimEnd('/');
        var existing = new HashSet<string>(
            rawActors.Select(a => a.Id ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var results = new List<IObjectOrLink>();
        foreach (var communityIri in await _persistence.Communities.GetAllCommunityIrisAsync(ct).ConfigureAwait(false))
        {
            // A local community (IRI on the instance base) is not "all known" — it is this instance's own
            // community, listed elsewhere. When the instance base is unknown, the community store is
            // consulted as-is (the same conservative stance the actor pass takes).
            if (instancePrefix is { Length: > 0 } prefix
                && communityIri.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // De-duplicate against what the actor store already surfaced (and against ourselves) so a
            // community cannot appear twice in the same result.
            if (!added.Add(communityIri.Value) || existing.Contains(communityIri.Value))
            {
                continue;
            }

            if (!await _persistence.Communities.TryGetCommunityAsync(communityIri, out var community, ct).ConfigureAwait(false)
                || community is null)
            {
                continue;
            }

            // Apply the query filter in-memory (the community store has no query-search): a community is
            // surfaced only when the query matches its name / preferredUsername / IRI (an empty query
            // matches everything, mirroring the actor pass).
            if (!string.IsNullOrWhiteSpace(query) && !MatchesActor(community, query.Trim()))
            {
                continue;
            }

            results.Add(community);
        }

        return results;
    }

    /// <summary>
    /// True when <paramref name="actor"/> may be shown in the mixed (non-local-only) search path.
    /// <para>
    /// When the instance base IRI is known, an actor whose IRI begins with the instance base is LOCAL
    /// (kept — it is canonical by definition). An actor whose IRI does NOT begin with the instance base
    /// is either a REMOTE actor (kept — the search legitimately lists cached remote actors, including
    /// remote Iris actors from other instances that carry a <c>preferredUsername</c> in their own
    /// document) or a LOCAL actor persisted under a stale/dev base (S5 — e.g.
    /// <c>http://localhost:8088/ap/v1/u/x</c> on an instance advertised as
    /// <c>https://iris.example</c>). The two are distinguished by handle: a stale local actor has a
    /// <c>preferredUsername</c> that matches a LOCAL actor in <paramref name="localHandles"/> (its
    /// canonical row); a remote actor's handle is not in <paramref name="localHandles"/>.
    /// </para>
    /// <para>
    /// When the instance base IRI is unavailable there is no canonical IRI to compare against, so every
    /// actor is allowed (the store's <c>preferredUsername</c> heuristic, if any, still applies at the store
    /// layer).
    /// </para>
    /// </summary>
    /// <param name="actor">The actor to check.</param>
    /// <param name="localHandles">The set of preferredUsernames for all LOCAL actors on this instance
    /// (IRI on the instance base). A non-canonical actor whose handle is in this set is a stale local
    /// row (S5) and is dropped; a non-canonical actor whose handle is NOT in this set is a remote peer
    /// and is kept.</param>
    private bool IsSameInstanceActor(Actor actor, HashSet<string> localHandles)
    {
        if (_instanceBase is not { } baseIri || actor.Id is not { Length: > 0 } id)
        {
            return true;
        }

        var prefix = baseIri.Value.TrimEnd('/');

        // A local actor (IRI on the instance base) is canonical by definition — keep it.
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A non-canonical actor (IRI not on the instance base): if it carries a preferredUsername that
        // matches a LOCAL handle, it is a stale local row (S5 — a ghost duplicate of the canonical row)
        // and is dropped. If it carries no preferredUsername or a handle that is not local, it is a
        // remote peer (a cached actor from another instance, including remote Iris actors that also
        // carry a handle) and is kept.
        if (actor.PreferredUsername is { Length: > 0 } handle
            && localHandles.Contains(handle))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when the actor's <c>name</c>, <c>preferredUsername</c>, or IRI contains
    /// <paramref name="query"/> as a case-insensitive substring.
    /// </summary>
    private static bool MatchesActor(Actor actor, string query)
    {
        return (actor.Name is not null && actor.Name.Any(v => v is not null && v.Contains(query, StringComparison.OrdinalIgnoreCase)))
            || (actor.PreferredUsername is { Length: > 0 } username && username.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (actor.Id is { Length: > 0 } id && id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true when an object's ActivityStreams <c>@type</c> equals <paramref name="type"/>
    /// (case-insensitive). Used by the type filter to restrict a content pass to a single type (e.g.
    /// <c>"Note"</c>).
    /// </summary>
    private static bool ItemMatchesType(IObject obj, string type)
    {
        var actualType = obj.Type;
        if (actualType is not null &&
            actualType.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(obj.GetType().Name, type, StringComparison.OrdinalIgnoreCase);
    }
}
