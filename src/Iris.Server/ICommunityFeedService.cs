using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Computes the unified feed for a community: the content its members have posted, merged newest-first.
/// </summary>
/// <remarks>
/// The feed is the community's content surface — the source for the <c>GET /c/{name}/feed</c> endpoint
/// and the client's <c>GetCommunityFeedAsync</c>. This slice assembles the feed from the **local
/// members'** outboxes (a member's posted activities, newest first, merged across members in outbox
/// order). The "followed community content" half — receiving and propagating content from *remote*
/// communities the community follows — is the separate <c>CommunityInboxHandler</c> / community-following
/// slice, which records that content in the community's outbox so it flows through this same path.
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
}
