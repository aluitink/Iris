using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IDislikeStore"/> backed by two concurrent dictionaries: the
/// <c>disliker → [disliked objects]</c> direction and the <c>disliked object → [dislikers]</c>
/// reverse index. Mirrors <see cref="InMemoryLikeStore"/>.
/// </summary>
public sealed class InMemoryDislikeStore : IDislikeStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _disliked = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _dislikedBy = new();
    // Deleted-actor filter (139.3-F2): an actor-existence predicate wired by the provider (the EF
    // sibling applies the same filter at the SQL level). Null = no filtering (standalone use, e.g. the
    // store's own unit tests), which preserves prior behavior.
    private System.Func<Iri, bool>? _sourceExists;

    /// <summary>
    /// Wires the deleted-actor filter (139.3-F2): read paths exclude edges whose source no longer
    /// names a stored actor. Called by the persistence provider; a store used standalone (unit tests)
    /// leaves this unset and filters nothing.
    /// </summary>
    /// <param name="exists">The actor-existence predicate, or null to disable filtering.</param>
    public void SetSourceExistsPredicate(System.Func<Iri, bool>? exists)
        => _sourceExists = exists;

    public void Clear()
    {
        _disliked.Clear();
        _dislikedBy.Clear();
    }

    public Task RecordDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddEdge(_disliked, dislikerIri, objectIri);
        AddEdge(_dislikedBy, objectIri, dislikerIri);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var removedForward = RemoveEdge(_disliked, dislikerIri, objectIri);
        var removedReverse = RemoveEdge(_dislikedBy, objectIri, dislikerIri);
        return Task.FromResult(removedForward || removedReverse);
    }

    public Task<bool> HasDislikedAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_disliked.TryGetValue(dislikerIri, out var set) && set.Contains(objectIri));
    }

    public Task<IReadOnlyList<Iri>> GetDislikersAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_dislikedBy.TryGetValue(objectIri, out var set))
        {
            return Task.FromResult<IReadOnlyList<Iri>>([]);
        }

        var exists = _sourceExists;
        return Task.FromResult<IReadOnlyList<Iri>>(exists is null ? [.. set] : [.. set.Where(exists)]);
    }

    private static void AddEdge(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> map,
        Iri from, Iri to)
    {
        map.AddOrUpdate(from, _ =>
        {
            var s = new HashSet<Iri> { to };
            return s;
        }, (_, s) =>
        {
            lock (s) { s.Add(to); }
            return s;
        });
    }

    private static bool RemoveEdge(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> map,
        Iri from, Iri to)
    {
        if (!map.TryGetValue(from, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Remove(to);
        }
    }
}
