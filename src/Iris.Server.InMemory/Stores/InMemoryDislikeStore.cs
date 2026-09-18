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
        return Task.FromResult<IReadOnlyList<Iri>>(_dislikedBy.TryGetValue(objectIri, out var set)
            ? [.. set]
            : []);
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
