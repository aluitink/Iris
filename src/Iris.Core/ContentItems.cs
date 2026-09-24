using KristofferStrube.ActivityStreams;

namespace Iris.Core;

/// <summary>
/// The single source of truth for "is this an ActivityStreams item a posts/feed view should render
/// as a content post?" S81: this classification was duplicated in four places that had drifted apart
/// (the Blazor UI's <c>OutboxFilter</c>, the home-feed private filters, the server's
/// <c>?type=content</c> outbox filter, and the server's per-actor <c>postsCount</c> counter); each
/// copy independently decided which object types count, so the home feed showed a Lemmy
/// cross-post (<c>Page</c>) while the profile "Your posts" tab and the post counter did not.
/// Every caller now funnels through <see cref="IsContentPost"/> so the set of content types lives in
/// exactly one place.
/// </summary>
public static class ContentItems
{
    /// <summary>
    /// Whether an ActivityStreams item is renderable content: a <c>Create</c> whose object is a
    /// <c>Note</c>, <c>Article</c>, <c>Page</c> (a Lemmy/community cross-post), or <c>Question</c>
    /// (a poll); an <c>Announce</c> (a boost); or a bare content object of those types (some servers
    /// publish the object directly rather than wrapped in a <c>Create</c>). Social and moderation
    /// activities (<c>Follow</c>, <c>Accept</c>, <c>Like</c>, <c>Flag</c>, …) and non-content objects
    /// (<c>Group</c>, a community join, a bare link) return <c>false</c>.
    /// </summary>
    public static bool IsContentPost(IObjectOrLink item)
    {
        if (item is Announce)
        {
            return true;
        }

        if (item is Create create)
        {
            if (create.Object is not { } objects)
            {
                return false;
            }

            foreach (var obj in objects)
            {
                if (obj is IObject o && IsContentObject(o))
                {
                    return true;
                }
            }

            return false;
        }

        return item is IObject bare && IsContentObject(bare);
    }

    /// <summary>
    /// Whether an outbox item is a <c>Create</c> of a content object that is a <em>reply</em> — i.e.
    /// the created object carries an <c>inReplyTo</c>. Used by the profile "Replies" tab (S104) via the
    /// server's <c>?type=reply</c> outbox filter so the tab receives only the user's own replies and
    /// does not have to page through the entire (mixed, social-activity-dominated) outbox to find them.
    /// An <c>Announce</c> of a reply is still a boost (not a reply the user authored), so it returns
    /// <c>false</c>; a bare reply object (a server that publishes the object unwrapped) is accepted.
    /// </summary>
    public static bool IsContentReply(IObjectOrLink item)
    {
        if (item is Create create && create.Object is { } objects)
        {
            foreach (var reference in objects)
            {
                if (reference is IObject obj && IsContentObject(obj) && HasInReplyTo(obj))
                {
                    return true;
                }
            }

            return false;
        }

        return item is IObject bare && IsContentObject(bare) && HasInReplyTo(bare);
    }

    private static bool HasInReplyTo(IObject obj)
        => obj.InReplyTo is { } inReplyTo && inReplyTo.Any();

    private static bool IsContentObject(IObject obj)
        => obj is Note or Article or Page or Question;
}
