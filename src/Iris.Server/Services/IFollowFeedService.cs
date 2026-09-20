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
    /// <param name="threadDepth">Optional reply-depth threshold (117.1): when non-null and > 0, replies up
    /// to this depth from followed actors are included in the feed. When null or 0, all replies from
    /// followed actors are filtered out (the default). The actor's own replies are always kept regardless.</param>
    /// <param name="requesterIri">The requesting actor's IRI, or null for an anonymous / unsigned
    /// request. When set, non-public items (followers-only or direct) not addressed to the requester
    /// (and not authored by them) are excluded from the feed; when null, only public items are
    /// returned. The feed's owner always sees their own posts (the author clause). This is the
    /// audience/visibility filter — without it an anonymous request to <c>GET /u/{handle}/feed</c>
    /// would surface the owner's direct messages and their follows' non-public posts (the Phase
    /// 136.18 / 139.2-s5 gap, follow-feed surface).</param>
    /// <param name="source">Optional source filter (unified-home-feed Phase 2): <c>"people"</c> → items
    /// whose <c>attributedTo</c> does not include a <c>Group</c>; <c>"communities"</c> → items whose
    /// <c>attributedTo</c> does include a <c>Group</c>; null/absent → full merged feed (back-compat).</param>
    /// <param name="bypassCache">When true, the per-actor feed cache is skipped (the feed is rebuilt
    /// from the stores and the cache entry is refreshed). This is the server-side <c>?refresh=true</c>
    /// escape hatch: after a moderation edge change (block/mute/unblock/unmute) or a new post, the
    /// caller can force a rebuild without waiting for the TTL to lapse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the feed items (the actor's own posts plus the follows' posts;
    /// filtered when a query/type/source filter is supplied; empty only when the actor has no posts of
    /// their own and no followed actor has content, or nothing matches the filters). A remote outbox that
    /// cannot be fetched contributes nothing (it does not fail the whole feed).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, string? query = null, string? activityType = null, int? threadDepth = null, Iri? requesterIri = null, string? source = null, bool bypassCache = false, CancellationToken ct = default);

    /// <summary>
    /// Invalidates the server-side per-actor feed cache entry for <paramref name="actorIri"/> so the next
    /// <c>GET /u/{handle}/feed</c> (without <c>?refresh=true</c>) rebuilds from the stores instead of
    /// serving the cached (pre-change) feed for the rest of the TTL.
    /// </summary>
    /// <remarks>
    /// The per-actor feed cache (<see cref="FeedService"/>, 30 s TTL) merges the actor's <em>current</em>
    /// <c>following</c> set. When a follow edge the actor depends on changes — most importantly an
    /// <em>un-follow</em> (an <c>Undo</c> of a <c>Follow</c>) — the cached feed still contains the
    /// (now-unfollowed) actor's posts until the TTL lapses. Without this invalidation, an un-follower
    /// "still sees messages from an unfollowed user" for up to the cache TTL (and longer if the client
    /// honors the feed's <c>Cache-Control</c>). Callers invoke this at the follow-edge write sites (the
    /// outbox Follow/Undo branches and the inbound handlers that record/remove a local edge). The default
    /// implementation is a no-op, so an implementation without a server-side cache (or a test double)
    /// need not override it.
    /// </remarks>
    /// <param name="actorIri">The actor whose feed cache entry should be dropped (the un-follower, on the
    /// actor's home instance).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual void InvalidateActorFeedCache(Iri actorIri, CancellationToken ct = default)
    {
        _ = ct;
    }
}
