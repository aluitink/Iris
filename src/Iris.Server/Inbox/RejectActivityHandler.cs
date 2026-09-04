using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Reject"/> activities: when a remote actor rejects a follow that a
/// <em>local</em> actor (a person or a community) made, the local follow edge is removed (undone) — from
/// the <see cref="IFollowStore"/> for a person follower, or from the community's follows set
/// (<see cref="ICommunityStore.RemoveFollowAsync"/>) for a community follower.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="AcceptActivityHandler"/>: on the follower side a follow is provisional until
/// the followed side responds. A <c>Reject</c> delivered back to the follower's inbox removes the
/// <c>follower → target</c> edge — but only when the follower is local. The target is resolved from the
/// original <c>Follow</c> (referenced by IRI in the Reject's object, fetched from the local activity
/// store). A missing target (the follow was never stored) is a no-op (there is no edge to remove).
/// </remarks>
/// <remarks>
/// <strong>Community follower (G-3, symmetric with <see cref="AcceptActivityHandler"/>)</strong> — a
/// community is a <see cref="Group"/> actor, not a person in the actor store, so the base local-actor
/// guard does not see it as local. This handler overrides the local check to also treat a local community
/// as local, and undoes a community's rejected follow by removing the edge from the community's follows
/// set — the inverse of the inbound <see cref="FollowActivityHandler"/>'s community branch and the exact
/// mirror of <see cref="AcceptActivityHandler"/>'s community arm. Without this, a community-initiated
/// follow (published via the community outbox, gap G-3) would be finalized by an inbound <c>Accept</c>
/// but a declined follow would never be undone on the community side (asymmetric with the person path).
/// </remarks>
public sealed class RejectActivityHandler : FollowResponseActivityHandler<Reject>
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="RejectActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>,
    /// <see cref="IActivityStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the recipient is a local person.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public RejectActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
        : base(persistence, localActors)
    {
        _persistence = persistence;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A community follower (in the community store) is also local, so the base local-actor guard is
    /// widened to cover it (a community is not in the person store, which the base guard consults) — the
    /// exact mirror of <see cref="AcceptActivityHandler.IsLocalRecipientAsync(Iri, CancellationToken)"/>.
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
        // A community follower (in the community store): undo the community's rejected follow by removing
        // the edge from the community's follows set — the inverse of the FollowActivityHandler's community
        // branch and the mirror of AcceptActivityHandler's community arm (a community follow is recorded in
        // the community store, not the person follow store).
        if (await _persistence.Communities.TryGetCommunityAsync(followerIri, out _, ct).ConfigureAwait(false))
        {
            await _persistence.Communities.RemoveFollowAsync(followerIri, targetIri, ct).ConfigureAwait(false);
            return;
        }

        // A person follower (in the actor store): remove the follower → target edge from the follow store.
        await Persistence.Follows.RemoveFollowAsync(followerIri, targetIri, ct).ConfigureAwait(false);
    }
}
