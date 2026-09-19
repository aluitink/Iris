using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IModerationStore"/> (F-07) backed by concurrent dictionaries: a forward
/// index (blocker IRI → set of blocked IRIs) and an inverse index (blocked IRI → set of blocker IRIs)
/// for block edges, a forward index (flagger IRI → set of flagged IRIs) for flag edges, and a forward
/// index (muter IRI → set of muted IRIs) for mute edges.
/// </summary>
/// <remarks>
/// Ephemeral: moderation edges vanish on restart. Thread-safe. The block indexes are kept in lockstep
/// (a record removes nothing from the other; a remove clears both), so the forward
/// (<see cref="IModerationStore.GetBlocksAsync"/>) and inverse
/// (<see cref="IModerationStore.GetBlockersAsync"/>) queries are both O(1) lookups. The flag and mute
/// indexes are forward-only (an actor's <c>flags</c> / <c>mutes</c> collection) — there is no inverse
/// flag/mute query (no delivery-suppression use), so a single forward index suffices for each.
/// </remarks>
public sealed class InMemoryModerationStore : IModerationStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _blocks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _blockers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _flags = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _mutes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Iri Flagger, Iri Flagged), DateTimeOffset> _flagTimestamps = new();
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
    /// Removes all moderation edges (blocks, flags, mutes) and reverse indices (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _blocks.Clear();
        _blockers.Clear();
        _flags.Clear();
        _mutes.Clear();
        _flagTimestamps.Clear();
    }

    /// <inheritdoc/>
    public Task RecordBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(_blocks, blockerIri, blockedIri);
        Add(_blockers, blockedIri, blockerIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = Remove(_blocks, blockerIri, blockedIri);
        // Keep the inverse index in lockstep regardless of the forward result (idempotent).
        Remove(_blockers, blockedIri, blockerIri);
        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlocksAsync(Iri blockerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_blocks, blockerIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<bool> IsBlockedAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Contains(_blocks, blockerIri, blockedIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlockersAsync(Iri blockedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_blockers, blockedIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task RecordFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(_flags, flaggerIri, flaggedIri);
        _flagTimestamps.TryAdd((flaggerIri, flaggedIri), DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _flagTimestamps.TryRemove((flaggerIri, flaggedIri), out _);
        return Task.FromResult(Remove(_flags, flaggerIri, flaggedIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFlagsAsync(Iri flaggerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_flags, flaggerIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<bool> HasFlaggedAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Contains(_flags, flaggerIri, flaggedIri));
    }

    /// <inheritdoc/>
    public Task RecordMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(_mutes, muterIri, mutedIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Remove(_mutes, muterIri, mutedIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetMutesAsync(Iri muterIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_mutes, muterIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<bool> IsMutedAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Contains(_mutes, muterIri, mutedIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FlagEdge>> GetAllFlagEdgesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var edges = new List<FlagEdge>();
        foreach (var (flagger, flaggedSet) in _flags)
        {
            lock (flaggedSet)
            {
                foreach (var flagged in flaggedSet)
                {
                    var ts = _flagTimestamps.TryGetValue((flagger, flagged), out var t) ? t : DateTimeOffset.UtcNow;
                    edges.Add(new FlagEdge(flagger, flagged, ts));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<FlagEdge>>(edges.OrderByDescending(e => e.CreatedAt).ToList());
    }

    private static void Add(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key, Iri value)
    {
        index.AddOrUpdate(
            key,
            _ => NewSet(value),
            (_, set) => { lock (set) { set.Add(value); } return set; });
    }

    private static bool Remove(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key, Iri value)
    {
        bool removed = false;
        if (index.TryGetValue(key, out var set))
        {
            lock (set) { removed = set.Remove(value); }
        }

        return removed;
    }

    private static bool Contains(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key, Iri value)
    {
        if (!index.TryGetValue(key, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Contains(value);
        }
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
            var filtered = filterSource && exists is not null
                ? set.Where(exists).ToHashSet()
                : set;

            // IRI-sorted for a deterministic collection order (the blocks collection is insertion-
            // unordered, so a stable sort makes the paged output stable across requests).
            return filtered.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
        }
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }
}
