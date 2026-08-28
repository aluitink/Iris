using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles an inbound <see cref="Create"/> activity: when the recipient is a local person, records the
/// <see cref="Create"/> in that person's outbox (so the author's post is surfaced in their own feed); when
/// the recipient is a local community, records it in the community's local members' outboxes (the
/// "followed content" half of the community feed, delegating to <see cref="CommunityContentRecorder"/>).
/// </summary>
/// <remarks>
/// The <see cref="InboxProcessor"/> prefers the most specific handler, so this dedicated
/// <see cref="Create"/>-typed handler intercepts every inbound <see cref="Create"/> before the
/// catch-all <see cref="CommunityInboxActivityHandler"/> (which now serves <see cref="Like"/> and
/// <see cref="Announce"/> to a community's inbox). The handler therefore owns the <em>recipient</em>
/// decision: a <see cref="Create"/> delivered to a local person's inbox is the person's own post (record
/// in the person's outbox); a <see cref="Create"/> delivered to a local community's inbox is content a
/// follower published (record in the community's members' outboxes); anything else is a no-op (the
/// activity is still stored by the processor, and an unknown recipient's inbox 404s before the handler
/// runs — the guard is a safety net).
/// </remarks>
/// <para>
/// <strong>Why the person's own outbox.</strong> A local member's client posts by delivering a signed
/// <see cref="Create"/> to the author's own inbox (the client cannot enumerate the follower set — that is
/// server-side state, and ActivityPub has no "post to my outbox" endpoint; outboxes are read collections).
/// Recording the <see cref="Create"/> in the author's outbox is what surfaces the post locally (J-8). The
/// outbound federation of the post to the author's remote followers (J-18) is a separate, later concern
/// (a host may add a delivery step); this handler makes the post appear in the author's own feed first.
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
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="CreateActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IActivityStore"/> and
    /// <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the recipient (and each candidate member) is a local
    /// actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public CreateActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
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
