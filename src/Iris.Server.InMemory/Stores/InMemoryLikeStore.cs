using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="ILikeStore"/> backed by two concurrent dictionaries: the <c>liker → [liked
/// objects]</c> direction and the <c>liked object → [likers]</c> reverse index.
/// </summary>
/// <remarks>
/// Ephemeral: likes vanish on restart. Thread-safe. Both directions are maintained together on record
/// / remove (the <c>liked object → [likers]</c> reverse index backs the object's <c>likes</c>
/// collection — the per-object like counter, decision 056 (d)).
/// </remarks>
public sealed class InMemoryLikeStore : ILikeStore
{
    // liker → set of liked-object IRIs (the actor's `liked` collection).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _liked = new();
    // liked object → set of liker IRIs (the object's `likes` reverse index, decision 056 (d)).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _likedBy = new();

    /// <inheritdoc/>
    public Task RecordLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddEdge(_liked, likerIri, likedObjectIri);
        AddEdge(_likedBy, likedObjectIri, likerIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Both directions are maintained together, so both are always removed (the edge is present in
        // either). Removing from the reverse index is mandatory (it backs the object's `likes`
        // collection) — a short-circuit `||` would leave a stale liker in the reverse index whenever the
        // forward edge was already present.
        var removedForward = RemoveEdge(_liked, likerIri, likedObjectIri);
        var removedReverse = RemoveEdge(_likedBy, likedObjectIri, likerIri);
        return Task.FromResult(removedForward || removedReverse);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_liked, likerIri));
    }

    /// <inheritdoc/>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsIn(_liked, likerIri, likedObjectIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikersAsync(Iri likedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_likedBy, likedObjectIri));
    }

    private static void AddEdge(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri source, Iri target)
    {
        index.AddOrUpdate(
            source,
            _ => NewSet(target),
            (_, set) => { lock (set) { set.Add(target); } return set; });
    }

    private static bool RemoveEdge(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri source, Iri target)
    {
        if (!index.TryGetValue(source, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Remove(target);
        }
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }

    private static IReadOnlyList<Iri> Snapshot(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key)
    {
        if (!index.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            return set.ToList();
        }
    }

    private static bool IsIn(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri source, Iri target)
    {
        return index.TryGetValue(source, out var set) && IsIn(set, target);
    }

    private static bool IsIn(HashSet<Iri> set, Iri iri)
    {
        lock (set)
        {
            return set.Contains(iri);
        }
    }
}
