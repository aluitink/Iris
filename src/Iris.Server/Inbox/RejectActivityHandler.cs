using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Reject"/> activities: when a remote actor rejects a follow that a
/// <em>local</em> actor made, the local follow edge is removed (undone) from the
/// <see cref="IFollowStore"/>.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="AcceptActivityHandler"/>: on the follower side a follow is provisional until
/// the followed side responds. A <c>Reject</c> delivered back to the follower's inbox removes the
/// <c>follower → target</c> edge — but only when the follower is a local actor. The target is resolved
/// from the original <c>Follow</c> (referenced by IRI in the Reject's object, fetched from the local
/// activity store). A missing target (the follow was never stored) is a no-op (there is no edge to
/// remove).
/// </remarks>
public sealed class RejectActivityHandler : FollowResponseActivityHandler<Reject>
{
    /// <summary>
    /// Initializes a new <see cref="RejectActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether an actor IRI is a local actor.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localActors"/> is null.</exception>
    public RejectActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
        : base(persistence, localActors)
    {
    }

    /// <inheritdoc/>
    protected override Task ApplyAsync(Iri followerIri, Iri targetIri, CancellationToken ct)
    {
        return Persistence.Follows.RemoveFollowAsync(followerIri, targetIri, ct);
    }
}
