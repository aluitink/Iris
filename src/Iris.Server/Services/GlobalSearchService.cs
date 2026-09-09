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
public sealed class GlobalSearchService : IGlobalSearchService
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new global search service over the given persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the actor + object stores). Must not be null.</param>
    public GlobalSearchService(IPersistenceProvider persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> SearchAsync(string? query, CancellationToken ct = default, string? type = null)
    {
        // The un-paged surface is the full result set (offset 0, no limit) — the same matching, ordering,
        // and type-filter rules as the paged search.
        var (items, _) = await SearchPagedAsync(query, ct, type, int.MaxValue, 0).ConfigureAwait(false);
        return items;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<IObjectOrLink> Items, int Total)> SearchPagedAsync(
        string? query,
        CancellationToken ct,
        string? type,
        int limit,
        int offset)
    {
        var normalized = query?.Trim();

        // A type filter (e.g. "Actor") restricts the result to a single ActivityStreams type so the
        // directory page searches actors only (no content). The two passes are independent: the actor
        // pass yields actors (every local actor is an `Actor`), the content pass yields content objects
        // (never actors). When a filter is present, only the pass whose items can match the type
        // contributes — "Actor" runs only the actor pass; a non-actor type (e.g. "Note") runs only the
        // content pass and filters each item by its type.
        var typeFilter = type?.Trim();
        var hasType = !string.IsNullOrWhiteSpace(typeFilter);
        var actorPass = !hasType || string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);
        var contentPass = !hasType || !string.Equals(typeFilter!, "Actor", StringComparison.OrdinalIgnoreCase);

        // The combined ordering is actors first, then content (each sub-list IRI-sorted), so the global
        // offset/limit slice starts in the actor pass. When no type filter is set the per-pass store
        // count is exact (the actor pass returns only actors, the content pass only content), so the
        // totals are computed with a cheap COUNT and each pass materializes only its slice of the page
        // (57.4 — no full-table scan). When a type filter restricts the content pass to a single type
        // (the count is then over the untyped surface), the content slice is taken from the full content
        // match set and filtered, so the total is exact.
        int total;
        var results = new List<IObjectOrLink>();

        if (actorPass)
        {
            var actorTotal = await _persistence.Actors.CountSearchMatchesAsync(normalized, ct).ConfigureAwait(false);

            if (contentPass && !hasType)
            {
                // No type filter: both passes are exact and independent.
                var contentTotal = await _persistence.Objects.CountSearchMatchesAsync(normalized, ct).ConfigureAwait(false);
                total = actorTotal + contentTotal;

                var actorTaken = 0;
                if (offset < actorTotal)
                {
                    var actorLimit = Math.Min(limit, actorTotal - offset);
                    actorTaken = actorLimit;
                    results.AddRange(await _persistence.Actors.SearchActorsAsync(normalized, actorLimit, offset, ct).ConfigureAwait(false));
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
                // A type filter restricts the content pass to a single ActivityStreams type (the store
                // count is over the untyped surface), so the content slice is taken from the full content
                // match set and filtered to the type — keeping the total exact.
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
                    results.AddRange(await _persistence.Actors.SearchActorsAsync(normalized, actorLimit, offset, ct).ConfigureAwait(false));
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
        else
        {
            // Actor pass skipped (type filter is a non-actor type): the content pass starts at offset 0.
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

        // Fall back to the concrete CLR type's simple name (e.g. a deserialized <see cref="Note"/> is
        // "Note") when the object does not carry an explicit `@type` value.
        return string.Equals(obj.GetType().Name, type, StringComparison.OrdinalIgnoreCase);
    }
}
