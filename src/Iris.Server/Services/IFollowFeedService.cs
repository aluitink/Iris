using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// Computes an actor's <strong>followed feed</strong> (the home timeline, F-14): the actor's own posts
/// plus the content the actor follows, merged newest-first.
/// </summary>
/// <remarks>
/// The feed is the union of:
/// <list type="bullet">
/// <item>
/// the actor's <em>own</em> outbox (read from the local activity store — the home timeline shows the
/// signed-in actor's own posts alongside their follows', 54.17),
/// </item>
/// <item>
/// the outboxes of the actor's <em>local</em> follows (read from the local activity store — no network),
/// and
/// </item>
/// <item>
/// the outboxes of the actor's <em>remote</em> follows (fetched over the wire via the
/// <see cref="IActorDocumentFetcher"/> and the outbound ActivityPub client, walking each outbox's pages).
/// </item>
/// </list>
/// The result is de-duplicated by item IRI and capped by the configured <see cref="FeedOptions"/>. This
/// is the source for the <c>GET /u/{handle}/feed</c> endpoint and the client's
/// <c>GetFollowFeedAsync</c>. Unlike the <see cref="ICommunityFeedService"/> (which merges a community's
/// local members' outboxes), the followed feed is per-actor and spans the actor's own outbox plus both
/// local and remote follows — the "home timeline" a client polls for new content.
/// </remarks>
public interface IFollowFeedService
{
    /// <summary>
    /// Returns the followed feed for the given actor: the union of the actor's local and remote
    /// follows' outbox items, newest-first, de-duplicated, capped by <see cref="FeedOptions"/>.
    /// </summary>
    /// <remarks>
    /// When <paramref name="query"/> is non-empty/whitespace, the feed is **filtered** to the items that
    /// match it (the same content/name match as the community feed's <c>?q</c> filter, F-23): an item
    /// matches when its <c>content</c> or <c>name</c> (either as a single value or a value within the
    /// multi-valued property, and — for activities — the content/name of each referenced object) contains
    /// the query as a substring, case-insensitively. A null/empty/whitespace query returns the feed
    /// unfiltered. This is the source for the <c>GET /u/{handle}/feed?q=...</c> filter (21.4.2).
    /// </remarks>
    /// <param name="actorIri">The IRI of the actor whose followed feed is requested (must be a local actor).</param>
    /// <param name="query">Optional content filter (matched case-insensitively against item content/name).
    /// A null/empty/whitespace query returns the feed unfiltered.</param>
    /// <param name="activityType">Optional activity-type filter (e.g. <c>"Create"</c>): when non-empty, only
    /// items whose <c>type</c> includes the given ActivityStreams type are returned. A null/empty/whitespace
    /// value returns the feed unfiltered by type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the feed items (the actor's own posts plus the follows' posts;
    /// filtered when a query/type filter is supplied; empty only when the actor has no posts of their own
    /// and no followed actor has content, or nothing matches the filters). A remote outbox that cannot be
    /// fetched contributes nothing (it does not fail the whole feed).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, string? query = null, string? activityType = null, CancellationToken ct = default);
}
