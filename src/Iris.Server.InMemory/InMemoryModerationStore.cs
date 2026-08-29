using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory;

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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_blocks, blockerIri));
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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_blockers, blockedIri));
    }

    /// <inheritdoc/>
    public Task RecordFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(_flags, flaggerIri, flaggedIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Remove(_flags, flaggerIri, flaggedIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFlagsAsync(Iri flaggerIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_flags, flaggerIri));
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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_mutes, muterIri));
    }

    /// <inheritdoc/>
    public Task<bool> IsMutedAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Contains(_mutes, muterIri, mutedIri));
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

    private static IReadOnlyList<Iri> Snapshot(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key)
    {
        if (!index.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            // IRI-sorted for a deterministic collection order (the blocks collection is insertion-
            // unordered, so a stable sort makes the paged output stable across requests).
            return set.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
        }
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }
}
