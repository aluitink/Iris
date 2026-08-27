using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="IFollowStore"/> backed by two concurrent dictionaries (followers and
/// following, each actor IRI → set of IRIs).
/// </summary>
/// <remarks>
/// Ephemeral: follows vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryFollowStore : IFollowStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _followers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> _following = new();

    /// <inheritdoc/>
    public Task RecordFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _followers.AddOrUpdate(targetIri, _ => NewSet(followerIri), (_, set) => { lock (set) { set.Add(followerIri); } return set; });
        _following.AddOrUpdate(followerIri, _ => NewSet(targetIri), (_, set) => { lock (set) { set.Add(targetIri); } return set; });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        if (_followers.TryGetValue(targetIri, out var followers))
        {
            lock (followers) { removed = followers.Remove(followerIri); }
        }

        if (_following.TryGetValue(followerIri, out var following))
        {
            lock (following) { following.Remove(targetIri); }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowersAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_followers, actorIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowingAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Iri>>(Snapshot(_following, actorIri));
    }

    /// <inheritdoc/>
    public Task<bool> IsFollowingAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_following.TryGetValue(followerIri, out var set) && IsIn(set, targetIri));
    }

    private static HashSet<Iri> NewSet(Iri iri)
    {
        var set = new HashSet<Iri>();
        lock (set) { set.Add(iri); }
        return set;
    }

    private static IReadOnlyList<Iri> Snapshot(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, HashSet<Iri>> map,
        Iri key)
    {
        if (!map.TryGetValue(key, out var set))
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
