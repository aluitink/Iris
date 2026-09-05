using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="ICommunityStore"/> backed by concurrent dictionaries.
/// </summary>
/// <remarks>
/// Ephemeral: communities and memberships vanish on restart. Thread-safe. Community membership is
/// keyed by community IRI and holds the set of local actor IRIs that are members.
/// </remarks>
public sealed class InMemoryCommunityStore : ICommunityStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, Group> _communities = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _members = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _follows = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _followers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _blocks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _flags = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _mutes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> _joinRequests = new();

    /// <summary>
    /// Removes all communities, members, follows, followers, moderation edges, and join requests
    /// (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        _communities.Clear();
        _members.Clear();
        _follows.Clear();
        _followers.Clear();
        _blocks.Clear();
        _flags.Clear();
        _mutes.Clear();
        _joinRequests.Clear();
    }

    /// <inheritdoc/>
    public Task<bool> TryGetCommunityAsync(Iri communityIri, out Group? community, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _communities.TryGetValue(communityIri, out community);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutCommunityAsync(Group community, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (string.IsNullOrWhiteSpace(community.Id))
        {
            throw new ArgumentException("Community must have a non-null Id.", nameof(community));
        }

        ct.ThrowIfCancellationRequested();
        _communities[new Iri(community.Id)] = community;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> AddMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var added = _members.GetOrAdd(communityIri, _ => new System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>()).TryAdd(actorIri, 0);
        return Task.FromResult(added);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_members.TryGetValue(communityIri, out var members))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(members.TryRemove(actorIri, out _));
    }

    /// <inheritdoc/>
    public Task<bool> IsMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_members.TryGetValue(communityIri, out var members) && members.ContainsKey(actorIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetMembersAsync(Iri communityIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Iri>();
        if (_members.TryGetValue(communityIri, out var members))
        {
            foreach (var member in members.Keys)
            {
                result.Add(member);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Iri>>(result);
    }

    // --- Pending join requests (19.5.2) ---

    /// <inheritdoc/>
    public Task<bool> AddJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => AddToSetAsync(_joinRequests, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => RemoveFromSetAsync(_joinRequests, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<bool> HasJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_joinRequests.TryGetValue(communityIri, out var set) && set.ContainsKey(actorIri));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetJoinRequestsAsync(Iri communityIri, CancellationToken ct = default)
        => GetSetAsync(_joinRequests, communityIri, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetFollowsAsync(Iri communityIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Iri>();
        if (_follows.TryGetValue(communityIri, out var follows))
        {
            foreach (var followed in follows.Keys)
            {
                result.Add(followed);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Iri>>(result);
    }

    /// <inheritdoc/>
    public Task<bool> AddFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var added = _follows.GetOrAdd(communityIri, _ => new System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>()).TryAdd(actorIri, 0);
        return Task.FromResult(added);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_follows.TryGetValue(communityIri, out var follows))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(follows.TryRemove(actorIri, out _));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetFollowersAsync(Iri communityIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Iri>();
        if (_followers.TryGetValue(communityIri, out var followers))
        {
            foreach (var follower in followers.Keys)
            {
                result.Add(follower);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Iri>>(result);
    }

    /// <inheritdoc/>
    public Task<bool> AddFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var added = _followers.GetOrAdd(communityIri, _ => new System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>()).TryAdd(actorIri, 0);
        return Task.FromResult(added);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_followers.TryGetValue(communityIri, out var followers))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(followers.TryRemove(actorIri, out _));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetAllCommunityIrisAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Iri>(_communities.Keys);
        return Task.FromResult<IReadOnlyCollection<Iri>>(result);
    }

    // --- Community moderation (19.5.4) ---

    /// <inheritdoc/>
    public Task<bool> AddBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => AddToSetAsync(_blocks, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => RemoveFromSetAsync(_blocks, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetBlocksAsync(Iri communityIri, CancellationToken ct = default)
        => GetSetAsync(_blocks, communityIri, ct);

    /// <inheritdoc/>
    public Task<bool> AddFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => AddToSetAsync(_flags, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => RemoveFromSetAsync(_flags, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetFlagsAsync(Iri communityIri, CancellationToken ct = default)
        => GetSetAsync(_flags, communityIri, ct);

    /// <inheritdoc/>
    public Task<bool> AddMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => AddToSetAsync(_mutes, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => RemoveFromSetAsync(_mutes, communityIri, actorIri, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetMutesAsync(Iri communityIri, CancellationToken ct = default)
        => GetSetAsync(_mutes, communityIri, ct);

    private static Task<bool> AddToSetAsync(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> index,
        Iri communityIri, Iri actorIri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var added = index.GetOrAdd(communityIri, _ => new System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>()).TryAdd(actorIri, 0);
        return Task.FromResult(added);
    }

    private static Task<bool> RemoveFromSetAsync(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> index,
        Iri communityIri, Iri actorIri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!index.TryGetValue(communityIri, out var set))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(set.TryRemove(actorIri, out _));
    }

    private static Task<IReadOnlyCollection<Iri>> GetSetAsync(
        System.Collections.Concurrent.ConcurrentDictionary<Iri, System.Collections.Concurrent.ConcurrentDictionary<Iri, byte>> index,
        Iri communityIri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Iri>();
        if (index.TryGetValue(communityIri, out var set))
        {
            foreach (var actor in set.Keys)
            {
                result.Add(actor);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Iri>>(result);
    }
}
