using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Accept"/> activities: when a remote actor accepts a follow that a
/// <em>local</em> actor (a person or a community) made, the local follow edge is finalized — recorded in
/// the <see cref="IFollowStore"/> for a person follower, or in the community's follows set
/// (<see cref="ICommunityStore.AddFollowAsync"/>) for a community follower.
/// </summary>
/// <remarks>
/// On the follower side, a follow is <em>provisional</em> until the followed side accepts it. The
/// <see cref="FollowActivityHandler"/> (on the followed side) schedules the <c>Accept</c>; when that
/// <c>Accept</c> is delivered back to the follower's inbox (this instance), this handler finalizes the
/// follow by recording the <c>follower → target</c> edge — but only when the follower is local (a person
/// in the actor store or a community in the community store; the local actor's own follow). The followed
/// side's acceptance of a <em>remote</em> follower's follow is owned by the remote instance, so a remote
/// follower's <c>Accept</c> is a no-op here.
/// The <c>Accept</c>'s object references the original <c>Follow</c> (by IRI); the target is resolved
/// from that follow (fetched from the local activity store — the follower stored it when it sent the
/// follow). A missing target (the follow was never stored) is a no-op.
/// </remarks>
/// <remarks>
/// <strong>Community follower (G-3).</strong> A community is a <see cref="Group"/> actor, not a person in
/// the actor store, so <see cref="ILocalActorResolver.IsLocalActorAsync(Iri, CancellationToken)"/> does
/// not see it as local (the base guard would no-op). This handler overrides the local check to also treat
/// a local community as local, and finalizes a community's follow by recording the edge in the community's
/// follows set — the inverse of the inbound <see cref="FollowActivityHandler"/>'s community branch. This is
/// what makes the follower side of a community-initiated follow (published via the community outbox, gap
/// G-3) two-sided: the followed side's <c>Accept</c> delivered back to the community's inbox finalizes the
/// community's <c>following</c> collection.
/// </remarks>
public sealed class AcceptActivityHandler : FollowResponseActivityHandler<Accept>
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="AcceptActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>,
    /// <see cref="IActivityStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the recipient is a local person.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public AcceptActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
        : base(persistence, localActors)
    {
        _persistence = persistence;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A community follower (in the community store) is also local, so the base local-actor guard is
    /// widened to cover it (a community is not in the person store, which the base guard consults).
    /// </remarks>
    protected override async Task<bool> IsLocalRecipientAsync(Iri followerIri, CancellationToken ct)
    {
        if (await base.IsLocalRecipientAsync(followerIri, ct).ConfigureAwait(false))
        {
            return true;
        }

        return await _persistence.Communities.TryGetCommunityAsync(followerIri, out _, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task ApplyAsync(Iri followerIri, Iri targetIri, CancellationToken ct)
    {
        // A community follower (in the community store): finalize the community's own follow by recording
        // the edge in the community's follows set — the inverse of the FollowActivityHandler's community
        // branch (a community follow is recorded in the community store, not the person follow store).
        if (await _persistence.Communities.TryGetCommunityAsync(followerIri, out _, ct).ConfigureAwait(false))
        {
            await _persistence.Communities.AddFollowAsync(followerIri, targetIri, ct).ConfigureAwait(false);
            return;
        }

        // A person follower (in the actor store): record the follower → target edge in the follow store.
        await Persistence.Follows.RecordFollowAsync(followerIri, targetIri, ct).ConfigureAwait(false);
    }
}
