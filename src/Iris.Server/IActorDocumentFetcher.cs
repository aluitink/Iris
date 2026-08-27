using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Fetches an actor document by IRI. A seam so <see cref="RemoteInboundKeyResolver"/> (and the
/// Phase 4 delivery path) can retrieve remote actor documents without depending on a concrete
/// HTTP client.
/// </summary>
/// <remarks>
/// The default implementation (<see cref="IrisActorDocumentFetcher"/>) wraps the
/// <see cref="Iris.Client.IActivityPubClient"/>. It is registered by
/// <see cref="ActivityPubServerExtensions"/> via <c>AddActivityPubServer</c>.
/// </remarks>
public interface IActorDocumentFetcher
{
    /// <summary>
    /// Fetches the actor document for the given actor IRI.
    /// </summary>
    /// <param name="actorIri">The absolute IRI of the actor (e.g. <c>https://b.test/ap/v1/u/bob</c>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized <see cref="Actor"/>, or null when the fetch fails or the object is
    /// not an actor.</returns>
    /// <remarks>
    /// Fetch failures (404, network error, not-an-actor) are an expected condition — return null,
    /// do not throw.
    /// </remarks>
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default);
}
