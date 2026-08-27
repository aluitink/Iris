using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IActorDocumentFetcher"/>, backed by an <see cref="IActivityPubClient"/>.
/// </summary>
/// <remarks>
/// The client is the outbound federation transport (Phase 2). Fetches are signed with the client's
/// configured identity (<see cref="ActivityPubServerOptions.InstanceActorId"/>). Fetch failures and
/// not-an-actor results return null (an expected condition), per the <see cref="IActorDocumentFetcher"/>
/// contract.
/// </remarks>
public sealed class IrisActorDocumentFetcher(IActivityPubClient client) : IActorDocumentFetcher
{
    private readonly IActivityPubClient _client = client!;

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        return await _client.GetActorAsync(actorIri, ct).ConfigureAwait(false);
    }
}
