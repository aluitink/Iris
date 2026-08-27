using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Records and queries follow relationships between actors.
/// </summary>
/// <remarks>
/// A follow is the directed edge <c>follower → target</c>. The store tracks both directions so the
/// followers/following collections (Phase 4/5) can be served. Phase 3 only needs the primitives to
/// record and look up a single follow; the full accept/reject lifecycle is Phase 4.
/// </remarks>
public interface IFollowStore
{
    /// <summary>
    /// Records a follow from <paramref name="followerIri"/> to <paramref name="targetIri"/>.
    /// </summary>
    /// <param name="followerIri">The IRI of the actor who initiated the follow.</param>
    /// <param name="targetIri">The IRI of the actor being followed.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a follow edge.
    /// </summary>
    /// <param name="followerIri">The IRI of the actor who initiated the follow.</param>
    /// <param name="targetIri">The IRI of the actor being followed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a follow edge was removed.</returns>
    public Task<bool> RemoveFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of actors following <paramref name="actorIri"/>.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose followers are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the follower IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetFollowersAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of actors that <paramref name="actorIri"/> follows.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose following list is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the followed IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetFollowingAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="followerIri"/> currently follows <paramref name="targetIri"/>.
    /// </summary>
    /// <param name="followerIri">The IRI of the potential follower.</param>
    /// <param name="targetIri">The IRI of the potential target.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the follow edge exists.</returns>
    public Task<bool> IsFollowingAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);
}
