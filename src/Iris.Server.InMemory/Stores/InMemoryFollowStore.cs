using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

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
    // Pending follow requests (Phase 100): target actor → the ordered list of requester IRIs (newest
    // appended last, so the queue is served newest-first by reversing on read). A list (not a set) is
    // used so insertion order is preserved for the newest-first ordering the queue surface wants.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Generic.List<Iri>> _followRequests = new();

    /// <summary>
    /// Removes all follow edges and reverse indices (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _followers.Clear();
        _following.Clear();
        _followRequests.Clear();
    }

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

    /// <inheritdoc/>
    public Task RecordFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _followRequests.AddOrUpdate(targetIri, _ => NewList(followerIri), (_, list) => { lock (list) { if (!list.Contains(followerIri)) { list.Add(followerIri); } } return list; });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool removed = false;
        if (_followRequests.TryGetValue(targetIri, out var list))
        {
            lock (list) { removed = list.Remove(followerIri); }
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<bool> HasFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_followRequests.TryGetValue(targetIri, out var list) && InList(list, followerIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowRequestsAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_followRequests.TryGetValue(actorIri, out var list))
        {
            return Task.FromResult<IReadOnlyList<Iri>>([]);
        }

        // Serve newest-first: the list is oldest→newest, so reverse the snapshot.
        lock (list)
        {
            var snapshot = list.ToList();
            snapshot.Reverse();
            return Task.FromResult<IReadOnlyList<Iri>>(snapshot);
        }
    }

    private static System.Collections.Generic.List<Iri> NewList(Iri iri)
    {
        var list = new System.Collections.Generic.List<Iri>();
        lock (list) { list.Add(iri); }
        return list;
    }

    private static bool InList(System.Collections.Generic.List<Iri> list, Iri iri)
    {
        lock (list)
        {
            return list.Contains(iri);
        }
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
