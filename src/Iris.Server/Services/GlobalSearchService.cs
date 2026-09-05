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
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

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

        // The actor (directory) pass: every local actor whose name / preferredUsername / IRI matches.
        var matchedActors = new List<IObjectOrLink>();
        if (actorPass)
        {
            var actors = await _persistence.Actors.ListActorsAsync(ct).ConfigureAwait(false);
            foreach (var actor in actors.OrderBy(a => a.Id ?? string.Empty, StringComparer.Ordinal))
            {
                if (!hasQuery
                    || ContainsInStrings(actor.Name, normalized!)
                    || (actor.PreferredUsername is { Length: > 0 } username
                        && username.Contains(normalized!, StringComparison.OrdinalIgnoreCase))
                    || (actor.Id is { Length: > 0 } id
                        && id.Contains(normalized!, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedActors.Add(actor);
                }
            }
        }

        // The content pass: every stored content object (not a Tombstone, not an actor) whose content /
        // name matches.
        var matchedObjects = new List<IObjectOrLink>();
        if (contentPass)
        {
            var objects = await _persistence.Objects.ListObjectsAsync(ct).ConfigureAwait(false);
            foreach (var obj in objects.OrderBy(o => o.Id ?? string.Empty, StringComparer.Ordinal))
            {
                // A deleted object (Tombstone) has no searchable content; an actor is matched by the
                // actor pass (skip it here so it is not duplicated).
                if (obj is Tombstone or Actor)
                {
                    continue;
                }

                // A type filter that is not "Actor" further restricts to items of that ActivityStreams
                // type (e.g. "Note"); "Actor" never matches a content object.
                if (hasType && !ItemMatchesType(obj, typeFilter!))
                {
                    continue;
                }

                if (!hasQuery
                    || ContainsInStrings(obj.Content, normalized!)
                    || ContainsInStrings(obj.Name, normalized!))
                {
                    matchedObjects.Add(obj);
                }
            }
        }

        // Actors first, then content objects (each sub-list already IRI-sorted).
        var results = new List<IObjectOrLink>(matchedActors.Count + matchedObjects.Count);
        results.AddRange(matchedActors);
        results.AddRange(matchedObjects);
        return results;
    }

    /// <summary>
    /// Returns true when any value in the multi-valued <c>content</c>/<c>name</c> property contains
    /// <paramref name="query"/> as a substring (case-insensitive, ordinal).
    /// </summary>
    private static bool ContainsInStrings(IEnumerable<string>? values, string query)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null
                && value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
