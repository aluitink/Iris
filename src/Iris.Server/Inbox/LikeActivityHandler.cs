using Iris.Core;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Like"/> activities: records the like edge when the <em>liked object</em>
/// is stored locally (so the object's <c>/likes</c> collection and like count are served, regardless of
/// whether the liker is local or remote), and — when the recipient is a local community — records the
/// like in each of the community's local members' outboxes (so the like appears in the community feed,
/// mirroring the <see cref="CommunityInboxActivityHandler"/>).
/// </summary>
/// <remarks>
/// An inbound <c>Like</c> is an actor endorsing (liking) an object. The handler interprets two cases:
/// <list type="number">
/// <item><strong>Local liked object.</strong> When the activity's <c>object</c> is a content object
/// stored in this instance's <see cref="IObjectStore"/> (a <em>local</em> object), the directed like
/// edge <c>liker → object</c> is recorded in the <see cref="ILikeStore"/> so the object's <c>/likes</c>
/// collection (served at <c>GET {object-irI}/likes</c>) lists the like activity and the object's
/// <c>likeCount</c> reflects it. This is independent of the liker's locality: a <em>remote</em> actor's
/// like of a local object is recorded (the remote actor's like is the object's like, surfaced on the
/// object's own collection), as is a local actor's like of a local object. A like of a <em>remote</em>
/// object (not stored locally) is not recorded here: the edge is recorded on the object's <em>author's</em>
/// home instance instead (the object's <c>liked</c> collection is home-instance-local), so recording it
/// here would duplicate the edge.</item>
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
/// interpreted as a no-op: nothing is recorded. A like of a remote object is recorded on the object's
/// author's home instance (the liker's own <c>liked</c> collection there), not here.
/// </para>
public sealed class LikeActivityHandler : ActivityHandlerBase<Like>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="LikeActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ILikeStore"/>,
    /// <see cref="IObjectStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the recipient is a local actor.</param>
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

        // (1) Local liked object: record the like edge so the object's `/likes` collection and like
        // count reflect the like. This is independent of the liker's locality — a remote actor's like of
        // a local object is recorded here (the remote actor's like is the object's like, surfaced on the
        // object's own collection), as is a local actor's like of a local object. A like of a remote
        // object (not stored locally) is not recorded here: the edge is recorded on the object's author's
        // home instance instead (the object's `liked` collection is home-instance-local), so recording it
        // here would duplicate the edge.
        if (await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out _, ct).ConfigureAwait(false))
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
