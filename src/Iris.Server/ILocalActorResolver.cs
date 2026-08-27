using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Resolves whether a given actor IRI is a <em>local</em> actor (one hosted by this instance, in the
/// instance's <see cref="IActorStore"/>).
/// </summary>
/// <remarks>
/// Inbound activity handlers use this to decide how to interpret an activity. The
/// <see cref="FollowActivityHandler"/> records a follow edge only when the recipient is a local actor
/// (a remote recipient's follow is not this instance's concern); the
/// <see cref="AcceptActivityHandler"/> and <see cref="RejectActivityHandler"/> finalize or undo the
/// follow state only when the follower is a local actor (the local actor's own follow, which it sent
/// and now accepts or rejects). A remote follower's accept/reject is a no-op here: the remote
/// instance owns that follow's state.
/// </remarks>
public interface ILocalActorResolver
{
    /// <summary>
    /// Returns whether <paramref name="actorIri"/> is a local actor (present in the
    /// <see cref="IActorStore"/>).
    /// </summary>
    /// <param name="actorIri">The actor IRI to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the actor is local.</returns>
    public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default);
}
