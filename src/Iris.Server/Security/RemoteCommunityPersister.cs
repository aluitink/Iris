using Iris.Core;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Security;

/// <summary>
/// Persists remote community (Group) documents to the durable <see cref="ICommunityStore"/> on first
/// encounter, so the instance can serve the communities it has interacted with during federation as
/// known content (Phase 135.1 — Lemmy / community interop).
/// </summary>
/// <remarks>
/// <para>
/// A remote community is a <see cref="Group"/> actor whose IRI is on a different instance than this
/// server's. When such a community's document is fetched (via <see cref="IActorDocumentFetcher"/>,
/// which is used for inbound key resolution and other remote-actor reads), this persister stores it
/// in the durable community store if it is not already present. Local communities (whose IRI starts
/// with the instance base) are never persisted by this class — they are provisioned by the community
/// creation flow.
/// </para>
/// <para>
/// A <see cref="Group"/> cannot be stored in the <see cref="IActorStore"/> (it is not an
/// <see cref="Actor"/> in the ActivityStreams model), so it is persisted to the separate
/// <see cref="ICommunityStore"/>. Once stored, the community is served as known content by the
/// cached-actor-by-IRI endpoint (<c>GET /ap/v1/actor?iri=…</c>) — which reads the community store for
/// a Group IRI — and can be listed in the directory's community surface.
/// </para>
/// <para>
/// Persistence is best-effort: a failure to store (e.g. a transient DB error) is logged but does not
/// fail the fetch. A community is only persisted once (the <c>TryGetCommunityAsync</c> check makes
/// this idempotent).
/// </para>
/// </remarks>
public sealed class RemoteCommunityPersister
{
    private readonly ICommunityStore _communities;
    private readonly Iri? _instanceBase;
    private readonly ILogger<RemoteCommunityPersister> _logger;

    /// <summary>
    /// Initializes a new <see cref="RemoteCommunityPersister"/>.
    /// </summary>
    /// <param name="communities">The durable community store to persist remote communities into.</param>
    /// <param name="instanceBase">
    /// The instance base IRI (e.g. <c>https://iris.luit.ink/ap/v1</c>). Used to exclude local
    /// communities from persistence. May be null (all fetched communities are persisted).
    /// </param>
    /// <param name="logger">The logger. May be null (a no-op logger is used).</param>
    public RemoteCommunityPersister(
        ICommunityStore communities,
        Iri? instanceBase = null,
        ILogger<RemoteCommunityPersister>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(communities);
        _communities = communities;
        _instanceBase = instanceBase;
        _logger = logger ?? NullLogger<RemoteCommunityPersister>.Instance;
    }

    /// <summary>
    /// Persists <paramref name="community"/> to the durable store if it is a remote community not
    /// already stored. Local communities (IRI prefix matches <see cref="_instanceBase"/>) are skipped.
    /// </summary>
    /// <param name="community">The remote community (Group) document to persist.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when the community was newly persisted; <see langword="false"/>
    /// when it was already stored, is a local community, or has no IRI.</returns>
    public async Task<bool> PersistIfNewAsync(Group? community, CancellationToken ct = default)
    {
        if (community is null || community.Id is not { } idStr)
        {
            return false;
        }

        var iri = new Iri(idStr);

        // Skip local communities — they are provisioned by the community creation flow.
        if (_instanceBase is { } instanceBase)
        {
            var prefix = instanceBase.Value.TrimEnd('/');
            if (idStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var alreadyStored = await _communities.TryGetCommunityAsync(iri, out _, ct).ConfigureAwait(false);
        if (alreadyStored)
        {
            return false;
        }

        try
        {
            await _communities.PutCommunityAsync(community, ct).ConfigureAwait(false);
            _logger.LogInformation("Persisted remote community {Iri} to durable store", iri);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist remote community {Iri} to durable store", iri);
            return false;
        }
    }
}
