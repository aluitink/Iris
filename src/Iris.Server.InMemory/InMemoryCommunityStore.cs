using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory;

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
}
