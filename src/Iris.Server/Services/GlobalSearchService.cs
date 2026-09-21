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

    /// <summary>
    /// Initializes a new global search service over the given persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the actor + object stores). Must not be null.</param>
    /// <param name="instanceBase">The instance's base IRI (e.g. <c>https://iris.example</c>), used to
    /// distinguish local actors from cached remote actors by IRI prefix. When null, the store's
    /// <c>preferredUsername</c> heuristic is used as a fallback.</param>
    public GlobalSearchService(IPersistenceProvider persistence, Iri? instanceBase = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _instanceBase = instanceBase;
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
