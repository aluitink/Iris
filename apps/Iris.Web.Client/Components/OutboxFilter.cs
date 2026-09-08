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
}
