using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IAnnounceStore"/> backed by two concurrent dictionaries: the
/// <c>announcer → [announced objects]</c> direction and the <c>announced object → [announcers]</c>
/// reverse index.
/// </summary>
/// <remarks>
/// Ephemeral: announces vanish on restart. Thread-safe. Both directions are maintained together on
/// record / remove (the <c>announced object → [announcers]</c> reverse index backs the object's
/// <c>shares</c> collection — the per-object boost counter, decision 056 (d)).
/// </remarks>
public sealed class InMemoryAnnounceStore : IAnnounceStore
{
    // announcer → set of announced-object IRIs (the actor's boosts).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _announced = new();
    // announced object → set of announcer IRIs (the object's `shares` reverse index, decision 056 (d)).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _announcedBy = new();
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
    /// Removes all announce edges and reverse indices (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _announced.Clear();
        _announcedBy.Clear();
    }

    /// <inheritdoc/>
    public Task RecordAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddEdge(_announced, announcerIri, announcedObjectIri);
        AddEdge(_announcedBy, announcedObjectIri, announcerIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Both directions are maintained together, so both are always removed (the edge is present in
        // either). Removing from the reverse index is mandatory (it backs the object's `shares`
        // collection) — a short-circuit `||` would leave a stale announcer in the reverse index whenever
        // the forward edge was already present.
        var removedForward = RemoveEdge(_announced, announcerIri, announcedObjectIri);
        var removedReverse = RemoveEdge(_announcedBy, announcedObjectIri, announcerIri);
        return Task.FromResult(removedForward || removedReverse);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncedAsync(Iri announcerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_announced, announcerIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<bool> HasAnnouncedAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsIn(_announced, announcerIri, announcedObjectIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncersAsync(Iri announcedObjectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_announcedBy, announcedObjectIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> GetAnnouncersBatchAsync(
        IReadOnlyCollection<Iri> announcedObjectIris, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<Iri, IReadOnlyList<Iri>>();
        foreach (var iri in announcedObjectIris)
        {
            if (_announcedBy.TryGetValue(iri, out var set))
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
    public Task<IReadOnlySet<Iri>> HasAnnouncedBatchAsync(
        Iri announcerIri, IReadOnlyCollection<Iri> objectIris, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new HashSet<Iri>();
        if (_announced.TryGetValue(announcerIri, out var set))
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
