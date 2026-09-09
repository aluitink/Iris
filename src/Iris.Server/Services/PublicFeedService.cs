using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="IPublicFeedService"/>: merges all local actors' outbox activities into a
/// single newest-first, de-duplicated, capped feed. This is the instance's public timeline — the
/// surface a logged-out visitor sees at <c>GET /ap/v1/public/feed</c>.
/// </summary>
/// <remarks>
/// For each local actor (from <see cref="IActorStore.ListActorsAsync"/>), reads the actor's outbox
/// (from <see cref="IActivityStore.GetOutboxAsync"/>) and concatenates the items. The union is
/// de-duplicated by item IRI (keep the first occurrence) and truncated to the requested maximum.
/// Communities are excluded (their content is surfaced through the community feed, not the public
/// feed — a community is not a "person" posting to the public timeline).
/// </remarks>
public sealed class PublicFeedService : IPublicFeedService
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new public feed service.
    /// </summary>
    /// <param name="persistence">The persistence provider (the <see cref="IActorStore"/> and
    /// <see cref="IActivityStore"/>).</param>
    public PublicFeedService(IPersistenceProvider persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetPublicFeedAsync(
        int maxItems,
        string? query = null,
        string? activityType = null,
        CancellationToken ct = default)
    {
        var actors = await _persistence.Actors.ListActorsAsync(ct).ConfigureAwait(false);

        // Only Person actors contribute to the public feed (Groups/communities have their own feed).
        var personActors = actors
            .Where(a => a.Type?.FirstOrDefault() is string t && string.Equals(t, "Person", StringComparison.Ordinal))
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        var feed = new List<IObjectOrLink>();

        foreach (var actor in personActors)
        {
            if (string.IsNullOrEmpty(actor.Id))
            {
                continue;
            }

            var actorIri = new Iri(actor.Id);
            var items = await _persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                feed.Add(item);
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            feed = FilterByQuery(feed, query.Trim()).ToList();
        }

        if (!string.IsNullOrWhiteSpace(activityType))
        {
            feed = FilterByType(feed, activityType).ToList();
        }

        return TruncateDedup(feed, maxItems);
    }

    private static IReadOnlyList<IObjectOrLink> FilterByQuery(IReadOnlyList<IObjectOrLink> feed, string query)
    {
        var matches = new List<IObjectOrLink>();
        foreach (var item in feed)
        {
            if (item is IObject obj)
            {
                var activityMatches =
                    ContainsInStrings(obj.Content, query) || ContainsInStrings(obj.Name, query);
                var nestedMatches = false;
                if (obj is Activity activity)
                {
                    foreach (var referenced in activity.Object ?? [])
                    {
                        if (referenced is IObject refObj &&
                            (ContainsInStrings(refObj.Content, query) || ContainsInStrings(refObj.Name, query)))
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

    private static bool ContainsInStrings(IEnumerable<string>? values, string query)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<IObjectOrLink> FilterByType(IReadOnlyList<IObjectOrLink> feed, string activityType)
    {
        var matches = new List<IObjectOrLink>();
        foreach (var item in feed)
        {
            if (item is Activity activity)
            {
                var type = activity.Type?.FirstOrDefault();
                if (string.Equals(type, activityType, StringComparison.Ordinal))
                {
                    matches.Add(item);
                }
            }
        }

        return matches;
    }

    private static IReadOnlyList<IObjectOrLink> TruncateDedup(IReadOnlyList<IObjectOrLink> items, int maxItems)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<Iri>();
        var result = new List<IObjectOrLink>(Math.Min(items.Count, maxItems));
        foreach (var item in items)
        {
            if (result.Count >= maxItems)
            {
                break;
            }

            if (item is IObject { Id: { Length: > 0 } id })
            {
                if (!seen.Add(new Iri(id)))
                {
                    continue;
                }
            }

            result.Add(item);
        }

        return result;
    }
}
