using Iris.Core;
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
    /// Whether an outbox item is a content item — a <c>Create</c> whose object is a <c>Note</c>,
    /// <c>Article</c>, <c>Page</c> (Lemmy cross-post), or <c>Question</c> (poll), an <c>Announce</c>
    /// (boost), or a bare content object. Social and moderation activities return <c>false</c>. S81:
    /// delegates to the single shared <see cref="Iris.Core.ContentItems.IsContentPost"/> so the
    /// content-type set cannot drift from the home feed or the server.
    /// </summary>
    public static bool IsContentItem(IObjectOrLink item)
        => ContentItems.IsContentPost(item);

    /// <summary>
    /// Whether an outbox item is a content item (<see cref="IsContentItem"/>) **authored by a
    /// specific actor**. Used by the signed-in user's "Your posts" tab: the server mirrors
    /// followed (remote) content into the local actor's outbox as <c>Create</c> activities whose
    /// <c>actor</c> is the **remote** author, not the local user. Without this filter those foreign
    /// notes render in "Your posts" alongside the user's own (B-005). For <c>Announce</c> items the
    /// check applies to the booster (the <c>actor</c> of the Announce). When <paramref name="authorIri"/>
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

        var actorId = ResolveActorIri(activity.Actor);
        var expected = authorIri.ToLibraryId();
        return string.Equals(actorId, expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an outbox item is a content <em>reply</em> (a <c>Create</c> of a Note/Article/Page/
    /// Question with an <c>inReplyTo</c>) authored by a specific actor — the profile "Replies" tab
    /// (S104). The server now serves these via <c>?type=reply</c> (so the tab need not page through the
    /// whole outbox); this author-scoped check is the client-side counterpart that keeps the tab
    /// showing the user's own replies (a bare content check would also surface mirrored remote replies).
    /// When <paramref name="authorIri"/> is <c>null</c> the author check is skipped.
    /// </summary>
    public static bool IsOwnContentReply(IObjectOrLink item, Iri? authorIri)
    {
        if (!IsContentReply(item))
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

        var actorId = ResolveActorIri(activity.Actor);
        var expected = authorIri.ToLibraryId();
        return string.Equals(actorId, expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an outbox item is a content reply (a <c>Create</c> of a Note/Article/Page/Question with an
    /// <c>inReplyTo</c>). Delegates to the shared <see cref="ContentItems.IsContentReply"/> so the reply
    /// set cannot drift from the server's <c>?type=reply</c> filter.
    /// </summary>
    public static bool IsContentReply(IObjectOrLink item)
        => ContentItems.IsContentReply(item);

    /// <summary>
    /// Resolves the IRI of the first resolvable actor reference in an ActivityStreams actor
    /// collection. An <c>actor</c> on a wire activity is a bare IRI string (a <see cref="ILink"/>)
    /// in the common case — e.g. Iris's own server emits <c>"actor": "https://…/u/andrew"</c> — but
    /// may also be an embedded object (a <see cref="IObject"/> carrying an <c>id</c>). Both shapes are
    /// accepted so the comparison is correct regardless of how the server serialized the actor
    /// (B-005 / 93.2: reading only <c>.Id</c> silently returned null for the bare-IRI shape and
    /// filtered out every own post).
    /// </summary>
    private static string? ResolveActorIri(IEnumerable<IObjectOrLink>? refs)
    {
        if (refs is null)
        {
            return null;
        }

        foreach (var reference in refs)
        {
            if (reference is ILink { Href: { } href })
            {
                return href.ToString();
            }

            if (reference is IObject { Id: { Length: > 0 } id })
            {
                return id;
            }
        }

        return null;
    }
}
