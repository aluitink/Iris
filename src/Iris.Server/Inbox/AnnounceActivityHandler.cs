using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

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
/// <item>Propagates the announce to each of the recipient's followers (mirroring
/// <see cref="CreateActivityHandler"/>): a <em>local</em> follower sees the boost via the follower's
/// outbox on this instance (recorded directly — no cross-instance delivery); a <em>remote</em>
/// follower needs a cross-instance delivery (the peer's handler records it in the follower's outbox
/// on its instance). The propagated <c>Announce</c> (deterministic IRI
/// <c>{recipient}/announces/{objectIri}</c>, <c>to</c> = the follower, <c>cc</c> = the announcer,
/// <c>actor</c>/<c>attributedTo</c> = the announcer) is stored under the shared IRI (the
/// inbox processor's add-if-absent guard keeps the propagated form), and the remote delivery is
/// signed as the announcer (<see cref="InboxDelivery.RecipientIri"/>).</item>
/// </list>
/// </remarks>
/// <para>
/// <strong>Follower fan-out (mirrors Create).</strong> The boost is propagated to the recipient's
/// followers the same way a Create is (see <see cref="CreateActivityHandler"/>): a <em>local</em>
/// follower sees the boost via the follower's outbox on this instance (recorded directly — no
/// cross-instance delivery, the follower's followed-feed reads the outbox); a <em>remote</em>
/// follower needs a cross-instance delivery (the peer's <c>AnnounceActivityHandler</c> records it in
/// the follower's outbox on its instance). The boost's id (minted once at record-time — decision 055,
/// see <see cref="AnnounceIris.BuildAnnounce(Iri, Iri, Iri, Iri)"/> — and reused for every propagated
/// copy) is scoped to the actual announcer, so the propagated form and the original form share the same
/// id: the inbox processor's add-if-absent guard (the 19.3.1/19.3.2 re-delivery loop guard) stores the
/// propagated (addressed) form under that IRI, which the outbox and follower view surface.
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
    private readonly Stores.ObjectInteractionCountRefreshService? _countRefresh;

    /// <summary>
    /// Initializes a new <see cref="AnnounceActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IActivityStore"/>
    /// and <see cref="IFollowStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the propagated <c>Announce</c> to each
    /// local follower's inbox).</param>
    /// <param name="localActors">Resolves whether the recipient (and each candidate follower) is a
    /// local actor.</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <param name="countRefresh">
    /// The interaction-count refresher (S37/S28): when present, the announced object's denormalized
    /// <c>sharedCount</c> is refreshed immediately after the announce edge is recorded, so the object
    /// document is correct on the next read without waiting for the periodic refresh pass. May be null
    /// (a host that does not register it) — in that case the count converges on the next interval pass.
    /// </param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public AnnounceActivityHandler(
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors,
        ILogger<AnnounceActivityHandler>? logger = null,
        Stores.ObjectInteractionCountRefreshService? countRefresh = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _delivery = delivery;
        _localActors = localActors;
        _countRefresh = countRefresh;
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
        var firstObject = announce.Object?.FirstOrDefault();
        var objectIri = firstObject?.ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            // An announce with no resolvable object is malformed; nothing to record or propagate.
            return;
        }

        // Interpret the announce only when the recipient is a local actor OR a local community (the
        // inbox the announce was delivered to belongs to this instance). An announce addressed to a
        // remote actor is not this instance's concern. The person and community stores are disjoint
        // (a community lives in the community store, not the actor store), so check both.
        var isLocalActor = await _localActors.IsLocalActorAsync(delivery.RecipientIri, ct).ConfigureAwait(false);
        var isLocalCommunity = await _persistence.Communities
            .TryGetCommunityAsync(delivery.RecipientIri, out _, ct)
            .ConfigureAwait(false);
        if (!isLocalActor && !isLocalCommunity)
        {
            return;
        }

        // 138.12 (Lemmy community relay): when the Announce's object is an embedded Create activity
        // (not a bare object IRI), it's a Lemmy-style community relay — the community is relaying a
        // member's post. Unwrap the Create and handle it as if it were delivered directly to the
        // community's inbox (so the embedded Page/Note is stored in the object store and recorded in
        // the community's local members' outboxes). The Announce itself is still recorded in the
        // community's outbox (the relay envelope), but the embedded Create is what surfaces the post
        // in the community feed.
        if (firstObject is Create embeddedCreate)
        {
            await HandleEmbeddedCreateAsync(delivery, embeddedCreate, ct).ConfigureAwait(false);
        }

        // Record the announce in the recipient's outbox (newest first). The recipient is the
        // announcer (delivery.RecipientIri is the actor IRI whose inbox received the announce).
        await _persistence.Activities
            .AddToOutboxAsync(delivery.RecipientIri, announce, ct)
            .ConfigureAwait(false);

        // Record the announcer → announced-object edge in the announce store (both directions): this is
        // the durable boost record and the <c>announcedBy</c> reverse index (the per-object boost
        // counter, decision 056 (d)). The outbox entry above makes the boost discoverable from the
        // announcer's outbox; this edge makes it queryable against the object (and reversible via
        // UndoActivityHandler's Undo(Announce)).
        await _persistence.Announces
            .RecordAnnounceAsync(announcerIri.Value, objectIri.Value, ct)
            .ConfigureAwait(false);

        // S28/S37: refresh the object's denormalized sharedCount immediately so the object document
        // is correct on the next read, rather than waiting for the periodic refresh pass (which only
        // runs every 30 s and would otherwise serve the stale pre-computed 0). Only when the
        // announced object is stored locally (a remote object's counters are maintained on its home
        // instance).
        if (_countRefresh is { } refresh
            && await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out _, ct).ConfigureAwait(false))
        {
            await refresh.RefreshObjectCountsAsync(objectIri.Value, ct).ConfigureAwait(false);
        }

        // Propagate the announce to the recipient's followers (mirroring CreateActivityHandler's fan-out):
        // a local follower sees the boost via the follower's outbox on this instance (recorded directly —
        // no cross-instance delivery, the follower's followed-feed reads the outbox); a remote follower
        // needs a cross-instance delivery (the peer's AnnounceActivityHandler records it in the
        // follower's outbox on its instance).
        //
        // The deterministic IRI ({announcer}/announces/{objectIri}) is scoped to the actual announcer
        // (the activity's Actor), so the propagated form and the original announce share the same IRI.
        // The inbox processor's add-if-absent guard (the 19.3.1/19.3.2 re-delivery loop guard) stores the
        // original form under that IRI; the propagated form (to=follower, cc=announcer) is recorded in
        // the follower's outbox under the same IRI, so the activity store and follower outboxes reference
        // the same activity.
        var followers = await _persistence.Follows
            .GetFollowersAsync(delivery.RecipientIri, ct)
            .ConfigureAwait(false);
        foreach (var followerIri in followers)
        {
            // Decision 055 ("mint once at record-time, reuse for all deliveries"): the propagated form
            // reuses the ORIGINAL announce's id (minted once by the outbox write path, or the inbound
            // announce's originator id), not a re-derived one. This keeps the propagated form's IRI
            // identical to the original announce's IRI, so the activity store and follower outboxes
            // reference the same activity and a follower that stores by IRI dedupes the boost (one copy,
            // not one per delivery).
            var propagated = AnnounceIris.BuildAnnounce(new Iri(announce.Id!), announcerIri.Value, objectIri.Value, followerIri);

            if (await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
            {
                // A local follower sees the boost via the follower's outbox on this instance: record it
                // directly (no cross-instance delivery — the follower's followed-feed reads the outbox).
                await _persistence.Activities
                    .AddToOutboxAsync(followerIri, propagated, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // A remote follower needs a cross-instance delivery (the peer's handler records it in
                // the follower's outbox on its instance). DeliverToActorAsync derives the follower's
                // inbox from the follower's actor IRI; the delivery is signed as the recipient (the
                // announcer on the peer's instance).
                await _delivery
                    .DeliverToActorAsync(followerIri, propagated, delivery.RecipientIri, ct)
                    .ConfigureAwait(false);
            }
        }

        // F-06 (relay fan-out): deliver the boost to each relay the announcer has subscribed to (a
        // `star`-subscribed fan-out server, AP §5.1.3), signed as the announcer. A relay is a remote
        // fan-out server — always cross-instance — so no local-actor / block check is needed: the relay
        // is never a local actor, and a relay that has blocked the announcer is suppressed by
        // IDeliveryService.DeliverToActorAsync (F-07) before it is enqueued.
        await DeliverToSubscribedRelaysAsync(delivery.RecipientIri, announce, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 138.12 (Lemmy community relay): handles an embedded <see cref="Create"/> activity that is the
    /// object of an inbound <see cref="Announce"/> (Lemmy's community relay pattern: a community relays
    /// a member's post by sending an <c>Announce</c> whose object is the member's <c>Create</c>). Stores
    /// the embedded object in the object store and records the <see cref="Create"/> in the community's
    /// local members' outboxes (via <see cref="CommunityContentRecorder"/>), so the post surfaces in the
    /// community feed. The recipient is the community (the <c>Announce</c>'s actor, which is the
    /// relaying community's IRI).
    /// </summary>
    /// <param name="delivery">The inbound delivery (the recipient is the community whose inbox received
    /// the <c>Announce</c>).</param>
    /// <param name="embeddedCreate">The embedded <see cref="Create"/> activity (the member's post).</param>
    /// <param name="ct">A cancellation token.</param>
    private async Task HandleEmbeddedCreateAsync(InboxDelivery delivery, Create embeddedCreate, CancellationToken ct)
    {
        var recipient = delivery.RecipientIri;

        // Store the embedded object (the Page/Note) in the object store under its own IRI, so it can be
        // served by IRI and later refreshed (an Update) or tombstoned (a Delete).
        var embeddedObject = embeddedCreate.ExtractEmbeddedObject();
        if (embeddedObject is not null)
        {
            // 136.19 (re-animation guard): if the object's IRI already holds a Tombstone, do not
            // re-store the live content.
            var objectIriCheck = embeddedObject.ResolveObjectIri();
            if (objectIriCheck is { } oi
                && await _persistence.Objects.TryGetObjectAsync(oi, out var existing, ct).ConfigureAwait(false)
                && existing is Tombstone)
            {
                return;
            }

            if (embeddedObject.Published is null)
            {
                embeddedObject.Published = embeddedCreate.Published ?? DateTime.UtcNow;
            }

            await _persistence.Objects.PutObjectAsync(embeddedObject, ct).ConfigureAwait(false);
        }

        // Record the Create in the community's local members' outboxes (the "followed content" half of
        // the community feed). The CommunityContentRecorder handles the fan-out to local members.
        await CommunityContentRecorder.RecordToMembersAsync(
            _persistence,
            _localActors,
            recipient,
            embeddedCreate,
            ct).ConfigureAwait(false);
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
