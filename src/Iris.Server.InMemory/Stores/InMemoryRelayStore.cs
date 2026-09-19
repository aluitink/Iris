using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="IRelayStore"/> (F-06) backed by a concurrent dictionary: a forward index
/// (subscribing actor IRI → set of relay IRIs).
/// </summary>
/// <remarks>
/// Ephemeral: relay subscriptions vanish on restart. Thread-safe. The index is forward-only (an
/// actor's <c>relays</c> / <c>star</c> collection) — there is no inverse "which actors subscribe to
/// this relay" query in this slice, so a single forward index suffices.
/// </remarks>
public sealed class InMemoryRelayStore : IRelayStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _relays = new();
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
    /// Removes all relay subscriptions (test isolation / teardown).
    /// </summary>
    public void Clear() => _relays.Clear();

    /// <inheritdoc/>
    public Task RecordRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(_relays, actorIri, relayIri);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Remove(_relays, actorIri, relayIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetRelaysAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_relays, actorIri, filterSource: true));
    }

    /// <inheritdoc/>
    public Task<bool> IsRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Contains(_relays, actorIri, relayIri));
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

            // IRI-sorted for a deterministic collection order (the relays/star collection is
            // insertion-unordered, so a stable sort makes the paged output stable across requests).
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
