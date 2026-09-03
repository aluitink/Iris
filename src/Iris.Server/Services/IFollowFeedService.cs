using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// Computes an actor's <strong>followed feed</strong> (the home timeline, F-14): the content the actor
/// follows, merged newest-first.
/// </summary>
/// <remarks>
/// The feed is the union of:
/// <list type="bullet">
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
/// local members' outboxes), the followed feed is per-actor and spans both local and remote follows —
/// the "home timeline" a client polls for new content from the people/communities an actor follows.
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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the feed items (filtered when a query is supplied; empty when the
    /// actor follows no one, no followed actor has content, or nothing matches the query). A remote outbox
    /// that cannot be fetched contributes nothing (it does not fail the whole feed).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, string? query = null, CancellationToken ct = default);
}
