using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records and queries reply (thread) relationships between content objects (F-12).
/// </summary>
/// <remarks>
/// A reply is the directed edge <c>childObject → parentObject</c> (an object's <c>inReplyTo</c>). The
/// store tracks, for each parent object, the objects that reply to it, so a note's <c>replies</c>
/// collection (served at <c>{object}/replies</c>) can be assembled without scanning every stored object.
/// The <c>parent → [child]</c> direction is what a thread reader needs (the replies to a note); the
/// reverse (<c>child → parent</c>) is already carried by the stored object's <c>inReplyTo</c> and is read
/// via <see cref="IriExtensions.GetParentIri"/>.
/// </remarks>
public interface IReplyStore
{
    /// <summary>
    /// Records that <paramref name="childIri"/> replies to <paramref name="parentIri"/>.
    /// </summary>
    /// <param name="parentIri">The IRI of the object being replied to (the parent note).</param>
    /// <param name="childIri">The IRI of the reply (the child note).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a reply edge.
    /// </summary>
    /// <param name="parentIri">The IRI of the parent object.</param>
    /// <param name="childIri">The IRI of the child (reply) object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a reply edge was removed.</returns>
    public Task<bool> RemoveReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the objects that reply to <paramref name="parentIri"/>.
    /// </summary>
    /// <param name="parentIri">The IRI of the object whose replies are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the reply IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetRepliesAsync(Iri parentIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="childIri"/> replies to <paramref name="parentIri"/>.
    /// </summary>
    /// <param name="parentIri">The potential parent object IRI.</param>
    /// <param name="childIri">The potential reply (child) object IRI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the reply edge exists.</returns>
    public Task<bool> HasReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default);
}
