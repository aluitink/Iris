using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records and queries like (endorsement) relationships.
/// </summary>
/// <remarks>
/// A like is the directed edge <c>liker → likedObject</c>. The store tracks both directions:
/// <list type="bullet">
/// <item>The <c>liker → [liked objects]</c> direction, so the actor's <c>liked</c> collection can be
/// served (ActivityPub's <c>Liked</c> relationship).</item>
/// <item>The <c>liked object → [likers]</c> reverse index, so an object's <c>likes</c> collection
/// (and thus its like count) can be assembled without scanning every stored activity — the
/// per-object interaction counter deferred by decision 056 (d).</item>
/// </list>
/// Both directions are maintained atomically on <see cref="RecordLikeAsync"/> /
/// <see cref="RemoveLikeAsync"/>.
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

    /// <summary>
    /// Returns the IRIs of the actors that have liked <paramref name="likedObjectIri"/> (the
    /// <c>likes</c> reverse index, the per-object like counter — decision 056 (d)).
    /// </summary>
    /// <param name="likedObjectIri">The IRI of the object whose likers are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the liker IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetLikersAsync(Iri likedObjectIri, CancellationToken ct = default);
}
