using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Like"/> activities: records the like edge when the liker is a local
/// actor (so the actor's <c>liked</c> collection can be served), and — when the recipient is a local
/// community — records the like in each of the community's local members' outboxes (so the like
/// appears in the community feed, mirroring the <see cref="CommunityInboxActivityHandler"/>).
/// </summary>
/// <remarks>
/// An inbound <c>Like</c> is an actor endorsing (liking) an object. The handler interprets two cases:
/// <list type="number">
/// <item><strong>Local liker.</strong> When the activity's <c>actor</c> (the liker) is a <em>local</em>
/// actor, the directed like edge <c>liker → object</c> is recorded in the <see cref="ILikeStore"/> so
/// the liker's <c>liked</c> collection (served at <c>GET /ap/v1/u/{handle}/liked</c>) lists the liked
/// object. This is independent of the recipient: a local actor's like of a remote object is recorded
/// regardless of which inbox it was delivered to.</item>
/// <item><strong>Local community recipient.</strong> When the recipient is a local community (the like
/// was delivered to the community's inbox), the like is recorded in each of the community's local
/// members' outboxes (delegating to <see cref="CommunityContentRecorder.RecordToMembersAsync"/>, the
/// same member-recording loop the <see cref="CommunityInboxActivityHandler"/> and
/// <see cref="CreateActivityHandler"/> use) so the like appears in the community feed. Previously the
/// catch-all <see cref="CommunityInboxActivityHandler"/> owned community <c>Like</c>s; now this specific
/// handler intercepts them first (the <see cref="InboxProcessor"/> prefers the most specific type), so
/// it must delegate to the same member-recording path to avoid regressing community likes.</item>
/// </list>
/// </remarks>
/// <para>
/// A malformed like (no resolvable actor or no resolvable object) is stored (by the processor) but
/// interpreted as a no-op: nothing is recorded. A remote liker whose like arrives in a local actor's
/// inbox is not recorded in the local actor's <c>liked</c> collection (the <c>liked</c> collection
/// lists objects <em>this</em> actor liked, not objects others liked of this actor).
/// </para>
public sealed class LikeActivityHandler : ActivityHandlerBase<Like>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="LikeActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ILikeStore"/> and
    /// <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the liker (and the recipient) is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public LikeActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Like like, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(like);

        // The liker is the activity's actor (Rule 3: read multi-valued as IEnumerable, null-safe).
        var likerIri = like.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!likerIri.HasValue)
        {
            // A like with no resolvable actor is malformed; nothing to record. The activity is still
            // stored (by the processor) so it can be inspected.
            return;
        }

        // The liked object is the activity's object (Rule 3: read multi-valued as IEnumerable, null-safe).
        var objectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            // A like with no resolvable object is malformed; nothing to record.
            return;
        }

        // (1) Local liker: record the like edge so the liker's `liked` collection lists the object. This
        // is independent of the recipient — a local actor's like of a remote object is recorded regardless
        // of which inbox the like was delivered to.
        if (await _localActors.IsLocalActorAsync(likerIri.Value, ct).ConfigureAwait(false))
        {
            await _persistence.Likes
                .RecordLikeAsync(likerIri.Value, objectIri.Value, ct)
                .ConfigureAwait(false);
        }

        // (2) Local community recipient: record the like in each of the community's local members'
        // outboxes so it appears in the community feed. Previously the catch-all
        // CommunityInboxActivityHandler owned community Likes; this specific handler intercepts them
        // first, so it delegates to the same member-recording path to avoid regressing community likes.
        if (await _persistence.Communities
                .TryGetCommunityAsync(delivery.RecipientIri, out _, ct)
                .ConfigureAwait(false))
        {
            await CommunityContentRecorder.RecordToMembersAsync(
                _persistence,
                _localActors,
                delivery.RecipientIri,
                like,
                ct).ConfigureAwait(false);
        }
    }
}
