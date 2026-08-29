using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Stores;

/// <summary>
/// Reads and writes <see cref="Actor"/> documents by their IRI.
/// </summary>
/// <remarks>
/// The server stores actors as the library's <see cref="Actor"/> type (deserialized per the
/// 3rd-Party ActivityStreams rules). The <c>privateKey</c> extension (owner-only PEM) is carried
/// in the actor's <c>ExtensionData</c> and is only ever returned by the authenticated path.
/// </remarks>
public interface IActorStore
{
    /// <summary>
    /// Attempts to retrieve the actor for the given IRI.
    /// </summary>
    /// <param name="actorIri">The IRI identifying the actor.</param>
    /// <param name="actor">When successful, the actor; otherwise null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if the actor was found; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) the actor under its IRI. The actor's <c>Id</c> must already be set.
    /// </summary>
    /// <param name="actor">The actor to store. Must not be null and must have a non-null <c>Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="actor"/> is null.</exception>
    /// <exception cref="ArgumentException">When the actor has no <c>Id</c>.</exception>
    public Task PutActorAsync(Actor actor, CancellationToken ct = default);

    /// <summary>
    /// Removes the actor for the given IRI.
    /// </summary>
    /// <param name="actorIri">The IRI identifying the actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when an actor was removed.</returns>
    public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Lists every actor this instance stores (the local actors + communities).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the stored actors (possibly empty). The order is
    /// unspecified; callers that need a stable order sort the result (e.g. by IRI).</returns>
    public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default);
}
