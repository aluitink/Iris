using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Move"/> activities: when an actor migrates to a new IRI (a <c>Move</c> whose
/// <c>actor</c> is the old IRI and <c>object</c> is the new IRI), the local follow edges that pointed at the
/// old IRI are re-pointed at the new IRI so the instance keeps following the moved actor (F-08).
/// </summary>
/// <remarks>
/// Per ActivityPub §5.2.1.6, a <c>Move</c> is delivered to the moving actor's <em>followers</em> (their
/// inboxes) so each follower can update its record of where the actor now lives. The handler therefore
/// does <em>not</em> gate on <see cref="InboxDelivery.RecipientIri"/> (the recipient is a follower, which may
/// be local or remote) — instead it re-points every <em>local</em> edge that targets the moving actor.
/// </remarks>
/// <para>
/// <strong>Person edges.</strong> Each local actor in the moving actor's follower set
/// (<see cref="IFollowStore.GetFollowersAsync"/>) has its <c>localFollower → oldIri</c> edge removed and
/// re-recorded as <c>localFollower → newIri</c> (<see cref="IFollowStore"/>). The moving actor's own
/// following set is not re-pointed here (the moved actor's home instance owns that state).
/// </para>
/// <para>
/// <strong>Community edges.</strong> A local community's follows set is not indexed by target (the
/// <see cref="ICommunityStore"/> exposes no "communities following X" query), so the handler is constructed
/// with the set of local community IRIs (from the <see cref="ICommunityStore"/> member enumeration) and
/// checks each community's follows set for the old IRI, re-pointing it to the new IRI when present.
/// </para>
/// <para>
/// <strong>Key re-resolution (F-25).</strong> The handler also clears the moving actor's entries from the
/// outbound <c>RemoteKeyCache</c> and <c>RemoteActorCache</c> (when provided) so the next key resolution
/// fetches the new actor document (with the new key) rather than serving the stale cached one. The old
/// actor document may still be served until the cache TTL expires (or a <c>?refresh=true</c> bypass), which
/// is the documented scope limit of this slice.
/// </para>
public sealed class MoveActivityHandler : ActivityHandlerBase<Move>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IReadOnlyCollection<Iri> _localCommunities;
    private readonly RemoteKeyCache? _remoteKeys;
    private readonly RemoteActorCache? _remoteActors;

    /// <summary>
    /// Initializes a new <see cref="MoveActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="ICommunityStore"/>).</param>
    /// <param name="localCommunities">The IRIs of the local communities (communities this instance hosts).
    /// The handler checks each community's follows set for an edge to the moving actor.</param>
    /// <param name="remoteKeys">The outbound remote-key cache (invalidated for the moving actor's key so the
    /// next resolution fetches the new key). May be <see langword="null"/> (no cache to clear).</param>
    /// <param name="remoteActors">The outbound remote-actor cache (invalidated for the moving actor so the
    /// next fetch retrieves the new actor document). May be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localCommunities"/> is null.</exception>
    public MoveActivityHandler(
        IPersistenceProvider persistence,
        IReadOnlyCollection<Iri> localCommunities,
        RemoteKeyCache? remoteKeys = null,
        RemoteActorCache? remoteActors = null)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localCommunities);
        _persistence = persistence;
        _localCommunities = localCommunities;
        _remoteKeys = remoteKeys;
        _remoteActors = remoteActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Move move, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(move);

        // The moving actor (the old IRI) is the activity's actor; the new IRI is the activity's object.
        var oldIri = move.Actor?.FirstOrDefault()?.ResolveObjectIri();
        var newIri = move.Object?.FirstOrDefault()?.ResolveObjectIri();
        if (!oldIri.HasValue || !newIri.HasValue)
        {
            // A Move with no resolvable actor or object is malformed; nothing to re-point.
            return;
        }

        // Re-point the local person-follow edges that target the moving actor.
        await RePointPersonFollowersAsync(oldIri.Value, newIri.Value, ct).ConfigureAwait(false);

        // Re-point the local community follows-edges that target the moving actor.
        await RePointCommunityFollowsAsync(oldIri.Value, newIri.Value, ct).ConfigureAwait(false);

        // Invalidate the moving actor's outbound cache entries so the next key resolution fetches the new
        // key (F-25). A no-op when the caches are not provided. The actor-document cache is keyed by the
        // actor IRI; the key cache is keyed by the actor's publicKey IRI (the actor IRI + the
        // <c>#key-1</c> fragment, the ActivityPub convention), so the key is invalidated by that IRI.
        _remoteActors?.Invalidate(oldIri.Value);
        if (Iri.TryParse($"{oldIri.Value}#key-1", out var keyIri))
        {
            _remoteKeys?.Invalidate(keyIri);
        }
    }

    /// <summary>
    /// Re-points each local person-follow edge that targets <paramref name="oldIri"/> to
    /// <paramref name="newIri"/>.
    /// </summary>
    private async Task RePointPersonFollowersAsync(Iri oldIri, Iri newIri, CancellationToken ct)
    {
        var followers = await _persistence.Follows.GetFollowersAsync(oldIri, ct).ConfigureAwait(false);
        foreach (var follower in followers)
        {
            // Only local actors' edges are this instance's to re-point; a remote follower's instance owns
            // that follower's follow state.
            if (!await _persistence.Actors.TryGetActorAsync(follower, out _, ct).ConfigureAwait(false))
            {
                continue;
            }

            await _persistence.Follows
                .RemoveFollowAsync(follower, oldIri, ct)
                .ConfigureAwait(false);
            await _persistence.Follows
                .RecordFollowAsync(follower, newIri, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-points each local community follows-edge that targets <paramref name="oldIri"/> to
    /// <paramref name="newIri"/>.
    /// </summary>
    private async Task RePointCommunityFollowsAsync(Iri oldIri, Iri newIri, CancellationToken ct)
    {
        foreach (var community in _localCommunities)
        {
            var follows = await _persistence.Communities.GetFollowsAsync(community, ct).ConfigureAwait(false);
            if (!follows.Contains(oldIri))
            {
                continue;
            }

            await _persistence.Communities
                .RemoveFollowAsync(community, oldIri, ct)
                .ConfigureAwait(false);
            await _persistence.Communities
                .AddFollowAsync(community, newIri, ct)
                .ConfigureAwait(false);
        }
    }
}
