using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="ICommunityFeedService"/>: merges the local members' outbox activities into a
/// single newest-first feed for a community, and searches that content.
/// </summary>
/// <remarks>
/// For each local member of the community, reads the member's outbox (the member's posted activities,
/// newest first) and concatenates them in member order, then de-duplicates by activity IRI (keeping the
/// first, i.e. newest, occurrence). A member with no outbox contributes nothing; an unknown community or
/// a community with no members yields an empty feed. The <see cref="SearchCommunityAsync"/> method runs a
/// case-insensitive substring search over the feed's items' <c>content</c>/<c>name</c>.
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
    public async Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri communityIri, string? query = null, CancellationToken ct = default)
    {
        // A non-empty query filters the feed to the matching items (the same content/name match as the
        // community search, F-23): the feed endpoint's ?q= is a filtered view of this same surface.
        if (!string.IsNullOrWhiteSpace(query))
        {
            return await SearchCommunityAsync(communityIri, query, ct).ConfigureAwait(false);
        }

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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> SearchCommunityAsync(Iri communityIri, string? query, CancellationToken ct = default)
    {
        // The search runs over the same surface as the feed (the union of the members' outbox
        // activities). An empty/whitespace query matches all items (the feed, unfiltered).
        var feed = await GetFeedAsync(communityIri, null, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query))
        {
            return feed;
        }

        var normalized = query.Trim();
        var matches = new List<IObjectOrLink>();
        foreach (var item in feed)
        {
            // An outbox item is an activity (e.g. a Create) whose content lives on the nested object
            // (e.g. the Create's Note). Match the activity's own content/name and, for activities, the
            // content/name of each referenced object.
            if (item is IObject obj)
            {
                var activityMatches =
                    ContainsInStrings(obj.Content, normalized) || ContainsInStrings(obj.Name, normalized);
                var nestedMatches = false;
                if (obj is Activity activity)
                {
                    foreach (var referenced in activity.Object ?? [])
                    {
                        if (referenced is IObject refObj &&
                            (ContainsInStrings(refObj.Content, normalized) || ContainsInStrings(refObj.Name, normalized)))
                        {
                            nestedMatches = true;
                            break;
                        }
                    }
                }

                if (activityMatches || nestedMatches)
                {
                    matches.Add(item);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Returns true when any value in the multi-valued <c>content</c>/<c>name</c> property contains
    /// <paramref name="query"/> as a substring (case-insensitive, ordinal).
    /// </summary>
    private static bool ContainsInStrings(IEnumerable<string>? values, string query)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null &&
                value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
