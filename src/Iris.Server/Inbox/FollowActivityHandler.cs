using Iris.Core;
using Iris.Server.Caching;
using Iris.Server.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

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
/// <em>community</em>, <em>two</em> edges are recorded (Resolved Decision #36, extended for F-24): the
/// community's follows set (<see cref="ICommunityStore.AddFollowAsync"/>) — the community "follows"
/// the follower, so the follower's content reaches the community's members via the federation path —
/// and the community's followers set (<see cref="ICommunityStore.AddFollowerAsync"/>) — the follower
/// "follows" the community, so the community's <c>followers</c> collection lists the follower (F-24).
/// A follow addressed to a remote actor is not this instance's concern.</item>
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
    private readonly IdMinter _idMinter;
    private readonly LocalCollectionPageCache? _collectionCache;

    /// <summary>
    /// Initializes a new <see cref="FollowActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the <c>Accept</c> response).</param>
    /// <param name="localActors">Resolves whether the recipient is a local actor (the follow is
    /// interpreted only when the recipient is local).</param>
    /// <param name="idMinter">The server-side id authority (mints the id of the <c>Accept</c> the handler
    /// authors in response to an inbound follow — decision 055).</param>
    /// <param name="collectionCache">The local collection-page response cache (invalidated after an
    /// inbound follow so the recipient's <c>followers</c> page re-renders). May be null in tests.</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public FollowActivityHandler(
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors,
        IdMinter idMinter,
        LocalCollectionPageCache? collectionCache = null,
        ILogger<FollowActivityHandler>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(localActors);
        ArgumentNullException.ThrowIfNull(idMinter);
        _persistence = persistence;
        _delivery = delivery;
        _localActors = localActors;
        _idMinter = idMinter;
        _collectionCache = collectionCache;
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
            // The recipient is a local community. A community's "membership" is its follower set
            // (members are followers — change 221), so an inbound Follow is a join: it records the
            // community's "following" edge (community → follower) which drives the federated feed, and
            // — unless the community manually approves members — the inverse "followers" edge
            // (follower → community) which is the membership. Surface the inbound follow in the
            // community's own outbox (the activity store alone is not enumerable) so a community
            // operator can list it (change 152).
            await _persistence.Communities
                .AddFollowAsync(delivery.RecipientIri, followerIri.Value, ct)
                .ConfigureAwait(false);

            await _persistence.Activities
                .AddToOutboxAsync(delivery.RecipientIri, follow, ct)
                .ConfigureAwait(false);

            // When the community manually approves members, the follow is NOT auto-accepted: the
            // follower edge is withheld and a pending join request is recorded so the operator's
            // requests tab (GET /local/v1/c/{name}/requests) lists it. The operator's Accept/Reject
            // then grants or drops the membership (RecordJoinDecisionLocalAsync). When the gate is off
            // the follower edge is recorded immediately (an auto-accepted join).
            if (await IsManuallyApprovingMembersAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
            {
                await _persistence.Communities
                    .AddJoinRequestAsync(delivery.RecipientIri, followerIri.Value, ct)
                    .ConfigureAwait(false);

                return;
            }

            await _persistence.Communities
                .AddFollowerAsync(delivery.RecipientIri, followerIri.Value, ct)
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

            // Invalidate the recipient's followers collection page so the next non-?refresh read
            // re-renders with the new follower (mirrors the outbox-page invalidation contract).
            _collectionCache?.Invalidate(new Iri(delivery.RecipientIri + "/followers"));

            // Surface the inbound follow in the followed actor's own outbox (the activity store alone is
            // not enumerable by the UI). The sample's "Inbound follows" list reads the followed actor's
            // outbox for Follow activities so the operator can see — and Accept/Reject — the request.
            // (An auto-accepted follow also lands here; the operator's Accept of an already-accepted
            // follow is idempotent.)
            await _persistence.Activities
                .AddToOutboxAsync(delivery.RecipientIri, follow, ct)
                .ConfigureAwait(false);

            if (await IsManuallyApprovingAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
            {
                // The follow is held for approval: record a pending follow-request edge (Phase 100) so
                // the actor's follow-approval queue (GET /local/v1/u/{handle}/requests) lists it. The
                // edge is independent of the provisional Follow edge (already recorded above) and is
                // removed when the operator Accepts or Rejects (RecordFollowDecisionLocalAsync).
                await _persistence.Follows
                    .RecordFollowRequestAsync(followerIri.Value, delivery.RecipientIri, ct)
                    .ConfigureAwait(false);

                return;
            }
        }

        // Respond to the follow: construct an Accept (actor = the local actor/community being followed,
        // which is delivery.RecipientIri — the actor IRI, per InboxDelivery's contract; object = the
        // original follow) and schedule it for delivery to the follower's inbox. DeliverToActorAsync
        // derives the follower's inbox from the follower's actor IRI; the delivery is signed as the
        // local actor being followed (the Accept's actor), so the remote verifies it against that
        // actor's key.
        var accept = FollowIris.BuildAccept(_idMinter, delivery.RecipientIri, follow);
        await _delivery
            .DeliverToActorAsync(followerIri.Value, accept, delivery.RecipientIri, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the local <em>person</em> has <c>manuallyApprovesFollowers</c> set (i.e. should not
    /// auto-accept an inbound follow). The library's <c>Actor</c> type does not model the property, so it
    /// is read from the actor's <c>ExtensionData</c> (seeded by the host and echoed onto the public
    /// document — Resolved Decision #46). A missing actor or a missing/false value means auto-accept (the
    /// default). (A community's inbound follows are gated separately by
    /// <see cref="IsManuallyApprovingMembersAsync"/>, since a community's members are its followers —
    /// change 221.)
    /// </summary>
    /// <param name="actorIri">The IRI of the local actor being followed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the actor manually approves followers; otherwise
    /// <see langword="false"/>.</returns>
    private async Task<bool> IsManuallyApprovingAsync(Iri actorIri, CancellationToken ct)
    {
        if (await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false)
            && actor is { } localActor)
        {
            return IsManuallyApprovingFollowers(localActor.ExtensionData);
        }

        return false;
    }

    /// <summary>
    /// Reports whether the local <em>community</em> has <c>manuallyApprovesMembers</c> set (i.e. should hold
    /// an inbound follow — a join, since members are followers, change 221 — for approval rather than
    /// auto-accepting it). The flag lives in the stored community's <c>ExtensionData</c> (change 217). A
    /// missing community or a missing/false value means auto-accept (the default).
    /// </summary>
    /// <param name="communityIri">The IRI of the local community being followed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the community manually approves members; otherwise
    /// <see langword="false"/>.</returns>
    private async Task<bool> IsManuallyApprovingMembersAsync(Iri communityIri, CancellationToken ct)
    {
        if (await _persistence.Communities.TryGetCommunityAsync(communityIri, out var community, ct).ConfigureAwait(false)
            && community is { } localCommunity)
        {
            return IsManuallyApprovingMembers(localCommunity.ExtensionData);
        }

        return false;
    }

    private static bool IsManuallyApprovingFollowers(Dictionary<string, System.Text.Json.JsonElement>? extensionData)
        => extensionData is { } ext
            && ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.True;

    private static bool IsManuallyApprovingMembers(Dictionary<string, System.Text.Json.JsonElement>? extensionData)
        => extensionData is { } ext
            && ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesMembersExtensionName, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.True;
}
