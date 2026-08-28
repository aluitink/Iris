using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

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
/// <strong>Idempotency.</strong> The <see cref="IActivityStore.AddToOutboxAsync"/> does not de-duplicate by
/// IRI; a re-delivered <see cref="Create"/> with the same IRI is recorded again. The inbox pipeline is the
/// authority for delivery (a given activity is delivered once), so re-recording is not expected in the
/// normal path; a host that re-delivers should ensure idempotency at that layer.
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

                await _delivery
                    .DeliverToActorAsync(followerIri, activity, recipient, ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // A Create delivered to a local community's inbox is content a follower published: record it in
        // the community's local members' outboxes (the "followed content" half of the community feed).
        if (await _persistence.Communities
                .TryGetCommunityAsync(recipient, out _, ct)
                .ConfigureAwait(false))
        {
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
}
