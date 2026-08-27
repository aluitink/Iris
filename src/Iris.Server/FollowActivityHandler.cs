using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Follow"/> activities: records the follow edge in the
/// <see cref="IFollowStore"/>.
/// </summary>
/// <remarks>
/// An inbound <c>Follow</c> is a remote actor asking to follow a local actor. The handler records the
/// directed edge <c>follower → recipient</c>: the follower is the activity's <c>actor</c>, and the
/// target is the delivery's <see cref="InboxDelivery.RecipientIri"/> (the inbox the follow was
/// delivered to — authoritative for the target). Recording the edge is the interpretation step; the
/// <em>response</em> to the follow (an <c>Accept</c> delivered back to the follower's inbox) is the
/// delivery slice (the <c>IDeliveryService</c> roadmap item) and is not sent here.
/// </remarks>
public sealed class FollowActivityHandler : ActivityHandlerBase<Follow>
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="FollowActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/>).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public FollowActivityHandler(IPersistenceProvider persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
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
    }

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
