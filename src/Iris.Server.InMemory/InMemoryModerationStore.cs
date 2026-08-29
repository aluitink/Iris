using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="IModerationStore"/> (F-07) backed by two concurrent dictionaries: the
/// forward index (blocker IRI → set of blocked IRIs) and the inverse index (blocked IRI → set of
/// blocker IRIs).
/// </summary>
/// <remarks>
/// Ephemeral: block edges vanish on restart. Thread-safe. Both indexes are kept in lockstep
/// (a record removes nothing from the other; a remove clears both), so the forward
/// (<see cref="IModerationStore.GetBlocksAsync"/>) and inverse
/// (<see cref="IModerationStore.GetBlockersAsync"/>) queries are both O(1) lookups.
/// </remarks>
public sealed class InMemoryModerationStore : IModerationStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _blocks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _blockers = new();

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
