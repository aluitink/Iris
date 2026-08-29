using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Announce"/> activities: records the announce in the recipient's
/// outbox (when the recipient is a local actor) and propagates it to the recipient's local
/// followers' inboxes.
/// </summary>
/// <remarks>
/// An inbound <c>Announce</c> is an actor re-sharing (boosting/reposting) an object. When the
/// recipient is a <em>local</em> actor (the inbox the announce was delivered to belongs to this
/// instance), the handler:
/// <list type="number">
/// <item>Records the announce in the recipient's outbox via
/// <see cref="IActivityStore.AddToOutboxAsync"/>, so the boost is discoverable from the actor's
/// outbox collection (newest first).</item>
/// <item>Propagates the announce to each of the recipient's local followers: for every local follower
/// it schedules the propagated <c>Announce</c> (deterministic IRI
/// <c>{recipient}/announces/{objectIri}</c>, <c>to</c> = the follower, <c>cc</c> = the announcer,
/// <c>actor</c>/<c>attributedTo</c> = the announcer) for delivery to the follower's inbox via
/// <see cref="IDeliveryService"/>. The delivery is signed as the announcer
/// (<see cref="InboxDelivery.RecipientIri"/>), so the follower's instance verifies it against the
/// announcer's key.</item>
/// </list>
/// </remarks>
/// <para>
/// <strong>Local followers only.</strong> Only local followers are propagated to: a local follower
/// is a local actor who follows the recipient, and its inbox is delivered by <em>this</em> instance's
/// <see cref="DeliveryWorker"/>. Remote followers are skipped (they are the remote instance's concern —
/// that instance receives the announce via its own federation path). When a local follower's delivery
/// target is resolved by <see cref="IDeliveryService.DeliverToActorAsync(Iri, Activity, CancellationToken)"/>,
/// it is the follower's own inbox (local actors advertise no <c>endpoints.sharedInbox</c> unless the
/// instance configures one). Propagation is therefore limited to the recipient's <em>local</em>
/// followers, which is the in-scope, verifiable behavior this feature targets.
/// </para>
/// <para>
/// The announce is recorded in the outbox and scheduled for delivery even when the recipient has no
/// local followers (the outbox entry stands on its own). A malformed announce (no resolvable actor
/// or no resolvable object) is stored (by the processor) but interpreted as a no-op: nothing is
/// recorded and nothing is scheduled.
/// </para>
/// <para>
/// <strong>Relay fan-out (F-06).</strong> After propagating the boost to the announcer's local followers,
/// the handler also delivers the announce to each <em>relay</em> the announcer has subscribed to (the
/// announcer's <c>relays</c> / <c>star</c> set, AP §5.1.3), signed as the announcer. A relay is a remote
/// fan-out server, so — unlike the follower loop — no local-actor skip is needed (a relay is never a local
/// actor), and a relay that has blocked the announcer is suppressed by <see cref="IDeliveryService"/>
/// (F-07) before it is enqueued. This is the delivery half of the relay feature; the subscription half
/// (the local <c>relays</c> collection) is recorded by the Basic-authenticated relay endpoint
/// (Slice 12.18).
/// </para>
public sealed class AnnounceActivityHandler : ActivityHandlerBase<Announce>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IDeliveryService _delivery;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="AnnounceActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IActivityStore"/>
    /// and <see cref="IFollowStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the propagated <c>Announce</c> to each
    /// local follower's inbox).</param>
    /// <param name="localActors">Resolves whether the recipient (and each candidate follower) is a
    /// local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public AnnounceActivityHandler(
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
    public override async Task HandleAsync(InboxDelivery delivery, Announce announce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(announce);

        // The announcer is the activity's actor (Rule 3: read multi-valued as IEnumerable, null-safe).
        var announcerIri = announce.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!announcerIri.HasValue)
        {
            // An announce with no resolvable actor is malformed; nothing to record or propagate. The
            // activity is still stored (by the processor) so it can be inspected.
            return;
        }

        // The announced object is the activity's object (Rule 3: read multi-valued as IEnumerable,
        // null-safe).
        var objectIri = announce.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            // An announce with no resolvable object is malformed; nothing to record or propagate.
            return;
        }

        // Interpret the announce only when the recipient is a local actor (the inbox the announce was
        // delivered to belongs to this instance). An announce addressed to a remote actor is not this
        // instance's concern.
        if (!await _localActors.IsLocalActorAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // Record the announce in the recipient's outbox (newest first). The recipient is the
        // announcer (delivery.RecipientIri is the actor IRI whose inbox received the announce).
        await _persistence.Activities
            .AddToOutboxAsync(delivery.RecipientIri, announce, ct)
            .ConfigureAwait(false);

        // Propagate the announce to the recipient's local followers: for each local follower, schedule
        // the propagated Announce (deterministic IRI, to=follower, cc=announcer) for delivery to the
        // follower's inbox, signed as the announcer (the recipient). DeliverToActorAsync derives the
        // follower's inbox from the follower's actor IRI.
        var followers = await _persistence.Follows
            .GetFollowersAsync(delivery.RecipientIri, ct)
            .ConfigureAwait(false);
        foreach (var followerIri in followers)
        {
            // Only propagate to local followers (their inboxes are delivered by this instance's
            // DeliveryWorker); a remote follower is the remote instance's concern.
            if (!await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            var propagated = AnnounceIris.BuildAnnounce(delivery.RecipientIri, objectIri.Value, followerIri);
            await _delivery
                .DeliverToActorAsync(followerIri, propagated, delivery.RecipientIri, ct)
                .ConfigureAwait(false);
        }

        // F-06 (relay fan-out): deliver the boost to each relay the announcer has subscribed to (a
        // `star`-subscribed fan-out server, AP §5.1.3), signed as the announcer. A relay is a remote
        // fan-out server — always cross-instance — so no local-actor / block check is needed: the relay
        // is never a local actor, and a relay that has blocked the announcer is suppressed by
        // IDeliveryService.DeliverToActorAsync (F-07) before it is enqueued.
        await DeliverToSubscribedRelaysAsync(delivery.RecipientIri, announce, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// F-06 (relay fan-out): delivers the <paramref name="announce"/> to each relay the
    /// <paramref name="announcerIri"/> has subscribed to (the announcer's <c>relays</c> / <c>star</c>
    /// set), signed as the announcer. A relay is a remote <c>star</c>-subscribed fan-out server (AP
    /// §5.1.3); this is the delivery half of the relay feature (the subscription half — recording the
    /// <c>announcer → relay</c> edge — is the local <c>relays</c> collection, Slice 12.18). When the
    /// announcer has no subscribed relays, nothing is scheduled. A relay that has blocked the announcer
    /// is suppressed by <see cref="IDeliveryService.DeliverToActorAsync(Iri, Activity, Iri?,
    /// CancellationToken)"/> (F-07) before it is enqueued.
    /// </summary>
    /// <param name="announcerIri">The announcer (the signing actor of the relay delivery).</param>
    /// <param name="announce">The <see cref="Announce"/> to fan out (the original announce, carrying the
    /// deterministic IRI).</param>
    /// <param name="ct">A cancellation token.</param>
    private async Task DeliverToSubscribedRelaysAsync(Iri announcerIri, Announce announce, CancellationToken ct)
    {
        var relays = await _persistence.Relays
            .GetRelaysAsync(announcerIri, ct)
            .ConfigureAwait(false);
        foreach (var relayIri in relays)
        {
            await _delivery
                .DeliverToActorAsync(relayIri, announce, announcerIri, ct)
                .ConfigureAwait(false);
        }
    }
}
