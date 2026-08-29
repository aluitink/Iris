using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Undo"/> activities: when the <em>local</em> actor (the recipient of the
/// delivery, i.e. the follower) undoes a follow it made, the local follow edge is removed from the
/// <see cref="IFollowStore"/> (an un-follow); when an actor undoes a <see cref="Block"/> it made, the
/// local block edge is removed from the <see cref="IModerationStore"/> (an un-block, F-07); and when an
/// actor undoes a <see cref="Flag"/> it made, the local flag edge is removed (an un-flag, F-07).
/// </summary>
/// <remarks>
/// An <c>Undo</c> is the ActivityStreams inverse primitive: it undoes the activity referenced by its
/// <c>object</c>. The <c>object</c> here is the original <c>Follow</c> (referenced by IRI). The handler
/// resolves the follow's target from the stored <c>Follow</c> (fetched from the local activity store —
/// the follower stored it when it sent the follow) and, when the recipient is a <em>local</em> actor,
/// removes the <c>follower → target</c> edge.
/// </remarks>
/// <para>
/// <strong>Recipient is the follower.</strong> A follow is undone by the party that made it, so the
/// <c>Undo</c> is delivered to the follower's inbox and <see cref="InboxDelivery.RecipientIri"/> is the
/// follower — the same convention the <see cref="AcceptActivityHandler"/>/<see cref="RejectActivityHandler"/>
/// use for follow responses. An <c>Undo</c> whose recipient is a <em>remote</em> actor is not this
/// instance's concern (the remote instance owns that actor's follow state), so it is a no-op.
/// </para>
/// <para>
/// <strong>Person vs. community.</strong> The person and community stores are disjoint. When the
/// follower is a local <em>person</em> (in the actor store) the edge is removed from the
/// <see cref="IFollowStore"/>; and when the un-followed target is a local <em>community</em>, the
/// follower is also removed from the community's followers set
/// (<see cref="ICommunityStore.RemoveFollowerAsync"/>) and follows set
/// (<see cref="ICommunityStore.RemoveFollowAsync"/>) — the inverse of the community follow
/// (<see cref="FollowActivityHandler"/>'s community branch, F-24). When the follower is a local
/// <em>community</em> (in the community store) the edge is removed from the community's follows set
/// (<see cref="ICommunityStore.RemoveFollowAsync"/>), the inverse of the community's own follow. A
/// missing target (the follow was never stored) is a no-op (there is no edge to remove).
/// </para>
/// <para>
/// <strong>Un-block (F-07).</strong> When the <c>Undo</c>'s object is a <see cref="Block"/> (an un-block),
/// the <c>blocker → blocked</c> edge recorded by <see cref="BlockActivityHandler"/> is removed from the
/// <see cref="IModerationStore"/> via <see cref="IModerationStore.RemoveBlockAsync"/> — the inverse of the
/// block. The block's parties are read from the original <c>Block</c> (fetched from the local activity
/// store), so the removal is scoped to the exact edge that was recorded (a local blocker of anyone, or a
/// blocker of a local actor).
/// </para>
/// <para>
/// <strong>Un-flag (F-07).</strong> When the <c>Undo</c>'s object is a <see cref="Flag"/> (an un-flag),
/// the <c>flagger → flagged</c> edge recorded by <see cref="FlagActivityHandler"/> is removed from the
/// <see cref="IModerationStore"/> via <see cref="IModerationStore.RemoveFlagAsync"/> — the inverse of the
/// flag. The flag's parties are read from the original <c>Flag</c> (fetched from the local activity
/// store). An <c>Undo</c> of any other activity type (not a <c>Follow</c>, a <c>Block</c>, or a
/// <c>Flag</c>) is a no-op (there is no corresponding edge in a store this instance owns).
/// </para>
public sealed class UndoActivityHandler : ActivityHandlerBase<Undo>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="UndoActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>,
    /// <see cref="IActivityStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether an actor IRI is a local person.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="localActors"/> is null.</exception>
    public UndoActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Undo activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // An un-block (F-07): when the Undo's object is a Block, remove the recorded block edge (the
        // inverse of the BlockActivityHandler). Handled before the follow path because a Block has no
        // follow target to resolve.
        if (await ResolveBlockEdgeAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false) is
            { } blockEdge)
        {
            await _persistence.Moderation
                .RemoveBlockAsync(blockEdge.Blocker, blockEdge.Blocked, ct)
                .ConfigureAwait(false);
            return;
        }

        // An un-flag (F-07): when the Undo's object is a Flag, remove the recorded flag edge (the
        // inverse of the FlagActivityHandler). Handled before the follow path like the block branch.
        if (await ResolveFlagEdgeAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false) is
            { } flagEdge)
        {
            await _persistence.Moderation
                .RemoveFlagAsync(flagEdge.Flagger, flagEdge.Flagged, ct)
                .ConfigureAwait(false);
            return;
        }

        // The recipient of the Undo is the follower (the party that made the follow being undone).
        var followerIri = delivery.RecipientIri;

        // Resolve the follow's target from the original Follow (referenced by IRI in the Undo's object,
        // fetched from the local activity store). A missing target (the follow was never stored) is a
        // no-op — there is no edge to remove.
        var targetIri = await ResolveFollowTargetAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false);
        if (!targetIri.HasValue)
        {
            return;
        }

        // A person follower (in the actor store): remove the follower → target edge. The person and
        // community stores are disjoint, so check the person store first.
        if (await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
        {
            await _persistence.Follows
                .RemoveFollowAsync(followerIri, targetIri.Value, ct)
                .ConfigureAwait(false);

            // When the un-followed target is a local community, the follow was also recorded in the
            // community's follows + followers sets (FollowActivityHandler's community branch, F-24):
            // remove the follower from the community's followers set and the follower from the
            // community's follows set — the inverse of the follow. A person un-following a community
            // leaves no IFollowStore edge (the follow of a community is recorded in the community
            // store, not the person follow store), so this is the real removal.
            if (await _persistence.Communities
                    .TryGetCommunityAsync(targetIri.Value, out _, ct)
                    .ConfigureAwait(false))
            {
                await _persistence.Communities
                    .RemoveFollowerAsync(targetIri.Value, followerIri, ct)
                    .ConfigureAwait(false);
                await _persistence.Communities
                    .RemoveFollowAsync(targetIri.Value, followerIri, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // A community follower (in the community store): remove the follow from the community's follows
        // set — the inverse of the FollowActivityHandler's community branch.
        if (await _persistence.Communities
                .TryGetCommunityAsync(followerIri, out _, ct)
                .ConfigureAwait(false))
        {
            await _persistence.Communities
                .RemoveFollowAsync(followerIri, targetIri.Value, ct)
                .ConfigureAwait(false);
            return;
        }

        // Neither a local person nor a local community (a remote follower): not this instance's concern.
    }

    /// <summary>
    /// Resolves the original follow's target from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Follow"/>, by IRI).
    /// </summary>
    private async Task<Iri?> ResolveFollowTargetAsync(IObjectOrLink? responseObject, CancellationToken ct)
    {
        var followIri = responseObject.ResolveObjectIri();
        if (!followIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(followIri.Value, out var storedFollow, ct)
            .ConfigureAwait(false) ||
            storedFollow is not Follow follow)
        {
            return null;
        }

        return follow.Object?.FirstOrDefault().ResolveObjectIri();
    }

    /// <summary>
    /// Resolves the original block's parties from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Block"/>, by IRI) when the undone activity is a <see cref="Block"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the object is not a <see cref="Block"/> (the follow path
    /// applies), when the referenced block was never stored, or when the stored block's parties cannot
    /// be resolved (a malformed block) — in which case there is no recorded edge to remove.
    /// </remarks>
    private async Task<(Iri Blocker, Iri Blocked)?> ResolveBlockEdgeAsync(
        IObjectOrLink? responseObject,
        CancellationToken ct)
    {
        var blockIri = responseObject.ResolveObjectIri();
        if (!blockIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(blockIri.Value, out var stored, ct)
            .ConfigureAwait(false) ||
            stored is not Block block)
        {
            return null;
        }

        var blockerIri = block.Actor?.FirstOrDefault().ResolveObjectIri();
        var blockedIri = block.Object?.FirstOrDefault().ResolveObjectIri();
        if (!blockerIri.HasValue || !blockedIri.HasValue)
        {
            return null;
        }

        return (blockerIri.Value, blockedIri.Value);
    }

    /// <summary>
    /// Resolves the original flag's parties from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Flag"/>, by IRI) when the undone activity is a <see cref="Flag"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the object is not a <see cref="Flag"/> (the follow path
    /// applies), when the referenced flag was never stored, or when the stored flag's parties cannot be
    /// resolved (a malformed flag) — in which case there is no recorded edge to remove.
    /// </remarks>
    private async Task<(Iri Flagger, Iri Flagged)?> ResolveFlagEdgeAsync(
        IObjectOrLink? responseObject,
        CancellationToken ct)
    {
        var flagIri = responseObject.ResolveObjectIri();
        if (!flagIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(flagIri.Value, out var stored, ct)
            .ConfigureAwait(false) ||
            stored is not Flag flag)
        {
            return null;
        }

        var flaggerIri = flag.Actor?.FirstOrDefault().ResolveObjectIri();
        var flaggedIri = flag.Object?.FirstOrDefault().ResolveObjectIri();
        if (!flaggerIri.HasValue || !flaggedIri.HasValue)
        {
            return null;
        }

        return (flaggerIri.Value, flaggedIri.Value);
    }
}
