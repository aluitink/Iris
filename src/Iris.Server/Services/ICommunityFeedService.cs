using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// Computes the unified feed for a community: the content its members have posted, merged newest-first,
/// and searches that content.
/// </summary>
/// <remarks>
/// The feed is the community's content surface — the source for the <c>GET /c/{name}/feed</c> endpoint
/// and the client's <c>GetCommunityFeedAsync</c>. This slice assembles the feed from the **local
/// members'** outboxes (a member's posted activities, newest first, merged across members in outbox
/// order). The "followed community content" half — receiving and propagating content from *remote*
/// communities the community follows — is the separate <c>CommunityInboxHandler</c> / community-following
/// slice, which records that content in the community's outbox so it flows through this same path. The
/// <see cref="SearchCommunityAsync"/> method is the source for the <c>GET /c/{name}/search</c> specialized
/// collection: it searches the community's content (the same surface as the feed) case-insensitively.
/// </remarks>
public interface ICommunityFeedService
{
    /// <summary>
    /// Returns the community's feed items: the union of the local members' outbox activities, newest first.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the feed items (empty when the community has no members or no member content).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Searches the community's content (the feed surface) for items whose content or name contains the
    /// query, case-insensitively.
    /// </summary>
    /// <remarks>
    /// The search runs over the same content as <see cref="GetFeedAsync"/> (the union of the local
    /// members' outbox activities, deduplicated). An item matches when its <c>content</c> or <c>name</c>
    /// (either as a single value or a value within the multi-valued property) contains the query as a
    /// substring, case-insensitively (ordinal). An empty or whitespace-only query matches **all** items
    /// (the feed, unfiltered). The results preserve the feed's ordering (member order, newest first within
    /// a member) so the search is deterministic/reproducible.
    /// </remarks>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="query">The search term (matched case-insensitively against item content/name). An
    /// empty/whitespace query returns all items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the matching items, in feed order (empty when nothing matches).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> SearchCommunityAsync(Iri communityIri, string? query, CancellationToken ct = default);
}
