using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IActorStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// Ephemeral: actors vanish on restart. Keys are the actor IRIs. Thread-safe.
/// </remarks>
public sealed class InMemoryActorStore : IActorStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, Actor> _actors = new();
    // IRIs of actors that were once stored and then removed (139.3-F2). The edge stores use this to
    // exclude edges whose source was a *locally-stored-then-deleted* actor, while still keeping edges
    // whose source is a remote actor (or a local actor not yet provisioned) — neither of which is in
    // this set, so their edges are unaffected by the filter.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, byte> _removed = new();

    /// <summary>
    /// Removes all actors (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _actors.Clear();
        _removed.Clear();
    }

    /// <summary>
    /// Synchronously reports whether an edge source IRI should surface in the edge stores' read paths
    /// (the deleted-actor filter, 139.3-F2). The IRI is hidden only when it was a locally-stored actor
    /// that has since been removed (it is in <c>_removed</c> and no longer in <c>_actors</c>); a stored
    /// actor, a remote actor, or an un-provisioned local actor all surface. A synchronous
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.ContainsKey"/> pair —
    /// no allocation, no async — so the edge stores apply the filter inside their read paths.
    /// </summary>
    /// <param name="actorIri">The edge source IRI to check.</param>
    /// <returns><see langword="true"/> when the IRI is not a deleted local actor (the edge surfaces).</returns>
    public bool SourceSurvives(Iri actorIri) => !_removed.ContainsKey(actorIri) || _actors.ContainsKey(actorIri);

    /// <inheritdoc/>
    public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _actors.TryGetValue(actorIri, out actor);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutActorAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new ArgumentException("Actor must have a non-null Id.", nameof(actor));
        }

        ct.ThrowIfCancellationRequested();
        var iri = new Iri(actor.Id);
        _actors[iri] = actor;
        _removed.TryRemove(iri, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var removed = _actors.TryRemove(actorIri, out _);
        if (removed)
        {
            _removed[actorIri] = 0;
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Actor>>(_actors.Values.ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Actor>> SearchActorsAsync(string? query, int limit, int offset, CancellationToken ct = default, bool localOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        // localOnly restricts the search to this instance's own actors (the directory). The in-memory
        // store only ever holds locally provisioned actors, so the filter is a no-op here — but the
        // signature is kept consistent with the durable stores (a remote actor cached here would carry
        // no PreferredUsername, so it would be excluded by the same predicate).
        var matches = _actors.Values
            .Where(a => IsLocal(a, localOnly))
            .Where(a => !hasQuery || MatchesActor(a, normalized!))
            .OrderBy(a => a.Id ?? string.Empty, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<Actor>>(matches);
    }

    /// <inheritdoc/>
    public Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default, bool localOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        var count = _actors.Values.Count(a => IsLocal(a, localOnly) && (!hasQuery || MatchesActor(a, normalized!)));
        return Task.FromResult(count);
    }

    /// <summary>
    /// True when the actor passes the <paramref name="localOnly"/> filter. A local actor carries a
    /// <c>preferredUsername</c> (its handle); a cached remote actor does not.
    /// </summary>
    private static bool IsLocal(Actor actor, bool localOnly)
        => !localOnly || actor.PreferredUsername is { Length: > 0 };

    /// <summary>
    /// Returns true when the actor's <c>name</c>, <c>preferredUsername</c>, or IRI contains
    /// <paramref name="query"/> as a case-insensitive substring (the same matching the global search
    /// service applies to the actor pass).
    /// </summary>
    private static bool MatchesActor(Actor actor, string query)
    {
        return ContainsInStrings(actor.Name, query)
            || (actor.PreferredUsername is { Length: > 0 } username && username.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (actor.Id is { Length: > 0 } id && id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsInStrings(IEnumerable<string>? values, string query)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
