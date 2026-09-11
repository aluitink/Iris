using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Client.Components;

/// <summary>
/// Shared outbox/feed item classification for the Blazor UI. An actor's outbox is a
/// <c>CollectionPage</c> of every activity the actor authored — a <c>Create</c> for each note, plus
/// social activities (<c>Follow</c>, <c>Accept</c>, <c>Reject</c>, <c>Like</c>, <c>Announce</c>,
/// <c>Undo</c>, <c>Delete</c>, and moderation activities such as <c>Flag</c> and <c>Block</c>). A
/// "posts" view (the profile's "Your posts" tab and another actor's "Posts" tab) should show only the
/// content items and filter the social/moderation activities out — rendering them with an
/// <c>&lt;ObjectView&gt;</c> (which expects a content object) produces an empty card with only a
/// timestamp.
/// </summary>
internal static class OutboxFilter
{
    /// <summary>
    /// Whether an outbox item is a content item — a <c>Create</c> whose object is a <c>Note</c> or
    /// <c>Article</c>. Social and moderation activities return <c>false</c>.
    /// </summary>
    public static bool IsContentItem(IObjectOrLink item)
    {
        if (item is not Create create)
        {
            return false;
        }

        if (create.Object is not { } objects)
        {
            return false;
        }

        foreach (var obj in objects)
        {
            if (obj is Note || obj is Article)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an outbox item is a content item (<see cref="IsContentItem"/>) **authored by a
    /// specific actor**. Used by the signed-in user's "Your posts" tab: the server mirrors
    /// followed (remote) content into the local actor's outbox as <c>Create</c> activities whose
    /// <c>actor</c> is the **remote** author, not the local user. Without this filter those foreign
    /// notes render in "Your posts" alongside the user's own (B-005). When <paramref name="authorIri"/>
    /// is <c>null</c> the author check is skipped and the behavior matches <see cref="IsContentItem"/>.
    /// </summary>
    public static bool IsOwnContentItem(IObjectOrLink item, Iri? authorIri)
    {
        if (!IsContentItem(item))
        {
            return false;
        }

        if (authorIri is null)
        {
            return true;
        }

        if (item is not Activity activity)
        {
            return false;
        }

        var actorId = activity.Actor?.FirstOrDefault()?.Id;
        var expected = authorIri.ToLibraryId();
        return string.Equals(actorId, expected, StringComparison.Ordinal);
    }
}
