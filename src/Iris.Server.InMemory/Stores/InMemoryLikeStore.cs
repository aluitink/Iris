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

    /// <summary>
    /// Removes all like edges and reverse indices (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _liked.Clear();
        _likedBy.Clear();
    }

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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_liked, likerIri, filterSource: true));
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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_likedBy, likedObjectIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> GetLikersBatchAsync(
        IReadOnlyCollection<Iri> likedObjectIris, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<Iri, IReadOnlyList<Iri>>();
        foreach (var iri in likedObjectIris)
        {
            if (_likedBy.TryGetValue(iri, out var set))
            {
                lock (set)
                {
                    if (set.Count > 0)
                    {
                        result[iri] = set.ToList();
                    }
                }
            }
        }
        return Task.FromResult<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>>(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlySet<Iri>> HasLikedBatchAsync(
        Iri likerIri, IReadOnlyCollection<Iri> objectIris, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new HashSet<Iri>();
        if (_liked.TryGetValue(likerIri, out var set))
        {
            lock (set)
            {
                foreach (var iri in objectIris)
                {
                    if (set.Contains(iri))
                    {
                        result.Add(iri);
                    }
                }
            }
        }
        return Task.FromResult<IReadOnlySet<Iri>>(result);
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

    private IReadOnlyList<Iri> Snapshot(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key,
        bool filterSource = false)
    {
        if (!index.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            var exists = _sourceExists;
            if (filterSource && exists is not null)
            {
                return set.Where(exists).ToList();
            }

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
