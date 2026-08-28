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
    }
}
