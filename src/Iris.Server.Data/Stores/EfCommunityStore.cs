using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="ICommunityStore"/>. Community documents (the library's
/// <see cref="Group"/> actor type) live in the <c>Actors</c> table (a <c>Group</c> is an actor) and the
/// community's membership, join-request, follow/follower, and moderation edges live in the shared
/// <c>Edges</c> table (kinds <see cref="EdgeKind.CommunityMember"/> … <see cref="EdgeKind.CommunityMute"/>).
/// </summary>
public sealed class EfCommunityStore : ICommunityStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a context factory and a shared edge store.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfCommunityStore(IDbContextFactory<IrisDbContext> factory, EdgeStore edges)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _edges = edges ?? throw new ArgumentNullException(nameof(edges));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query under a short-lived context (mirrors the in-memory store).
    /// </remarks>
    public Task<bool> TryGetCommunityAsync(Iri communityIri, out Group? community, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        community = null;
        using var db = _factory.CreateDbContext();
        var entity = db.Set<ActorEntity>().AsNoTracking().FirstOrDefault(e => e.Id == communityIri.Value && e.Type == "Group");
        if (entity is null)
        {
            return Task.FromResult(false);
        }

        community = AsDocument.Deserialize(entity.Document) as Group;
        return Task.FromResult(community is not null);
    }

    /// <inheritdoc/>
    public async Task PutCommunityAsync(Group community, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (string.IsNullOrWhiteSpace(community.Id))
        {
            throw new ArgumentException("Community must have a non-null Id.", nameof(community));
        }

        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var iri = community.Id;
        var existing = await db.Set<ActorEntity>().FirstOrDefaultAsync(e => e.Id == iri, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Set<ActorEntity>().Add(new ActorEntity
            {
                Id = iri,
                Handle = community.PreferredUsername,
                Type = "Group",
                CreatedAt = DateTimeOffset.UtcNow,
                Document = AsDocument.Serialize(community),
            });
        }
        else
        {
            existing.Handle = community.PreferredUsername;
            existing.Type = "Group";
            existing.Document = AsDocument.Serialize(community);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<bool> AddMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityMember, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityMember, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> IsMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.CommunityMember, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetMembersAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityMember, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<bool> AddJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityJoinRequest, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityJoinRequest, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> HasJoinRequestAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.CommunityJoinRequest, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetJoinRequestsAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityJoinRequest, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetFollowsAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityFollow, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<bool> AddFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityFollow, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityFollow, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetFollowersAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.InSourcesAsync(EdgeKind.CommunityFollower, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<bool> AddFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityFollower, actorIri.Value, communityIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityFollower, actorIri.Value, communityIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetAllCommunityIrisAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await db.Set<ActorEntity>().Where(e => e.Type == "Group").Select(e => e.Id).ToListAsync(ct).ConfigureAwait(false);
        return ids.Select(id => new Iri(id)).ToList();
    }

    /// <inheritdoc/>
    public Task<bool> AddBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityBlock, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityBlock, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetBlocksAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityBlock, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<bool> AddFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityFlag, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityFlag, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetFlagsAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityFlag, communityIri.Value, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<bool> AddMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.AddIfNewAsync(EdgeKind.CommunityMute, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.CommunityMute, communityIri.Value, actorIri.Value, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Iri>> GetMutesAsync(Iri communityIri, CancellationToken ct = default)
        => await _edges.OutTargetsAsync(EdgeKind.CommunityMute, communityIri.Value, ct).ConfigureAwait(false);
}
