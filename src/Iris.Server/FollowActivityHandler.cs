using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Follow"/> activities: records the follow edge in the
/// <see cref="IFollowStore"/> and delivers an <see cref="Accept"/> back to the follower's inbox.
/// </summary>
/// <remarks>
/// An inbound <c>Follow</c> is a remote actor asking to follow a local actor. The handler:
/// <list type="number">
/// <item>Records the directed edge <c>follower → recipient</c>: the follower is the activity's
/// <c>actor</c>, and the target is the delivery's <see cref="InboxDelivery.RecipientIri"/> (the inbox
/// the follow was delivered to — authoritative for the target).</item>
/// <item>Constructs an <c>Accept</c> (actor = the local actor being followed, object = the original
/// follow) and schedules it for delivery to the follower's inbox via <see cref="IDeliveryService"/>
/// (asynchronous — the handler returns after enqueuing; the <see cref="DeliveryWorker"/> POSTs it,
/// signed as the instance actor, once the worker pumps the queue).</item>
/// </list>
/// </remarks>
public sealed class FollowActivityHandler : ActivityHandlerBase<Follow>
{
    private readonly IPersistenceProvider _persistence;
    private readonly IDeliveryService _delivery;

    /// <summary>
    /// Initializes a new <see cref="FollowActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>).</param>
    /// <param name="delivery">The delivery service (schedules the <c>Accept</c> response).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> or
    /// <paramref name="delivery"/> is null.</exception>
    public FollowActivityHandler(IPersistenceProvider persistence, IDeliveryService delivery)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);
        _persistence = persistence;
        _delivery = delivery;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Follow follow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(follow);

        // The follower is the activity's actor (Rule 3: read multi-valued as IEnumerable, null-safe).
        // The actor is an IObjectOrLink: either a Link (Href) or an embedded Object (Id).
        var followerIri = ResolveIri(follow.Actor?.FirstOrDefault());
        if (!followerIri.HasValue)
        {
            // A follow with no resolvable actor is malformed; nothing to record. The activity is
            // still stored (by the processor) so it can be inspected.
            return;
        }

        // Record the directed follow edge: follower → recipient (the inbox the follow was delivered to).
        await _persistence.Follows
            .RecordFollowAsync(followerIri.Value, delivery.RecipientIri, ct)
            .ConfigureAwait(false);

        // Respond to the follow: construct an Accept (actor = the local actor being followed, which is
        // delivery.RecipientIri — the actor IRI, per InboxDelivery's contract; object = the original
        // follow) and schedule it for delivery to the follower's inbox. DeliverToActorAsync derives the
        // follower's inbox from the follower's actor IRI.
        var accept = BuildAccept(delivery.RecipientIri, follow);
        await _delivery
            .DeliverToActorAsync(followerIri.Value, accept, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <see cref="Accept"/> response to a follow: the local actor accepts the original
    /// follow activity. The <c>Accept</c>'s IRI is derived deterministically from the local actor and
    /// the original follow (a stable, idempotent IRI — re-delivering the same follow yields the same
    /// <c>Accept</c>, so a receiver can deduplicate).
    /// </summary>
    /// <param name="localActorIri">The IRI of the local actor being followed (the Accept's actor).</param>
    /// <param name="follow">The original follow activity (the Accept's object).</param>
    /// <returns>The constructed <see cref="Accept"/>.</returns>
    private static Accept BuildAccept(Iri localActorIri, Follow follow) => new()
    {
        Id = $"{localActorIri}/accepts/{follow.Id}",
        Actor = [new Link { Href = new Uri(localActorIri.Value) }],
        Object = [new Link { Href = new Uri(follow.Id!) }],
    };

    /// <summary>
    /// Resolves the IRI of an <see cref="IObjectOrLink"/>: a <see cref="Link"/> contributes its
    /// <c>Href</c>; an embedded object contributes its <c>Id</c>. Returns null when neither is set.
    /// </summary>
    private static Iri? ResolveIri(IObjectOrLink? objOrLink)
    {
        if (objOrLink is ILink { Href: { } href })
        {
            return new Iri(href);
        }

        if (objOrLink is IObject { Id: { Length: > 0 } id })
        {
            return new Iri(id);
        }

        return null;
    }
}
