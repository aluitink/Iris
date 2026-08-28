using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Records and queries like (endorsement) relationships.
/// </summary>
/// <remarks>
/// A like is the directed edge <c>liker → likedObject</c>. The store tracks which objects a local
/// actor has liked so the actor's <c>liked</c> collection can be served (ActivityPub's
/// <c>Liked</c> relationship). Unlike follows, only the <c>liker → object</c> direction is needed
/// (the <c>liked</c> collection lists objects the actor liked, not the actors who liked an object).
/// </remarks>
public interface ILikeStore
{
    /// <summary>
    /// Records a like from <paramref name="likerIri"/> to <paramref name="likedObjectIri"/>.
    /// </summary>
    /// <param name="likerIri">The IRI of the actor who issued the like.</param>
    /// <param name="likedObjectIri">The IRI of the object being liked.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a like edge.
    /// </summary>
    /// <param name="likerIri">The IRI of the actor who issued the like.</param>
    /// <param name="likedObjectIri">The IRI of the object being liked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a like edge was removed.</returns>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of objects that <paramref name="likerIri"/> has liked.
    /// </summary>
    /// <param name="likerIri">The IRI of the actor whose liked collection is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the liked-object IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="likerIri"/> has liked <paramref name="likedObjectIri"/>.
    /// </summary>
    /// <param name="likerIri">The IRI of the potential liker.</param>
    /// <param name="likedObjectIri">The IRI of the potential object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the like edge exists.</returns>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default);
}
