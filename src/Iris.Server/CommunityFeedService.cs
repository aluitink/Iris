using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// The default <see cref="ICommunityFeedService"/>: merges the local members' outbox activities into a
/// single newest-first feed for a community.
/// </summary>
/// <remarks>
/// For each local member of the community, reads the member's outbox (the member's posted activities,
/// newest first) and concatenates them in member order, then de-duplicates by activity IRI (keeping the
/// first, i.e. newest, occurrence). A member with no outbox contributes nothing; an unknown community or
/// a community with no members yields an empty feed.
/// </remarks>
public sealed class CommunityFeedService : ICommunityFeedService
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new feed service over the given persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the community + activity stores). Must not be null.</param>
    public CommunityFeedService(IPersistenceProvider persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri communityIri, CancellationToken ct = default)
    {
        var memberIris = await _persistence.Communities.GetMembersAsync(communityIri, ct).ConfigureAwait(false);
        if (memberIris.Count == 0)
        {
            return [];
        }

        // The membership set has no inherent order (a set), so sort by actor IRI for a deterministic,
        // reproducible feed. Within a member, the outbox is already newest-first; across members the
        // feed is grouped by member (in IRI order), each member's posts newest-first.
        var orderedMembers = memberIris.OrderBy(m => m.Value, StringComparer.Ordinal).ToList();

        // Concatenate each member's outbox (newest first) in member order, then de-duplicate by activity
        // IRI (keep the first, i.e. newest, occurrence). Members are local actors, so their outboxes are
        // served by the local activity store.
        var seen = new HashSet<Iri>();
        var feed = new List<IObjectOrLink>();
        foreach (var memberIri in orderedMembers)
        {
            var outbox = await _persistence.Activities.GetOutboxAsync(memberIri, ct).ConfigureAwait(false);
            foreach (var item in outbox)
            {
                if (item is IObject { Id: { Length: > 0 } id } && seen.Add(new Iri(id)))
                {
                    feed.Add(item);
                }
            }
        }

        return feed;
    }
}
