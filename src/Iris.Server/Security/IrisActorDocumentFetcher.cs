using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Security;

/// <summary>
/// The default <see cref="IActorDocumentFetcher"/>, backed by an <see cref="IActivityPubClient"/>.
/// </summary>
/// <remarks>
/// The client is the outbound federation transport (Phase 2). Fetches are signed with the client's
/// configured identity (<see cref="ActivityPubServerOptions.InstanceActorId"/>). Fetch failures and
/// not-an-actor results return null (an expected condition), per the <see cref="IActorDocumentFetcher"/>
/// contract.
/// <para>
/// Reads go through the Phase 3 <see cref="RemoteActorCache"/> (by actor IRI), so a remote actor's
/// document is fetched once and reused across key resolutions and deliveries within the cache's TTL.
/// An absent result (the client returned null) is not cached, so a later lookup retries.
/// </para>
/// <para>
/// When a <see cref="RemoteActorPersister"/> is provided, newly fetched remote actors are also
/// persisted to the durable actor store (117.3 — directory "All known" scope), so the directory
/// can list actors the instance has encountered during federation.
/// </para>
/// </remarks>
public sealed class IrisActorDocumentFetcher(IActivityPubClient client, RemoteActorCache remoteActors, RemoteActorPersister? persister = null)
    : IActorDocumentFetcher
{
    private readonly IActivityPubClient _client = client!;
    private readonly RemoteActorCache _remoteActors = remoteActors!;
    private readonly RemoteActorPersister? _persister = persister;

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        var (value, _, _) = await _remoteActors
            .GetAsync(
                actorIri,
                bypassCache: false,
                factory: iri => FetchDocumentAsync(iri, ct),
                ct)
            .ConfigureAwait(false);

        var actor = value as Actor;

        if (actor is not null && _persister is not null)
        {
            await _persister.PersistIfNewAsync(actor, ct).ConfigureAwait(false);
        }

        return actor;
    }

    private async Task<IObject?> FetchDocumentAsync(Iri iri, CancellationToken ct)
    {
        var actor = await _client.GetActorAsync(iri, ct).ConfigureAwait(false);
        return actor;
    }
}
