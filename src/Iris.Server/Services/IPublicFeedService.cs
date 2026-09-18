using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// Computes the instance's <strong>public feed</strong>: the union of all local actors' outbox
/// activities, newest first, de-duplicated, and capped. Serves as the read surface for the
/// <c>GET /ap/v1/public/feed</c> endpoint, which any visitor (signed in or out) can browse.
/// </summary>
/// <remarks>
/// Unlike the <see cref="IFollowFeedService"/> (per-actor, merges the actor's own outbox with
/// their follows' outboxes) or the <see cref="ICommunityFeedService"/> (per-community, merges
/// member outboxes), the public feed is instance-wide: it surfaces every local actor's posts so
/// a logged-out visitor has something to browse.
/// </remarks>
public interface IPublicFeedService
{
    /// <summary>
    /// Returns the instance's public feed: the union of all local actors' outbox activities,
    /// newest first, de-duplicated by item IRI, capped at <paramref name="maxItems"/>.
    /// </summary>
    /// <param name="maxItems">The maximum number of items to return (the feed is truncated to this).</param>
    /// <param name="query">
    /// An optional content/name filter (case-insensitive substring match). Null/empty/whitespace
    /// returns the feed unfiltered.
    /// </param>
    /// <param name="activityType">
    /// An optional ActivityStreams type filter (e.g. "Create"). Null/empty/whitespace returns the
    /// feed unfiltered by type.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the feed items (possibly empty).</returns>
    Task<IReadOnlyList<IObjectOrLink>> GetPublicFeedAsync(
        int maxItems,
        string? query = null,
        string? activityType = null,
        CancellationToken ct = default);
}
