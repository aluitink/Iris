using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Undo"/> activities: when the <em>local</em> actor (the recipient of the
/// delivery, i.e. the follower) undoes a follow it made, the local follow edge is removed from the
/// <see cref="IFollowStore"/> (an un-follow); when an actor undoes a <see cref="Block"/> it made, the
/// local block edge is removed from the <see cref="IModerationStore"/> (an un-block, F-07); when an actor
/// undoes a <see cref="Flag"/> it made, the local flag edge is removed (an un-flag, F-07); when an actor
/// undoes a <see cref="Like"/> it made, the local like edge is removed from the
/// <see cref="ILikeStore"/> (an unlike — the inverse of the <see cref="LikeActivityHandler"/>); and when
/// an actor undoes an <see cref="Announce"/> it made, the local announce edge is removed from the
/// <see cref="IAnnounceStore"/> and the boost is removed from the announcer's outbox (an un-boost — the
/// inverse of the <see cref="AnnounceActivityHandler"/>).
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

        // An unlike: when the Undo's object is a Like, remove the recorded like edge (the inverse of the
        // LikeActivityHandler). Handled before the follow path like the block/flag branches — a Like has
        // no follow target to resolve. A missing like (never recorded) is a no-op.
        if (await ResolveLikeEdgeAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false) is
            { } likeEdge)
        {
            await _persistence.Likes
                .RemoveLikeAsync(likeEdge.Liker, likeEdge.LikedObject, ct)
                .ConfigureAwait(false);
            return;
        }

        // An un-boost: when the Undo's object is an Announce, remove the recorded announce edge (the
        // inverse of the AnnounceActivityHandler) and remove the boost from the announcer's outbox.
        // Handled before the follow path like the block/flag/like branches — an Announce has no follow
        // target to resolve. A missing announce (never recorded) is a no-op. Before this branch existed,
        // Undo(Announce) was unhandled (an un-boost left the outbox entry in place).
        if (await ResolveAnnounceEdgeAsync(activity.Object?.FirstOrDefault(), ct).ConfigureAwait(false) is
            { } announceEdge)
        {
            await _persistence.Announces
                .RemoveAnnounceAsync(announceEdge.Announcer, announceEdge.AnnouncedObject, ct)
                .ConfigureAwait(false);

            // The boost's outbox entry (recorded by the AnnounceActivityHandler in the announcer's
            // outbox) is removed so the un-boosted object no longer appears in the announcer's outbox.
            var announceIri = activity.Object?.FirstOrDefault().ResolveObjectIri();
            if (announceIri.HasValue)
            {
                await _persistence.Activities
                    .RemoveFromOutboxAsync(announceEdge.Announcer, announceIri.Value, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // The recipient of the Undo is the party whose inbox received the delivery. The outbox publish
        // handler (OutboxPublishHandler) delivers an Undo of a Follow to the **target's** inbox (the
        // followed side), so the recipient is normally the target — not the un-follower. (F-1911-1:
        // the Undo must remove the follower from the target's followers set, not just the follower's
        // own following set.)
        var recipientIri = delivery.RecipientIri;

        // Resolve the original follow from the activity store (referenced by IRI in the Undo's object).
        // A missing follow (never stored) is a no-op — there is no edge to remove.
        var (targetIri, followerIri) = await ResolveFollowPartiesAsync(activity.Object?.FirstOrDefault(), ct)
            .ConfigureAwait(false);
        if (!targetIri.HasValue || !followerIri.HasValue)
        {
            return;
        }

        // When the recipient is the **target** (the followed side, the normal case for an outbox-published
        // Undo), remove the follower from the target's followers set (F-1911-1). The follower's own
        // following edge is removed on the follower's home instance by RemoveFollowLocalAsync (the
        // outbox handler); this handler on the target side removes the inverse edge.
        if (recipientIri == targetIri.Value)
        {
            // A person target (in the actor store): remove the follower from the target's followers set.
            if (await _localActors.IsLocalActorAsync(targetIri.Value, ct).ConfigureAwait(false))
            {
                await _persistence.Follows
                    .RemoveFollowAsync(followerIri.Value, targetIri.Value, ct)
                    .ConfigureAwait(false);
            }

            // When the target is a local community, the follow was also recorded in the community's
            // follows + followers sets (FollowActivityHandler's community branch, F-24): remove the
            // follower from both sets — the inverse of the follow.
            if (await _persistence.Communities
                    .TryGetCommunityAsync(targetIri.Value, out _, ct)
                    .ConfigureAwait(false))
            {
                await _persistence.Communities
                    .RemoveFollowerAsync(targetIri.Value, followerIri.Value, ct)
                    .ConfigureAwait(false);
                await _persistence.Communities
                    .RemoveFollowAsync(targetIri.Value, followerIri.Value, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // When the recipient is the **un-follower** (the party that made the follow being undone —
        // e.g. a direct inbox delivery, not the outbox publish path), remove the un-follower → target
        // edge from the follower's own following set.
        if (await _localActors.IsLocalActorAsync(recipientIri, ct).ConfigureAwait(false))
        {
            await _persistence.Follows
                .RemoveFollowAsync(recipientIri, targetIri.Value, ct)
                .ConfigureAwait(false);

            // When the un-followed target is a local community, the follow was also recorded in the
            // community's follows + followers sets (FollowActivityHandler's community branch, F-24):
            // remove the follower from the community's followers set and the follower from the
            // community's follows set — the inverse of the follow.
            if (await _persistence.Communities
                    .TryGetCommunityAsync(targetIri.Value, out _, ct)
                    .ConfigureAwait(false))
            {
                await _persistence.Communities
                    .RemoveFollowerAsync(targetIri.Value, recipientIri, ct)
                    .ConfigureAwait(false);
                await _persistence.Communities
                    .RemoveFollowAsync(targetIri.Value, recipientIri, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // A community un-follower (in the community store): remove the edges recorded when the follow
        // was delivered to this community's inbox. The community's follows + followers sets each hold a
        // (community → otherParty) edge where otherParty is the party on the OTHER side of the follow
        // (the target if this community made the follow, the follower if a remote party followed this
        // community). Remove both edges.
        if (await _persistence.Communities
                .TryGetCommunityAsync(recipientIri, out _, ct)
                .ConfigureAwait(false))
        {
            var otherParty = followerIri.Value == recipientIri ? targetIri.Value : followerIri.Value;
            await _persistence.Communities
                .RemoveFollowAsync(recipientIri, otherParty, ct)
                .ConfigureAwait(false);
            await _persistence.Communities
                .RemoveFollowerAsync(recipientIri, otherParty, ct)
                .ConfigureAwait(false);
            return;
        }

        // Neither a local person nor a local community (a remote un-follower): not this instance's
        // concern.
    }

    /// <summary>
    /// Resolves the original follow's parties from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Follow"/>, by IRI). Returns the follow's target (the party being followed,
    /// the follow's <c>object</c>) and the follow's follower (the party that made the follow, the
    /// follow's <c>actor</c>).
    /// </summary>
    private async Task<(Iri? Target, Iri? Follower)> ResolveFollowPartiesAsync(
        IObjectOrLink? responseObject, CancellationToken ct)
    {
        var followIri = responseObject.ResolveObjectIri();
        if (!followIri.HasValue)
        {
            return (null, null);
        }

        if (!await _persistence.Activities.TryGetActivityAsync(followIri.Value, out var storedFollow, ct)
            .ConfigureAwait(false) ||
            storedFollow is not Follow follow)
        {
            return (null, null);
        }

        var target = follow.Object?.FirstOrDefault().ResolveObjectIri();
        var follower = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        return (target, follower);
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

    /// <summary>
    /// Resolves the original like's parties from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Like"/>, by IRI) when the undone activity is a <see cref="Like"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the object is not a <see cref="Like"/> (the follow path
    /// applies), when the referenced like was never stored, or when the stored like's parties cannot be
    /// resolved (a malformed like) — in which case there is no recorded edge to remove.
    /// </remarks>
    private async Task<(Iri Liker, Iri LikedObject)?> ResolveLikeEdgeAsync(
        IObjectOrLink? responseObject,
        CancellationToken ct)
    {
        var likeIri = responseObject.ResolveObjectIri();
        if (!likeIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(likeIri.Value, out var stored, ct)
                .ConfigureAwait(false) ||
            stored is not Like like)
        {
            return null;
        }

        var likerIri = like.Actor?.FirstOrDefault().ResolveObjectIri();
        var likedObjectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!likerIri.HasValue || !likedObjectIri.HasValue)
        {
            return null;
        }

        return (likerIri.Value, likedObjectIri.Value);
    }

    /// <summary>
    /// Resolves the original announce's parties from the <see cref="Undo"/>'s object (a reference to the
    /// original <see cref="Announce"/>, by IRI) when the undone activity is an <see cref="Announce"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the object is not an <see cref="Announce"/> (the follow path
    /// applies), when the referenced announce was never stored, or when the stored announce's parties
    /// cannot be resolved (a malformed announce) — in which case there is no recorded edge to remove.
    /// </remarks>
    private async Task<(Iri Announcer, Iri AnnouncedObject)?> ResolveAnnounceEdgeAsync(
        IObjectOrLink? responseObject,
        CancellationToken ct)
    {
        var announceIri = responseObject.ResolveObjectIri();
        if (!announceIri.HasValue)
        {
            return null;
        }

        if (!await _persistence.Activities.TryGetActivityAsync(announceIri.Value, out var stored, ct)
                .ConfigureAwait(false) ||
            stored is not Announce announce)
        {
            return null;
        }

        var announcerIri = announce.Actor?.FirstOrDefault().ResolveObjectIri();
        var announcedObjectIri = announce.Object?.FirstOrDefault().ResolveObjectIri();
        if (!announcerIri.HasValue || !announcedObjectIri.HasValue)
        {
            return null;
        }

        return (announcerIri.Value, announcedObjectIri.Value);
    }
}
