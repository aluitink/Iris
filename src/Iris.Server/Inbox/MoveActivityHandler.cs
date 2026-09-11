using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

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
/// <strong>Key re-resolution (F-25).</strong> The handler clears the moving actor's entries from the
/// outbound <c>RemoteKeyCache</c> and <c>RemoteActorCache</c> (when provided) so the next key resolution
/// fetches the new actor document (with the new key) rather than serving the stale cached one. The key
/// IRI is read from the cached actor document's <c>publicKey.id</c> (via
/// <see cref="IriExtensions.GetPublicKeyIri"/>) rather than hard-coded to <c>#key-1</c>, so a non-standard
/// key fragment is invalidated correctly. The handler then fetches the new actor document to warm the
/// <c>RemoteActorCache</c>, so subsequent reads of the new IRI resolve immediately. A fetch failure is
/// non-fatal (the cache entry is absent and will be populated on the next resolution attempt).
/// </para>
public sealed class MoveActivityHandler : ActivityHandlerBase<Move>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IReadOnlyCollection<Iri> _localCommunities;
    private readonly RemoteKeyCache? _remoteKeys;
    private readonly RemoteActorCache? _remoteActors;
    private readonly IActorDocumentFetcher? _actorDocuments;

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
    /// next fetch retrieves the new actor document; warmed with the new actor's document after the move).
    /// May be <see langword="null"/>.</param>
    /// <param name="actorDocuments">The actor document fetcher (used to warm the new actor's document into
    /// the <c>RemoteActorCache</c> after the move). May be <see langword="null"/> (no warming).</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localCommunities"/> is null.</exception>
    public MoveActivityHandler(
        IPersistenceProvider persistence,
        IReadOnlyCollection<Iri> localCommunities,
        RemoteKeyCache? remoteKeys = null,
        RemoteActorCache? remoteActors = null,
        IActorDocumentFetcher? actorDocuments = null,
        ILogger<MoveActivityHandler>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localCommunities);
        _persistence = persistence;
        _localCommunities = localCommunities;
        _remoteKeys = remoteKeys;
        _remoteActors = remoteActors;
        _actorDocuments = actorDocuments;
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
        // key (F-25). The key IRI is read from the cached actor document's publicKey.id (via
        // IriExtensions.GetPublicKeyIri) rather than hard-coded to #key-1, so a non-standard key fragment
        // is invalidated correctly.
        // Resolve the moving actor's key IRI from the cached actor document BEFORE invalidating it — the
        // doc's publicKey.id is the real key IRI (a non-standard fragment such as #main-key, not the
        // #key-1 convention). Reading it after the actor-doc invalidation would find no doc and fall back
        // to #key-1, which only works by fragment-blind coincidence for a non-standard fragment.
        var keyIri = ResolveOldKeyIri(oldIri.Value);
        _remoteActors?.Invalidate(oldIri.Value);
        if (keyIri.HasValue)
        {
            _remoteKeys?.Invalidate(keyIri.Value);
        }

        // Warm the new actor's document into the RemoteActorCache so subsequent reads resolve immediately
        // (F-25 re-resolution). A fetch failure is non-fatal: the cache entry stays absent and the next
        // key resolution will re-fetch.
        if (_remoteActors is not null && _actorDocuments is not null)
        {
            try
            {
                await _remoteActors
                    .GetAsync(newIri.Value, bypassCache: false, factory: async iri => await _actorDocuments.GetActorAsync(iri, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A fetch failure (404, network error, not-an-actor) leaves the cache entry absent;
                // the next resolution attempt will re-fetch.
            }
        }
    }

    /// <summary>
    /// Resolves the moving actor's key IRI: reads the cached actor document's <c>publicKey.id</c> (via
    /// <see cref="IriExtensions.GetPublicKeyIri"/>) when available, falling back to the ActivityPub
    /// convention <c>actorIri#key-1</c>.
    /// </summary>
    private Iri? ResolveOldKeyIri(Iri oldActorIri)
    {
        if (_remoteActors is not null)
        {
            // Read the cached actor doc (bypassCache: false) without forcing a fetch: the factory
            // returns null, so a cache hit yields the doc (whose publicKey.id is the real key IRI) and
            // a miss yields null (fall back to the #key-1 convention). bypassCache: true would skip the
            // cache read entirely and always fall back — never invalidating a non-standard key fragment.
            var (cached, _, _) = _remoteActors
                .GetAsync(oldActorIri, bypassCache: false, factory: _ => Task.FromResult<IObject?>(null))
                .GetAwaiter()
                .GetResult();
            var fromDoc = cached?.GetPublicKeyIri();
            if (fromDoc is not null)
            {
                return fromDoc;
            }
        }

        return Iri.TryParse($"{oldActorIri}#key-1", out var fallback) ? fallback : null;
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
