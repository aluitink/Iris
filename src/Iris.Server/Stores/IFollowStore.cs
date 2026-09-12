using Iris.Core;

namespace Iris.Server.Stores;

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

    /// <summary>
    /// Records a pending follow request from <paramref name="followerIri"/> to <paramref name="targetIri"/>
    /// (Phase 100). This is the follow-approval queue edge: it exists only while <paramref name="targetIri"/>
    /// has <c>manuallyApprovesFollowers</c> set and an inbound <c>Follow</c> is held (not auto-accepted). It
    /// is independent of the <c>Follow</c> edge (which is always recorded when the recipient is local).
    /// </summary>
    /// <param name="followerIri">The IRI of the actor requesting to follow.</param>
    /// <param name="targetIri">The IRI of the (local) actor whose approval is required.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a pending follow request edge (Phase 100) when the operator Accepts or Rejects the request.
    /// </summary>
    /// <param name="followerIri">The IRI of the actor who requested to follow.</param>
    /// <param name="targetIri">The IRI of the (local) actor whose approval was pending.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a pending request edge was removed.</returns>
    public Task<bool> RemoveFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether a pending follow request from <paramref name="followerIri"/> to
    /// <paramref name="targetIri"/> is currently queued (Phase 100).
    /// </summary>
    /// <param name="followerIri">The IRI of the potential requester.</param>
    /// <param name="targetIri">The IRI of the (local) actor whose approval is pending.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the pending request edge exists.</returns>
    public Task<bool> HasFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of actors with a pending follow request to <paramref name="actorIri"/> (Phase 100)
    /// — the actor's follow-approval queue, newest-first.
    /// </summary>
    /// <param name="actorIri">The IRI of the (local) actor whose pending follow requests are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the requester IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetFollowRequestsAsync(Iri actorIri, CancellationToken ct = default);
}
