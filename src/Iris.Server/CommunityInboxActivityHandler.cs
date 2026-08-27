using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound content activities delivered to a community's inbox (<see cref="Create"/>,
/// <see cref="Like"/>, <see cref="Announce"/>): records the content in each of the community's local
/// members' outboxes so the content appears in the community's unified feed.
/// </summary>
/// <remarks>
/// A community is followed by remote actors (or by other communities) via the federation path: the
/// follower delivers the follower's own content activities to the community's inbox
/// (<c>POST /ap/v1/c/{name}/inbox</c>). Those activities are stored in the community's activity store by
/// the <see cref="InboxProcessor"/> (the stored activity is the community's record of "this content
/// arrived"), and this handler records each content activity in the community's local members' outboxes:
/// for every local member it calls <see cref="IActivityStore.AddToOutboxAsync"/>, so the activity is in
/// the member's outbox (newest first). The <see cref="ICommunityFeedService"/> (which merges the
/// members' outboxes) then surfaces it in the community feed. This is the "followed content" half of the
/// unified feed: the content delivered to the community by the actors it follows, alongside the members'
/// own posts.
/// </remarks>
/// <para>
/// <strong>Local members only, recorded directly.</strong> Members are local actors, so their outboxes
/// are the local activity store: the content is recorded directly (synchronously) in each member's
/// outbox, mirroring <see cref="AnnounceActivityHandler"/> recording a boost in the announcer's outbox.
/// This is done instead of scheduling a delivery to the member's inbox (which would be circular — the
/// member is local, and the feed reads the outbox, not a re-delivered inbox copy). The community must
/// exist locally (an unknown community's inbox 404s before the handler runs). A content activity with no
/// resolvable object is still recorded (the activity is stored by the processor; the outbox entry stands
/// on its own).
/// </para>
public sealed class CommunityInboxActivityHandler : ActivityHandlerBase<Activity>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="CommunityInboxActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="ICommunityStore"/>
    /// and <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether each candidate member is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public CommunityInboxActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(activity);

        // Interpret the content only when the recipient is a local community (the inbox the content was
        // delivered to belongs to a local community). A content activity addressed to a remote community
        // is not this instance's concern. The community inbox endpoint 404s before this handler runs when
        // the community is unknown, so this guard is a safety net (a non-community recipient is a no-op).
        if (!await _persistence.Communities
                .TryGetCommunityAsync(delivery.RecipientIri, out _, ct)
                .ConfigureAwait(false))
        {
            return;
        }

        // Record the content in each of the community's local members' outboxes (newest first), so the
        // ICommunityFeedService (which merges the members' outboxes) surfaces it in the community feed.
        var memberIris = await _persistence.Communities
            .GetMembersAsync(delivery.RecipientIri, ct)
            .ConfigureAwait(false);
        foreach (var memberIri in memberIris)
        {
            // Only record for local members (their outboxes are the local activity store); a remote
            // member is the remote instance's concern (that instance receives the content via its own
            // federation path).
            if (!await _localActors.IsLocalActorAsync(memberIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            await _persistence.Activities
                .AddToOutboxAsync(memberIri, activity, ct)
                .ConfigureAwait(false);
        }
    }
}
