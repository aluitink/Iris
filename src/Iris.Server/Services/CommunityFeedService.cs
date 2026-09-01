using Iris.Core;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="ICommunityFeedService"/>: merges the local members' outbox activities into a
/// single newest-first feed for a community, and searches that content.
/// </summary>
/// <remarks>
/// For each local member of the community, reads the member's outbox (the member's posted activities,
/// newest first) and merges them into a single **newest-first** feed: items are ordered by (outbox
/// position, then member IRI) — a stable, deterministic merge that ranks a member's newest post above
/// its older posts and orders same-position posts by member IRI. The merged items are de-duplicated by
/// activity IRI (keeping the first, i.e. newest, occurrence). A member with no outbox contributes
/// nothing; an unknown community or a community with no members yields an empty feed. The
/// <see cref="SearchCommunityAsync"/> method runs a case-insensitive substring search over the feed's
/// items' <c>content</c>/<c>name</c>.
/// </remarks>
/// <remarks>
/// <strong>Community moderation (19.5.4, apply the community's moderation edges).</strong> When
/// constructed with an <see cref="ICommunityStore"/> (the community's own moderation sets — the
/// <see cref="ICommunityStore.GetBlocksAsync(Iri, CancellationToken)"/> and <see cref="ICommunityStore.
/// GetMutesAsync(Iri, CancellationToken)"/> edges, scoped to the community being read), a member the
/// community has <em>blocked</em> or <em>muted</em> is excluded from the feed: the moderation is applied
/// on the community's side, so a blocked/muted member's content does not appear in the community's
/// unified feed. A block is a hard exclusion; a mute is a soft one (the membership is kept, only the
/// member's content is hidden). A member the community has only <em>flagged</em> is <em>not</em>
/// excluded — a flag is a moderation report surfaced in the community's <c>flags</c> collection for the
/// operator to act on, not a content exclusion (mirroring the person feed, where only blocks and mutes
/// filter the timeline). When the service is constructed without a community store, no moderation
/// filtering is applied (every member is merged).
/// </remarks>
public sealed class CommunityFeedService : ICommunityFeedService
{
    private readonly IPersistenceProvider _persistence;
    private readonly ICommunityStore? _communities;

    /// <summary>
    /// Initializes a new feed service over the given persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the community + activity stores). Must not be null.</param>
    /// <param name="communities">The community store (19.5.4): when present, a member the community has
    /// <em>blocked</em> or <em>muted</em> is excluded from the feed. Null (the default) disables
    /// community-moderation filtering (every member is merged). The community store is also read through
    /// <paramref name="persistence"/>'s <see cref="IPersistenceProvider.Communities"/> for membership;
    /// this parameter is the same store instance, injected so the moderation edges are resolvable without
    /// the service depending on a concrete provider shape.</param>
    public CommunityFeedService(IPersistenceProvider persistence, ICommunityStore? communities = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _communities = communities;
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

        // 19.5.4 (apply the community's moderation edges): a member the community has blocked or muted
        // is excluded from the feed (the moderation is applied on the community's side — a blocked/muted
        // member's content is hidden from the community's unified feed). A flag does NOT exclude a member
        // (it is a moderation report, surfaced in the community's flags collection, not a content filter).
        // When the service was constructed without a community store, both sets are empty and no member is
        // filtered (the feed merges every member, as before).
        var blocked = _communities is not null
            ? (await _communities.GetBlocksAsync(communityIri, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        var muted = _communities is not null
            ? (await _communities.GetMutesAsync(communityIri, ct).ConfigureAwait(false)).ToHashSet()
            : [];

        // The membership set has no inherent order (a set), so members are read in IRI order for a
        // deterministic, reproducible feed. Each member's outbox is already newest-first (the activity
        // store keeps it so), so a member's own posts are in recency order. Blocked/muted members are
        // dropped before their outbox is read (their content is excluded from the feed).
        var orderedMembers = memberIris
            .Where(m => !blocked.Contains(m) && !muted.Contains(m))
            .OrderBy(m => m.Value, StringComparer.Ordinal)
            .ToList();

        // Merge the members' outboxes into a single **newest-first** feed. An outbox has no per-item
        // timestamp to compare across members, so recency is approximated by each member's outbox
        // position (position 0 is that member's newest post). Items are ordered by (outbox position,
        // then member IRI) — a stable, deterministic merge: a member's newest post ranks above its older
        // posts, and two posts at the same outbox position are ordered by member IRI (deterministic, so
        // the feed is reproducible for a given set of outboxes). This is the "newest first" the feed
        // advertises (the union of the members' outboxes, de-duplicated, newest first).
        //
        // De-duplicate by activity IRI (keep the first, i.e. newest, occurrence). Members are local
        // actors, so their outboxes are served by the local activity store.
        var seen = new HashSet<Iri>();
        var merged = new List<(int Position, Iri MemberIri, IObjectOrLink Item)>();
        foreach (var memberIri in orderedMembers)
        {
            var outbox = await _persistence.Activities.GetOutboxAsync(memberIri, ct).ConfigureAwait(false);
            for (var position = 0; position < outbox.Count; position++)
            {
                var item = outbox[position];
                if (item is IObject { Id: { Length: > 0 } id })
                {
                    // Keep the newest (first) occurrence of a repeated IRI; drop the rest.
                    if (!seen.Add(new Iri(id)))
                    {
                        continue;
                    }
                }
                merged.Add((position, memberIri, item));
            }
        }

        return merged
            .OrderBy(m => m.Position)
            .ThenBy(m => m.MemberIri.Value, StringComparer.Ordinal)
            .Select(m => m.Item)
            .ToList();
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
