using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Records a content activity in each of a local community's local members' outboxes, so the content
/// appears in the community's unified feed (the <see cref="ICommunityFeedService"/> merges the members'
/// outboxes).
/// </summary>
/// <remarks>
/// This is the single source of truth for "record content in a community's members' outboxes." It is
/// shared by the community inbox handler (which records <see cref="Like"/> and <see cref="Announce"/>
/// content delivered to a community's inbox) and the <see cref="CreateActivityHandler"/> (which records a
/// <see cref="Create"/> delivered to a community's inbox, now that the <c>Create</c> dispatch is owned by a
/// dedicated handler). Keeping the member-recording loop in one place avoids the two paths diverging.
/// </remarks>
internal static class CommunityContentRecorder
{
    /// <summary>
    /// Records the activity in each of the community's local members' outboxes (newest first). Remote
    /// members are skipped (their instance records the content via its own federation path).
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IActivityStore"/>).</param>
    /// <param name="localActors">Resolves whether each candidate member is a local actor.</param>
    /// <param name="communityIri">The IRI of the community whose members' outboxes are updated.</param>
    /// <param name="activity">The content activity to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been recorded in every local member's outbox.</returns>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    internal static async Task RecordToMembersAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri communityIri,
        Activity activity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        ArgumentNullException.ThrowIfNull(activity);

        var memberIris = await persistence.Communities
            .GetMembersAsync(communityIri, ct)
            .ConfigureAwait(false);
        foreach (var memberIri in memberIris)
        {
            // Only record for local members (their outboxes are the local activity store); a remote
            // member is the remote instance's concern.
            if (!await localActors.IsLocalActorAsync(memberIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            await persistence.Activities
                .AddToOutboxAsync(memberIri, activity, ct)
                .ConfigureAwait(false);
        }
    }
}
