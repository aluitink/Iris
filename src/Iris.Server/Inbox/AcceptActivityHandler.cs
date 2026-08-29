using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Accept"/> activities: when a remote actor accepts a follow that a
/// <em>local</em> actor made, the local follow edge is finalized (recorded in the
/// <see cref="IFollowStore"/>).
/// </summary>
/// <remarks>
/// On the follower side, a follow is <em>provisional</em> until the followed side accepts it. The
/// <see cref="FollowActivityHandler"/> (on the followed side) schedules the <c>Accept</c>; when that
/// <c>Accept</c> is delivered back to the follower's inbox (this instance), this handler finalizes the
/// follow by recording the <c>follower → target</c> edge — but only when the follower is a local actor
/// (the local actor's own follow). The followed side's acceptance of a <em>remote</em> follower's
/// follow is owned by the remote instance, so a remote follower's <c>Accept</c> is a no-op here.
/// The <c>Accept</c>'s object references the original <c>Follow</c> (by IRI); the target is resolved
/// from that follow (fetched from the local activity store — the follower stored it when it sent the
/// follow). A missing target (the follow was never stored) is a no-op.
/// </remarks>
public sealed class AcceptActivityHandler : FollowResponseActivityHandler<Accept>
{
    /// <summary>
    /// Initializes a new <see cref="AcceptActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether an actor IRI is a local actor.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localActors"/> is null.</exception>
    public AcceptActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
        : base(persistence, localActors)
    {
    }

    /// <inheritdoc/>
    protected override Task ApplyAsync(Iri followerIri, Iri targetIri, CancellationToken ct)
    {
        return Persistence.Follows.RecordFollowAsync(followerIri, targetIri, ct);
    }
}
