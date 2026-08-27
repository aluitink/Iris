using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Follow"/> activities: records the follow edge in the
/// <see cref="IFollowStore"/> (when the recipient is a local actor) and delivers an
/// <see cref="Accept"/> back to the follower's inbox.
/// </summary>
/// <remarks>
/// An inbound <c>Follow</c> is a remote actor asking to follow a local actor. The handler:
/// <list type="number">
/// <item>Records the directed edge <c>follower → recipient</c>: the follower is the activity's
/// <c>actor</c>, and the target is the delivery's <see cref="InboxDelivery.RecipientIri"/> (the inbox
/// the follow was delivered to — authoritative for the target). The edge is recorded only when the
/// recipient is a <em>local</em> actor (a follow addressed to a remote actor is not this instance's
/// concern).</item>
/// <item>Constructs an <c>Accept</c> (actor = the local actor being followed, object = the original
/// follow) and schedules it for delivery to the follower's inbox via <see cref="IDeliveryService"/>
/// (asynchronous — the handler returns after enqueuing; the <see cref="DeliveryWorker"/> POSTs it,
/// signed as the instance actor, once the worker pumps the queue). When the follower's
/// <see cref="AcceptActivityHandler"/> receives it, the follower finalizes its own copy of the follow
/// edge — so on the follower side the edge is provisional until accepted (Resolved Decision: the
/// follow lifecycle is two-sided; the followed side's acceptance drives the follower's state).</item>
/// </list>
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
        var followerIri = FollowIris.ResolveActorIri(follow.Actor?.FirstOrDefault());
        if (!followerIri.HasValue)
        {
            // A follow with no resolvable actor is malformed; nothing to record. The activity is
            // still stored (by the processor) so it can be inspected.
            return;
        }

        // Interpret the follow only when the recipient is a local actor (the inbox the follow was
        // delivered to belongs to this instance). A follow addressed to a remote actor is not this
        // instance's concern.
        if (!await _localActors.IsLocalActorAsync(delivery.RecipientIri, ct).ConfigureAwait(false))
        {
            return;
        }

        // Record the directed follow edge: follower → recipient (the inbox the follow was delivered to).
        await _persistence.Follows
            .RecordFollowAsync(followerIri.Value, delivery.RecipientIri, ct)
            .ConfigureAwait(false);

        // Respond to the follow: construct an Accept (actor = the local actor being followed, which is
        // delivery.RecipientIri — the actor IRI, per InboxDelivery's contract; object = the original
        // follow) and schedule it for delivery to the follower's inbox. DeliverToActorAsync derives the
        // follower's inbox from the follower's actor IRI; the delivery is signed as the local actor
        // being followed (the Accept's actor), so the remote verifies it against that actor's key.
        var accept = FollowIris.BuildAccept(delivery.RecipientIri, follow);
        await _delivery
            .DeliverToActorAsync(followerIri.Value, accept, delivery.RecipientIri, ct)
            .ConfigureAwait(false);
    }
}
