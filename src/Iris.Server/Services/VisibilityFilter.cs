using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// The audience/visibility predicate for the instance's read surfaces (public feed, global search,
/// and — later — the follow feed and object-document endpoints). Iris models visibility purely with
/// the ActivityStreams <c>to</c>/<c>cc</c> audience, the same convention the platform uses for
/// delivery; there is no first-class "visibility" column. This helper centralizes the single rule
/// every read surface must apply so a non-public (followers-only or direct) post is not surfaced to
/// users who are not an intended recipient.
/// </summary>
/// <remarks>
/// The rule (matching Mastodon / ActivityPub convention):
/// <list type="bullet">
/// <item>
/// <description>A post is <b>public</b> when its <c>to</c>/<c>cc</c> contains the public-audience
/// sentinel (<c>as:Public</c>), <b>or</b> when it has no named audience at all (a note with no
/// <c>to</c>/<c>cc</c> is public by convention — the codebase's existing
/// <c>IsFollowReply</c> heuristic relies on the same "no audience ⇒ public" rule).</description>
/// </item>
/// <item>
/// <description>A post that names an audience but is <b>not</b> public (followers-only or a direct
/// message) is visible only to a requester who is one of its named recipients (appears in
/// <c>to</c>/<c>cc</c>).</description>
/// </item>
/// </list>
/// Feed items are <see cref="Activity"/>s (a <c>Create</c>/<c>Announce</c>) whose audience lives on
/// the embedded content object, so <see cref="IsFeedItemVisibleTo"/> extracts the embedded object
/// (or the item itself, when it is a bare object) and applies the rule to it.
/// </remarks>
internal static class VisibilityFilter
{
    /// <summary>
    /// Reports whether a content object is visible to a given (possibly anonymous) requester.
    /// </summary>
    /// <param name="obj">The content object whose <c>to</c>/<c>cc</c> audience is checked. May be
    /// null — a null object is treated as visible (no audience information to contradict
    /// visibility, and dropping content we cannot assess is worse than showing it).</param>
    /// <param name="requester">The requesting actor's IRI, or null for an anonymous /
    /// unauthenticated request.</param>
    /// <returns>
    /// <see langword="true"/> when the object is public (public sentinel, or no named audience), or
    /// when the requester is a named recipient, or when the requester is the author
    /// (<c>attributedTo</c>) — an actor can always see their own content; <see langword="false"/>
    /// otherwise. The author clause is what lets an actor see their own direct messages in their
    /// home timeline (a DM's audience is the recipient, not the sender).
    /// </returns>
    public static bool IsVisibleTo(IObject? obj, Iri? requester)
    {
        if (obj is null || IsPublic(obj))
        {
            return true;
        }

        return requester is { } r && (ContainsAudience(obj, r) || IsAuthor(obj, r));
    }

    /// <summary>
    /// Reports whether a content object is visible to a given (possibly anonymous) requester, resolving
    /// <em>followers-collection</em> audiences to membership when a follower check is supplied.
    /// </summary>
    /// <param name="obj">The content object whose <c>to</c>/<c>cc</c> audience is checked. May be null —
    /// treated as visible.</param>
    /// <param name="requester">The requesting actor's IRI, or null for an anonymous request.</param>
    /// <param name="isFollowerOfAsync">
    /// An async predicate reporting whether <paramref name="requester"/> is a follower of a given actor
    /// (the owner of a <c>…/followers</c> collection audience). When a <c>to</c>/<c>cc</c> entry is a
    /// followers-collection IRI, the object is visible to the requester if this predicate reports the
    /// requester follows the collection's owner — the ActivityPub convention that a followers-visibility
    /// post is visible to every follower. Pass <see langword="null"/> (the sync overload) to skip
    /// collection resolution: a followers-collection audience then never matches (only a literal
    /// recipient-IRI match or authorship grants visibility), which is the correct behavior for surfaces
    /// that cannot resolve membership.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the object is public, the requester is a named recipient, the requester
    /// follows the owner of a followers-collection audience (when <paramref name="isFollowerOfAsync"/> is
    /// supplied), or the requester is the author; <see langword="false"/> otherwise.
    /// </returns>
    public static async Task<bool> IsVisibleToAsync(
        IObject? obj,
        Iri? requester,
        Func<Iri, Task<bool>>? isFollowerOfAsync = null)
    {
        if (obj is null)
        {
            return true;
        }

        if (IsPublic(obj))
        {
            return true;
        }

        if (requester is not { } r)
        {
            return false;
        }

        if (ContainsAudience(obj, r) || IsAuthor(obj, r))
        {
            return true;
        }

        // A followers-collection audience (…/followers) names the owner's followers, not a literal
        // recipient: resolve membership so a follower of the owner can see the object.
        if (isFollowerOfAsync is not null)
        {
            foreach (var owner in FollowersCollectionOwners(obj))
            {
                if (await isFollowerOfAsync(owner).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Yields the actor IRIs that own a <c>…/followers</c> collection audience entry on
    /// <paramref name="obj"/> (its <c>to</c>/<c>cc</c>), with the <c>/followers</c> segment stripped so
    /// the result is the owner's actor IRI. De-duplicated (case-insensitive).
    /// </summary>
    private static IEnumerable<Iri> FollowersCollectionOwners(IObject obj)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in obj.To is { } to ? to.OfType<IObjectOrLink>() : [])
        {
            if (TryOwnerOfFollowersCollection(entry, out var owner) && seen.Add(owner.Value))
            {
                yield return owner;
            }
        }

        foreach (var entry in obj.Cc is { } cc ? cc.OfType<IObjectOrLink>() : [])
        {
            if (TryOwnerOfFollowersCollection(entry, out var owner) && seen.Add(owner.Value))
            {
                yield return owner;
            }
        }
    }

    /// <summary>
    /// Reports whether <paramref name="entry"/> is a followers-collection IRI (<c>…/followers</c>) and,
    /// when it is, returns the owner's actor IRI (the collection IRI with the <c>/followers</c> segment
    /// removed). A non-collection entry (a literal actor/recipient IRI) yields <see langword="false"/>.
    /// </summary>
    private static bool TryOwnerOfFollowersCollection(IObjectOrLink entry, out Iri owner)
    {
        owner = default;
        if (entry.ResolveObjectIri() is not { } iri)
        {
            return false;
        }

        const string segment = "/followers";
        var value = iri.Value;
        if (value.Length <= segment.Length || !value.EndsWith(segment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ownerValue = value[..^segment.Length];
        if (string.IsNullOrEmpty(ownerValue))
        {
            return false;
        }

        owner = new Iri(ownerValue);
        return true;
    }

    /// <summary>
    /// Reports whether a feed item (typically an <see cref="Activity"/> wrapping a content object, or
    /// a bare <see cref="IObject"/>) is visible to a given (possibly anonymous) requester.
    /// </summary>
    /// <param name="item">The feed item. For an <see cref="Activity"/>, the first embedded object in
    /// its <c>object</c> is the content whose audience is checked; for a bare <see cref="IObject"/>,
    /// the object itself.</param>
    /// <param name="requester">The requesting actor's IRI, or null for an anonymous /
    /// unauthenticated request.</param>
    /// <returns>
    /// <see langword="true"/> when the item's content is public or the requester is a named
    /// recipient; <see langword="true"/> also when no content object can be extracted (visibility
    /// cannot be assessed, so the item is kept rather than dropped).
    /// </returns>
    public static bool IsFeedItemVisibleTo(IObjectOrLink item, Iri? requester)
    {
        var content = item switch
        {
            Activity { Object: { } objects } => objects.FirstOrDefault() as IObject,
            IObject obj => obj,
            _ => null,
        };

        return IsVisibleTo(content, requester);
    }

    /// <summary>
    /// Async variant of <see cref="IsFeedItemVisibleTo"/> that resolves followers-collection audiences
    /// to membership via <paramref name="isFollowerOfAsync"/> (a followers-visibility feed item is
    /// visible to the requester when the requester follows the item's author).
    /// </summary>
    public static async Task<bool> IsFeedItemVisibleToAsync(
        IObjectOrLink item,
        Iri? requester,
        Func<Iri, Task<bool>>? isFollowerOfAsync = null)
    {
        var content = item switch
        {
            Activity { Object: { } objects } => objects.FirstOrDefault() as IObject,
            IObject obj => obj,
            _ => null,
        };

        return await IsVisibleToAsync(content, requester, isFollowerOfAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether a content object is public: its <c>to</c>/<c>cc</c> contains the public
    /// audience sentinel, or it names no audience at all (no audience ⇒ public by convention).
    /// </summary>
    /// <param name="obj">The object to inspect. May be null — treated as public (nothing contradicts
    /// visibility).</param>
    /// <returns><see langword="true"/> when the object is public to all requesters.</returns>
    public static bool IsPublic(IObject? obj)
    {
        if (obj is null)
        {
            return true;
        }

        if (HasPublicAudience(obj.To) || HasPublicAudience(obj.Cc))
        {
            return true;
        }

        // No public sentinel: public only if there is no named audience either.
        return !HasNamedAudience(obj.To) && !HasNamedAudience(obj.Cc);
    }

    private static bool HasPublicAudience(IEnumerable<IObjectOrLink>? entries)
    {
        if (entries is null)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.ResolveObjectIri() is { } iri && iri.IsPublicAudience())
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNamedAudience(IEnumerable<IObjectOrLink>? entries)
    {
        if (entries is null)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.ResolveObjectIri() is { } iri && !iri.IsPublicAudience())
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAudience(IObject obj, Iri requester)
    {
        return (obj.To is { } to && to.Any(e => MatchesAudience(e, requester)))
            || (obj.Cc is { } cc && cc.Any(e => MatchesAudience(e, requester)));
    }

    /// <summary>
    /// Reports whether <paramref name="requester"/> is the author of <paramref name="obj"/> (its
    /// <c>attributedTo</c> names the requester). An author can always see their own content — this is
    /// what lets an actor see their own direct messages (a DM's audience is the recipient, not the
    /// sender) in their home timeline.
    /// </summary>
    private static bool IsAuthor(IObject obj, Iri requester)
    {
        return obj.AttributedTo is { } attributedTo && attributedTo.Any(e => MatchesAudience(e, requester));
    }

    private static bool MatchesAudience(IObjectOrLink entry, Iri requester)
    {
        return entry.ResolveObjectIri() is { } iri
            && string.Equals(iri.Value, requester.Value, StringComparison.OrdinalIgnoreCase);
    }
}
