using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Follow"/> activities: records the follow edge in the
/// <see cref="IFollowStore"/> (when the recipient is a local actor) or, when the recipient is a
/// local <em>community</em>, records the follow in the community's follows set; and delivers an
/// <see cref="Accept"/> back to the follower's inbox in either case.
/// </summary>
/// <remarks>
/// An inbound <c>Follow</c> is a remote actor asking to follow a local actor <em>or</em> a local
/// community. The handler:
/// <list type="number">
/// <item>Records the directed edge <c>follower → recipient</c>: the follower is the activity's
/// <c>actor</c>, and the target is the delivery's <see cref="InboxDelivery.RecipientIri"/> (the inbox
/// the follow was delivered to — authoritative for the target). When the recipient is a local
/// <em>person</em>, the edge goes to the <see cref="IFollowStore"/>; when the recipient is a local
/// <em>community</em>, the edge goes to the community's follows set
/// (<see cref="ICommunityStore.AddFollowAsync"/>) — a community "follows" the follower, not the
/// reverse (Resolved Decision #36). A follow addressed to a remote actor is not this instance's
/// concern.</item>
/// <item>Constructs an <c>Accept</c> (actor = the local actor/community being followed, object = the
/// original follow) and schedules it for delivery to the follower's inbox via
/// <see cref="IDeliveryService"/> (asynchronous — the handler returns after enqueuing; the
/// <see cref="DeliveryWorker"/> POSTs it, signed as the instance actor, once the worker pumps the
/// queue). When the follower's <see cref="AcceptActivityHandler"/> receives it, the follower
/// finalizes its own copy of the follow edge — so on the follower side the edge is provisional until
/// accepted (Resolved Decision: the follow lifecycle is two-sided; the followed side's acceptance
/// drives the follower's state).</item>
/// </list>
/// A follow of a community is <em>not</em> a membership grant: following a community records that the
/// community follows the follower (so the follower's content reaches the community's members via the
/// federation path); it does not add the follower to the community's member set (membership is a
/// separate, local-administered relationship).
/// </remarks>
public sealed class FollowActivityHandler : ActivityHandlerBase<Follow>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IDeliveryService _delivery;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="FollowActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the <c>Accept</c> response).</param>
    /// <param name="localActors">Resolves whether the recipient is a local actor (the follow is
    /// interpreted only when the recipient is local).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public FollowActivityHandler(
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _delivery = delivery;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Follow follow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(follow);

        // The follower is the activity's actor (Rule 3: read multi-valued as IEnumerable, null-safe).
        // The actor is an IObjectOrLink: either a Link (Href) or an embedded Object (Id).
        var followerIri = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!followerIri.HasValue)
        {
            // A follow with no resolvable actor is malformed; nothing to record. The activity is
            // still stored (by the processor) so it can be inspected.
            return;
        }

        // Interpret the follow only when the recipient is a local actor OR a local community (the inbox
        // the follow was delivered to belongs to this instance). A follow addressed to a remote actor
        // (neither in the actor store nor the community store) is not this instance's concern.
        var isLocalCommunity = await _persistence.Communities
            .TryGetCommunityAsync(delivery.RecipientIri, out _, ct)
            .ConfigureAwait(false);
        if (!isLocalCommunity && !await _localActors.IsLocalActorAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // Record the directed follow edge. A community recipient is a local Group (not a Person in the
        // IActorStore), so it is local per the ICommunityStore, not per ILocalActorResolver. The two
        // cases are disjoint: an IRI is either a community (in ICommunityStore) or a person (in
        // IActorStore), never both.
        if (isLocalCommunity)
        {
            // The recipient is a local community: record that the community follows the follower
            // (the community's "following" set), not a person-follow edge. This is what lets the
            // follower's content reach the community's members via the federation path.
            await _persistence.Communities
                .AddFollowAsync(delivery.RecipientIri, followerIri.Value, ct)
                .ConfigureAwait(false);
        }
        else
        {
            // The recipient is a local person: record the directed follow edge follower → recipient.
            // When the person has manuallyApprovesFollowers set, the edge is still recorded (the
            // follower's content should reach the local followers' outboxes via the federation path),
            // but the follow is NOT auto-accepted — the operator must respond with an explicit
            // Accept or Reject (J-10 / Resolved Decision #46).
            await _persistence.Follows
                .RecordFollowAsync(followerIri.Value, delivery.RecipientIri, ct)
                .ConfigureAwait(false);

            if (await IsManuallyApprovingAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        // Respond to the follow: construct an Accept (actor = the local actor/community being followed,
        // which is delivery.RecipientIri — the actor IRI, per InboxDelivery's contract; object = the
        // original follow) and schedule it for delivery to the follower's inbox. DeliverToActorAsync
        // derives the follower's inbox from the follower's actor IRI; the delivery is signed as the
        // local actor being followed (the Accept's actor), so the remote verifies it against that
        // actor's key.
        var accept = FollowIris.BuildAccept(delivery.RecipientIri, follow);
        await _delivery
            .DeliverToActorAsync(followerIri.Value, accept, delivery.RecipientIri, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the local actor has <c>manuallyApprovesFollowers</c> set (i.e. should not
    /// auto-accept an inbound follow). The library's <c>Actor</c> type does not model the property, so
    /// it is read from the actor's <c>ExtensionData</c> (seeded by the host and echoed onto the public
    /// document — Resolved Decision #46). A missing actor or a missing/false value means auto-accept
    /// (the default).
    /// </summary>
    /// <param name="actorIri">The IRI of the local actor being followed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the actor manually approves followers; otherwise <see langword="false"/>.</returns>
    private async Task<bool> IsManuallyApprovingAsync(Iri actorIri, CancellationToken ct)
    {
        if (!await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false)
            || actor is not { } localActor)
        {
            return false;
        }

        return localActor.ExtensionData is { } ext
            && ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}
