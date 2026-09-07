using Iris.Core;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

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
    /// <remarks>
    /// 40.3: Before recording, a community-tagged copy of the activity is created (the embedded note's
    /// <c>attributedTo</c> carries the community IRI). The tagged copy is what is recorded in the
    /// members' outboxes, so the community feed's <c>attributedTo</c> filter includes it. This is
    /// important for followed-community content (delivered to the community inbox) where the note's
    /// <c>attributedTo</c> may only carry the remote community's IRI, not the local community's.
    /// </remarks>
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

        // 40.3: create a community-tagged copy of the activity (the note's attributedTo carries the
        // community IRI) so the feed filter includes it. The original activity is left unmodified.
        var taggedActivity = TagActivityForCommunity(activity, communityIri);

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
                .AddToOutboxAsync(memberIri, taggedActivity, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a copy of the activity with the community IRI added to the embedded note's
    /// <c>attributedTo</c>. If the note already has the community in its <c>attributedTo</c>, the
    /// original activity is returned (no copy needed). For activities without an embedded note
    /// (e.g. a bare link), the original activity is returned as-is.
    /// </summary>
    private static Activity TagActivityForCommunity(Activity activity, Iri communityIri)
    {
        // If the activity has no embedded object (a bare link), return it as-is.
        if (activity.Object is not { } objects || !objects.Any())
        {
            return activity;
        }

        // Check if any embedded object already has the community in its attributedTo.
        var needsTagging = false;
        foreach (var obj in activity.Object)
        {
            if (obj is IObject iObj && iObj is ActivityObject activityObject)
            {
                if (!HasCommunityInAttributedTo(activityObject, communityIri))
                {
                    needsTagging = true;
                    break;
                }
            }
        }

        if (!needsTagging)
        {
            return activity;
        }

        // For a Create, create a new Create with a tagged copy of the embedded note.
        if (activity is Create create)
        {
            var newObjects = new List<IObjectOrLink>();
            foreach (var obj in create.Object ?? [])
            {
                if (obj is Note note)
                {
                    newObjects.Add(TagNote(note, communityIri));
                }
                else
                {
                    newObjects.Add(obj);
                }
            }

            return new Create
            {
                Id = create.Id,
                Actor = create.Actor,
                Object = newObjects,
                Published = create.Published,
                To = create.To,
                Cc = create.Cc,
            };
        }

        // For other activity types (Announce, Like, etc.), return as-is (the filter handles them).
        return activity;
    }

    /// <summary>
    /// Creates a copy of the note with the community IRI added to its <c>attributedTo</c>.
    /// </summary>
    private static Note TagNote(Note note, Iri communityIri)
    {
        var attributedTo = note.AttributedTo is null
            ? new List<IObjectOrLink> { new Link { Href = new Uri(communityIri.Value) } }
            : note.AttributedTo.Concat([new Link { Href = new Uri(communityIri.Value) }]).ToList();

        return new Note
        {
            Id = note.Id,
            Content = note.Content,
            AttributedTo = attributedTo,
            Published = note.Published,
            To = note.To,
            Cc = note.Cc,
        };
    }

    /// <summary>
    /// Returns true when the object's <c>attributedTo</c> collection contains the community IRI.
    /// </summary>
    private static bool HasCommunityInAttributedTo(ActivityObject obj, Iri communityIri)
    {
        var attributedTo = obj.AttributedTo;
        if (attributedTo is null)
        {
            return false;
        }

        foreach (var attr in attributedTo)
        {
            if (attr.ResolveObjectIri() is { } iri &&
                string.Equals(iri.Value, communityIri.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
