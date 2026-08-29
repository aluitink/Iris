using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="ILikeStore"/> backed by a single concurrent dictionary
/// (liker IRI → set of liked-object IRIs).
/// </summary>
/// <remarks>
/// Ephemeral: likes vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryLikeStore : ILikeStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _liked = new();

    /// <inheritdoc/>
    public Task RecordLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _liked.AddOrUpdate(
            likerIri,
            _ => NewSet(likedObjectIri),
            (_, set) => { lock (set) { set.Add(likedObjectIri); } return set; });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        if (_liked.TryGetValue(likerIri, out var set))
        {
            lock (set) { removed = set.Remove(likedObjectIri); }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(likerIri));
    }

    /// <inheritdoc/>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_liked.TryGetValue(likerIri, out var set) && IsIn(set, likedObjectIri));
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }

    private IReadOnlyList<Iri> Snapshot(Iri key)
    {
        if (!_liked.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            return set.ToList();
        }
    }

    private static bool IsIn(HashSet<Iri> set, Iri iri)
    {
        lock (set)
        {
            return set.Contains(iri);
        }
    }
}
