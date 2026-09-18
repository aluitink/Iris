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
public sealed class IrisActorDocumentFetcher(
    IActivityPubClient client,
    RemoteActorCache remoteActors,
    RemoteActorPersister? persister = null,
    RemoteCommunityPersister? communityPersister = null)
    : IActorDocumentFetcher
{
    private readonly IActivityPubClient _client = client!;
    private readonly RemoteActorCache _remoteActors = remoteActors!;
    private readonly RemoteActorPersister? _persister = persister;
    private readonly RemoteCommunityPersister? _communityPersister = communityPersister;

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

        // The fetched document is the full ActivityStreams object (an actor of any kind — Person,
        // Organization, or a community Group — or null when the IRI is not an actor). Check for a
        // community Group FIRST (a Group is also an Actor in the ActivityStreams model, so the more
        // specific Group check must come before the general Actor check): a community is persisted to
        // the durable community store (135.1 — Lemmy / community interop, so the communities the
        // instance has interacted with are served as known content). It is then RETURNED as an Actor
        // (a Group IS an Actor) so the inbound key resolver can read its publicKey and validate the
        // community's signatures — returning null here would break signature validation for
        // community-signed activities. A plain (non-Group) actor is persisted to the actor store and
        // returned as before.
        if (value is Group group)
        {
            if (_communityPersister is not null)
            {
                await _communityPersister.PersistIfNewAsync(group, ct).ConfigureAwait(false);
            }

            return group;
        }

        if (value is Actor actor)
        {
            if (_persister is not null)
            {
                await _persister.PersistIfNewAsync(actor, ct).ConfigureAwait(false);
            }

            return actor;
        }

        return null;
    }

    private async Task<IObject?> FetchDocumentAsync(Iri iri, CancellationToken ct)
    {
        // Fetch the full object (not just an Actor) so a community Group document is not dropped —
        // a remote Lemmy community's document is a Group, and we want to persist it as known content.
        return await _client.GetObjectAsync(iri, ct).ConfigureAwait(false);
    }
}
