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
    public async Task<IReadOnlyList<IObjectOrLink>> SearchAsync(string? query, CancellationToken ct = default, string? type = null, bool localOnly = false)
    {
        var (items, _) = await SearchPagedAsync(query, ct, type, int.MaxValue, 0, localOnly).ConfigureAwait(false);
        return items;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<IObjectOrLink> Items, int Total)> SearchPagedAsync(
        string? query,
        CancellationToken ct,
        string? type,
        int limit,
        int offset,
        bool localOnly = false)
    {
        var normalized = query?.Trim();

        var typeFilter = type?.Trim();
        var hasType = !string.IsNullOrWhiteSpace(typeFilter);
        var actorPass = !hasType || string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);
        var contentPass = !hasType || !string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);

        int total;
        var results = new List<IObjectOrLink>();

        if (actorPass)
        {
            // When localOnly and the instance base is known, do IRI-prefix-based filtering in the
            // service (the store's preferredUsername heuristic is unreliable for remote actors from
            // other platforms that also carry a preferredUsername). Otherwise, delegate to the store.
            var useServiceFilter = localOnly && _instanceBase is not null;

            if (useServiceFilter)
            {
                var (filteredActors, filteredTotal) = await GetLocalActorsFilteredAsync(normalized, ct).ConfigureAwait(false);
                total = filteredTotal;

                if (contentPass)
                {
                    var contentTotal = hasType
                        ? (await _persistence.Objects.SearchObjectsAsync(normalized, int.MaxValue, 0, ct).ConfigureAwait(false))
                            .Count(o => ItemMatchesType(o, typeFilter!))
                        : await _persistence.Objects.CountSearchMatchesAsync(normalized, ct).ConfigureAwait(false);
                    total += contentTotal;

                    if (offset < filteredTotal)
                    {
                        var actorLimit = Math.Min(limit, filteredTotal - offset);
                        var actorSlice = filteredActors.Skip(offset).Take(actorLimit).Cast<IObjectOrLink>().ToList();
                        results.AddRange(actorSlice);

                        var remaining = limit - actorSlice.Count;
                        if (contentPass && remaining > 0)
                        {
                            var contentOffset = Math.Max(0, offset - filteredTotal);
                            var contentLimit = Math.Min(remaining, contentTotal - contentOffset);
                            results.AddRange(await _persistence.Objects.SearchObjectsAsync(normalized, contentLimit, contentOffset, ct).ConfigureAwait(false));
                        }
                    }
                    else if (contentPass)
                    {
                        var contentOffset = offset - filteredTotal;
                        if (contentOffset < contentTotal)
                        {
                            var contentLimit = Math.Min(limit, contentTotal - contentOffset);
                            results.AddRange(await _persistence.Objects.SearchObjectsAsync(normalized, contentLimit, contentOffset, ct).ConfigureAwait(false));
                        }
                    }
                }
                else
                {
                    if (offset < filteredTotal)
                    {
                        var actorLimit = Math.Min(limit, filteredTotal - offset);
                        results.AddRange(filteredActors.Skip(offset).Take(actorLimit).Cast<IObjectOrLink>());
                    }
                }
            }
            else
            {
                var actorTotal = await _persistence.Actors.CountSearchMatchesAsync(normalized, ct, localOnly).ConfigureAwait(false);

                if (contentPass && !hasType)
                {
                    var contentTotal = await _persistence.Objects.CountSearchMatchesAsync(normalized, ct).ConfigureAwait(false);
                    total = actorTotal + contentTotal;

                    var actorTaken = 0;
                    if (offset < actorTotal)
                    {
                        var actorLimit = Math.Min(limit, actorTotal - offset);
                        actorTaken = actorLimit;
                        results.AddRange(await _persistence.Actors.SearchActorsAsync(normalized, actorLimit, offset, ct, localOnly).ConfigureAwait(false));
                    }

                    var contentOffset = Math.Max(0, offset - actorTotal);
                    var remaining = limit - actorTaken;
                    if (contentOffset < contentTotal && remaining > 0)
                    {
                        var contentLimit = Math.Min(remaining, contentTotal - contentOffset);
                        results.AddRange(await _persistence.Objects.SearchObjectsAsync(normalized, contentLimit, contentOffset, ct).ConfigureAwait(false));
                    }
                }
                else
                {
                    var contentAll = contentPass
                        ? await _persistence.Objects.SearchObjectsAsync(normalized, int.MaxValue, 0, ct).ConfigureAwait(false)
                        : Array.Empty<IObject>();
                    var contentMatches = contentPass
                        ? contentAll.Where(o => ItemMatchesType(o, typeFilter!)).ToList()
                        : new List<IObject>();
                    total = actorTotal + contentMatches.Count;

                    var actorTaken = 0;
                    if (offset < actorTotal)
                    {
                        var actorLimit = Math.Min(limit, actorTotal - offset);
                        actorTaken = actorLimit;
                        results.AddRange(await _persistence.Actors.SearchActorsAsync(normalized, actorLimit, offset, ct, localOnly).ConfigureAwait(false));
                    }

                    var contentOffset = Math.Max(0, offset - actorTotal);
                    var remaining = limit - actorTaken;
                    if (contentOffset < contentMatches.Count && remaining > 0)
                    {
                        var contentLimit = Math.Min(remaining, contentMatches.Count - contentOffset);
                        results.AddRange(contentMatches.Skip(contentOffset).Take(contentLimit));
                    }
                }
            }
        }
        else
        {
            var contentAll = await _persistence.Objects.SearchObjectsAsync(normalized, int.MaxValue, 0, ct).ConfigureAwait(false);
            var contentMatches = hasType
                ? contentAll.Where(o => ItemMatchesType(o, typeFilter!)).ToList()
                : contentAll.ToList();
            total = contentMatches.Count;

            if (offset < contentMatches.Count)
            {
                var contentLimit = Math.Min(limit, contentMatches.Count - offset);
                results.AddRange(contentMatches.Skip(offset).Take(contentLimit));
            }
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
