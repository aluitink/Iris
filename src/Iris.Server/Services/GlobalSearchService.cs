using Iris.Core;
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
            if (localOnly && _instanceBase is not null)
            {
                var (filteredActors, filteredTotal) = await GetLocalActorsFilteredAsync(normalized, ct).ConfigureAwait(false);
                actorTotal = filteredTotal;
                actorMatches = filteredActors.Cast<IObjectOrLink>().ToList();
            }
            else
            {
                actorTotal = await _persistence.Actors.CountSearchMatchesAsync(normalized, ct, localOnly).ConfigureAwait(false);
                if (actorTotal > 0)
                {
                    actorMatches = (await _persistence.Actors.SearchActorsAsync(normalized, int.MaxValue, 0, ct, localOnly).ConfigureAwait(false))
                        .Cast<IObjectOrLink>().ToList();
                }
            }
        }

        // Content pass: load all matching content, apply the audience/visibility filter, then the type
        // filter. The total is the count of the visible, type-matching content.
        var contentMatches = new List<IObject>();
        if (contentPass)
        {
            var contentAll = await _persistence.Objects.SearchObjectsAsync(normalized, int.MaxValue, 0, ct).ConfigureAwait(false);
            contentMatches = contentAll
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
