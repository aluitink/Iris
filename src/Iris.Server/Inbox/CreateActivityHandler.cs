using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles an inbound <see cref="Create"/> activity: when the recipient is a local person, records the
/// <see cref="Create"/> in that person's outbox (so the author's post is surfaced in their own feed, J-8)
/// <em>and</em> federates it to the author's remote followers (J-18); when the recipient is a local
/// community, records it in the community's local members' outboxes (the "followed content" half of the
/// community feed, delegating to <see cref="CommunityContentRecorder"/>).
/// </summary>
/// <remarks>
/// The <see cref="InboxProcessor"/> prefers the most specific handler, so this dedicated
/// <see cref="Create"/>-typed handler intercepts every inbound <see cref="Create"/> before the
/// catch-all <see cref="CommunityInboxActivityHandler"/> (which now serves <see cref="Like"/> and
/// <see cref="Announce"/> to a community's inbox). The handler therefore owns the <em>recipient</em>
/// decision: a <see cref="Create"/> delivered to a local person's inbox is the person's own post (record
/// in the person's outbox, then federate to the author's remote followers); a <see cref="Create"/>
/// delivered to a local community's inbox is content a follower published (record in the community's
/// members' outboxes); anything else is a no-op (the activity is still stored by the processor, and an
/// unknown recipient's inbox 404s before the handler runs — the guard is a safety net).
/// </remarks>
/// <para>
/// <strong>Why the person's own outbox.</strong> A local member's client posts by delivering a signed
/// <see cref="Create"/> to the author's own inbox (the client cannot enumerate the follower set — that is
/// server-side state, and ActivityPub has no "post to my outbox" endpoint; outboxes are read collections).
/// Recording the <see cref="Create"/> in the author's outbox is what surfaces the post locally (J-8).
/// </para>
/// <para>
/// <strong>Outbound federation to the author's remote followers (J-18).</strong> After surfacing the post
/// locally, the handler federates it: for each of the author's <em>remote</em> followers it schedules the
/// author's own <see cref="Create"/> for delivery to that follower's inbox via
/// <see cref="IDeliveryService"/>'s actor-targeted delivery, signed as
/// <em>the author</em> (the follower's instance verifies it against the author's key). Only <em>remote</em>
/// followers are targeted: a local follower already sees the post in the author's outbox on <em>this</em>
/// instance (surfaced by J-8), so it needs no cross-instance delivery — the inverse of
/// <see cref="AnnounceActivityHandler"/>, which propagates a boost to <em>local</em> followers (a boost
/// originates remotely and must reach local readers). Delivery to a remote follower goes to that
/// follower's <em>delivery target</em>, which <see cref="IDeliveryService.DeliverToActorAsync(Iri, Activity, CancellationToken)"/>
/// resolves as the follower's advertised <c>endpoints.sharedInbox</c> when its document advertises one
/// (F-01) and otherwise as the follower's own inbox. The follower set is this instance's server-side
/// state (<see cref="IFollowStore.GetFollowersAsync(Iri, CancellationToken)"/>), which is exactly why
/// federation must happen here (server-side) and not in the client.
/// </para>
/// <para>
/// <strong>Relay fan-out (F-06).</strong> After federating to the author's remote followers, the handler
/// also delivers the post to each <em>relay</em> the author has subscribed to (the author's
/// <c>relays</c> / <c>star</c> set, AP §5.1.3), signed as the author. A relay is a remote fan-out server,
/// so — unlike the follower loop — no local-actor skip is needed (a relay is never a local actor), and a
/// relay that has blocked the author is suppressed by <see cref="IDeliveryService"/> (F-07) before it is
/// enqueued. This is the delivery half of the relay feature; the subscription half (the local
/// <c>relays</c> collection) is recorded by the Basic-authenticated relay endpoint (Slice 12.18).
/// </para>
/// <para>
/// <strong>Idempotency / loop safety.</strong> The <see cref="IInboxProcessor"/> de-duplicates by the
/// activity's IRI (C-07): a re-delivered <see cref="Create"/> (a retry, a restart replay, or — for mutual
/// follows — a peer's re-fan-out echo) is stored as a no-op and is <em>not</em> re-dispatched to this
/// handler. That guard is what stops the two-instance re-delivery loop (19.3.1/19.3.2): only the first
/// delivery of a Create reaches this handler, so the post is federated to the author's remote followers
/// exactly once, never re-fan-out. The outbox write itself (<see cref="IActivityStore.AddToOutboxAsync"/>)
/// is also idempotent by IRI (F-1911-2), so a post appears in the outbox exactly once.
/// </para>
public sealed class CreateActivityHandler : ActivityHandlerBase<Create>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IDeliveryService _delivery;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="CreateActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IActivityStore"/>,
    /// <see cref="IFollowStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the author's <see cref="Create"/> to each of
    /// the author's remote followers' inboxes, signed as the author).</param>
    /// <param name="localActors">Resolves whether the recipient (and each candidate follower/member) is a
    /// local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public CreateActivityHandler(
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
    public override async Task HandleAsync(InboxDelivery delivery, Create activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        var recipient = delivery.RecipientIri;

        // A Create delivered to a local person's inbox is the person's own post: record it in the
        // person's outbox so it is surfaced in the author's own feed (J-8). The person and community
        // stores are disjoint (a community lives in the community store, not the actor store), so a
        // recipient is either a local person, a local community, or neither.
        if (await _localActors.IsLocalActorAsync(recipient, ct).ConfigureAwait(false))
        {
            // Store the embedded object in the object store so it can be served by IRI, refreshed by an
            // Update, and tombstoned by a Delete (F-02/F-03). A Create without an embedded object (a
            // bare link reference) stores nothing here.
            await StoreEmbeddedObjectAsync(activity, ct).ConfigureAwait(false);

            await _persistence.Activities
                .AddToOutboxAsync(recipient, activity, ct)
                .ConfigureAwait(false);

            // Then federate the post to the author's remote followers (J-18). A local follower already
            // sees the post in the author's outbox on this instance, so only remote followers need a
            // cross-instance delivery. Each is delivered to its own inbox, signed as the author.
            var followers = await _persistence.Follows
                .GetFollowersAsync(recipient, ct)
                .ConfigureAwait(false);
            foreach (var followerIri in followers)
            {
                if (await _localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
                {
                    // A local follower sees the post via the author's outbox; skip (no cross-instance
                    // delivery needed).
                    continue;
                }

                // F-07 (apply the block edge): a remote follower who has blocked the author does not
                // want the author's content — skip the delivery (the follower's own block edge, recorded
                // when its Block arrived in the author's inbox, is read here).
                if (await _persistence.Moderation
                        .IsBlockedAsync(followerIri, recipient, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                await _delivery
                    .DeliverToActorAsync(followerIri, activity, recipient, ct)
                    .ConfigureAwait(false);
            }

            // F-06 (relay fan-out): deliver the post to each relay the author has subscribed to (a
            // `star`-subscribed fan-out server, AP §5.1.3), signed as the author. A relay is a remote
            // fan-out server — always cross-instance — so no local-actor / block check is needed: the
            // relay is never a local actor, and a relay that has blocked the author is suppressed by
            // IDeliveryService.DeliverToActorAsync (F-07) before it is enqueued.
            await DeliverToSubscribedRelaysAsync(recipient, activity, ct).ConfigureAwait(false);

            return;
        }

        // A Create delivered to a local community's inbox is content a follower published: record it in
        // the community's local members' outboxes (the "followed content" half of the community feed).
        if (await _persistence.Communities
                .TryGetCommunityAsync(recipient, out _, ct)
                .ConfigureAwait(false))
        {
            // Store the embedded object (shared with the person branch) so followed-community content is
            // also served by IRI and can be updated/deleted.
            await StoreEmbeddedObjectAsync(activity, ct).ConfigureAwait(false);
            await CommunityContentRecorder.RecordToMembersAsync(
                _persistence,
                _localActors,
                recipient,
                activity,
                ct).ConfigureAwait(false);
            return;
        }

        // Neither a local person nor a local community: no-op. The activity is still stored by the
        // processor; an unknown recipient's inbox 404s before the handler runs, so this is a safety net.
    }

    /// <summary>
    /// Stores the <see cref="Create"/>'s embedded object in the object store under its own IRI, so it
    /// can be served by IRI and later refreshed (an <see cref="Update"/>) or tombstoned (a
    /// <see cref="Delete"/>). A <see cref="Create"/> whose object is a bare link reference stores nothing.
    /// </summary>
    private async Task StoreEmbeddedObjectAsync(Create activity, CancellationToken ct)
    {
        var embedded = activity.ExtractEmbeddedObject();
        if (embedded is not null)
        {
            await _persistence.Objects.PutObjectAsync(embedded, ct).ConfigureAwait(false);

            // Decision 055: record the object → Create link so a later Delete can resolve this object's
            // originating Create by lookup (the object's ULID and its Create's ULID are independent — the
            // Create IRI can no longer be derived from the object IRI). A Create with a bare-link object
            // (no embedded object id) records no link.
            var objectIri = embedded.ResolveObjectIri();
            if (objectIri is { } obj && activity.Id is { } createId)
            {
                await _persistence.Creates
                    .RecordAsync(obj, new Iri(createId), ct)
                    .ConfigureAwait(false);
            }

            // F-12 threading: when the stored object is a reply (its inReplyTo is set), record the
            // parent → child edge so the parent's replies collection ({object}/replies) lists this
            // reply. A top-level object (no inReplyTo) records no edge. The edge is recorded in both
            // the person and community branches (a reply to a community-posted note threads there too).
            var parentIri = embedded.GetParentIri();
            if (parentIri is { } parent && objectIri is { } child)
            {
                await _persistence.Replies
                    .RecordReplyAsync(parent, child, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// F-06 (relay fan-out): delivers <paramref name="activity"/> to each relay <paramref name="authorIri"/>
    /// has subscribed to (the actor's <c>relays</c> / <c>star</c> set), signed as the author. A relay is a
    /// remote <c>star</c>-subscribed fan-out server (AP §5.1.3); this is the delivery half of the relay
    /// feature (the subscription half — recording the <c>actor → relay</c> edge — is the local
    /// <c>relays</c> collection, Slice 12.18). When the author has no subscribed relays, nothing is
    /// scheduled. A relay that has blocked the author is suppressed by
    /// <see cref="IDeliveryService.DeliverToActorAsync(Iri, Activity, Iri?, CancellationToken)"/> (F-07)
    /// before it is enqueued.
    /// </summary>
    /// <param name="authorIri">The author of the content (the signing actor of the relay delivery).</param>
    /// <param name="activity">The activity to fan out (the author's <see cref="Create"/> or
    /// <see cref="Announce"/>).</param>
    /// <param name="ct">A cancellation token.</param>
    private async Task DeliverToSubscribedRelaysAsync(Iri authorIri, Activity activity, CancellationToken ct)
    {
        var relays = await _persistence.Relays
            .GetRelaysAsync(authorIri, ct)
            .ConfigureAwait(false);
        foreach (var relayIri in relays)
        {
            await _delivery
                .DeliverToActorAsync(relayIri, activity, authorIri, ct)
                .ConfigureAwait(false);
        }
    }
}
