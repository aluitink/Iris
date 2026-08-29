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
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_relays, actorIri));
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

    private static IReadOnlyList<Iri> Snapshot(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key)
    {
        if (!index.TryGetValue(key, out var set))
        {
            return [];
        }

        lock (set)
        {
            // IRI-sorted for a deterministic collection order (the relays/star collection is
            // insertion-unordered, so a stable sort makes the paged output stable across requests).
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
