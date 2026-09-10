using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="IFollowFeedService"/> (F-14): merges the actor's <em>own</em> outbox (read from
/// the local activity store) with the local follows' outboxes (read from the local store) and the remote
/// follows' outboxes (fetched over the wire, walking each outbox's pages) into a single newest-first,
/// de-duplicated, capped feed.
/// </summary>
/// <remarks>
/// For each followed actor the service reads (local) or walks (remote) the outbox's first
/// <see cref="FeedOptions.PagesPerActor"/> pages and concatenates the items. The union is de-duplicated
/// by item IRI (keep the first occurrence) and truncated to <see cref="FeedOptions.MaxItems"/>. A remote
/// outbox that cannot be fetched (404, network error, not a page) contributes nothing — a single broken
/// remote must not fail the whole feed. The merge is in IRI order across follows (deterministic, like
/// the community feed) so the feed is reproducible for a given set of follows.
/// </remarks>
/// <remarks>
/// <strong>Block and mute filtering (F-07, apply the moderation edges).</strong> When constructed with
/// a <see cref="IModerationStore"/>, a follow the actor has <em>blocked</em> (per the store's
/// <see cref="IModerationStore.GetBlocksAsync(Iri, CancellationToken)"/>) or <em>muted</em> (per
/// <see cref="IModerationStore.GetMutesAsync(Iri, CancellationToken)"/>) is excluded from the feed: the
/// moderation is applied on the actor's side, so the other actor's content does not appear in the actor's
/// home timeline. A block is a hard exclusion (the relationship is severed); a mute is a soft one (the
/// follow is kept, only its content is hidden). When the service is constructed without a moderation
/// store (moderation disabled) every follow is merged (no filtering). The check is by the follow's actor
/// IRI (the edge is recorded on the actor IRI), so it applies uniformly to local and remote follows.
/// </remarks>
public sealed class FeedService : IFollowFeedService
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;
    private readonly IActorDocumentFetcher _actorDocs;
    private readonly IActivityPubClient _client;
    private readonly FeedOptions _options;
    private readonly IModerationStore? _moderation;

    /// <summary>
    /// Initializes a new followed-feed service.
    /// </summary>
    /// <param name="persistence">The persistence provider (the <see cref="IFollowStore"/>,
    /// <see cref="IActivityStore"/>, and <see cref="IModerationStore"/>).</param>
    /// <param name="localActors">Resolves whether a followed actor is local (its outbox is read from the
    /// local store) or remote (its outbox is fetched over the wire).</param>
    /// <param name="actorDocs">Fetches a remote followed actor's document to read its <c>outbox</c> IRI.</param>
    /// <param name="client">Fetches a remote followed actor's outbox pages over the wire.</param>
    /// <param name="optionsAccessor">The feed options (pages per actor + max items).</param>
    /// <param name="moderation">The moderation store (F-07): when present, a follow the actor has
    /// <em>blocked</em> or <em>muted</em> is excluded from the feed. Null disables block/mute filtering
    /// (every follow is merged).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public FeedService(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        IActorDocumentFetcher actorDocs,
        IActivityPubClient client,
        IOptions<FeedOptions> optionsAccessor,
        IModerationStore? moderation = null)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        ArgumentNullException.ThrowIfNull(actorDocs);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _persistence = persistence;
        _localActors = localActors;
        _actorDocs = actorDocs;
        _client = client;
        _options = optionsAccessor.Value;
        _moderation = moderation;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, string? query = null, string? activityType = null, CancellationToken ct = default)
    {
        var feed = await BuildFeedAsync(actorIri, ct).ConfigureAwait(false);

        // A non-empty query filters the feed to the matching items (the same content/name match as the
        // community feed's ?q filter, F-23 / 21.4.2): an item matches when its content/name (or, for
        // activities, the content/name of each referenced object) contains the query, case-insensitively.
        if (!string.IsNullOrWhiteSpace(query))
        {
            feed = FilterFeed(feed, query);
        }

        // A non-empty activityType filters the feed to only activities of that type (e.g. "Create" to
        // show only posts, excluding Flag/Block/Like/Announce activities).
        if (!string.IsNullOrWhiteSpace(activityType))
        {
            feed = FilterFeedByType(feed, activityType);
        }

        return feed;
    }

    /// <summary>
    /// Builds the unfiltered followed feed for the given actor: the actor's <em>own</em> outbox items plus
    /// the union of the actor's local and remote follows' outbox items, newest-first, de-duplicated, capped
    /// by <see cref="FeedOptions"/>.
    /// </summary>
    /// <remarks>
    /// The actor's own outbox is always merged in (54.17): a home timeline shows the signed-in actor's own
    /// posts alongside the posts of the actors they follow. The actor is always local here (the feed
    /// endpoint only resolves local actors), so their outbox is read from the local store. The own-outbox
    /// items are prepended before the followed actors' items; <see cref="TruncateDedup"/> de-duplicates by
    /// IRI (a post the actor made cannot also appear in a follow's outbox, but the de-dup is a cheap
    /// safeguard) and caps the result to <see cref="FeedOptions.MaxItems"/>.
    /// </remarks>
    private async Task<IReadOnlyList<IObjectOrLink>> BuildFeedAsync(Iri actorIri, CancellationToken ct)
    {
        var followed = await _persistence.Follows.GetFollowingAsync(actorIri, ct).ConfigureAwait(false);

        // Deterministic order across follows (IRI order), like the community feed.
        var ordered = followed.OrderBy(f => f.Value, StringComparer.Ordinal).ToList();

        // F-07 (apply the block + mute edges): the sets of follows the actor has blocked and muted. When
        // the moderation store is present, a blocked or muted follow contributes nothing to the feed (the
        // moderation is applied on the actor's side — the other actor's content is excluded from the
        // actor's home timeline). A block is a hard exclusion; a mute is a soft one (the follow is kept,
        // only its content is hidden). Without a moderation store (moderation disabled), both sets are
        // empty and no follow is filtered.
        var blocked = await _persistence.Moderation
            .GetBlocksAsync(actorIri, ct)
            .ConfigureAwait(false);
        var muted = await _persistence.Moderation
            .GetMutesAsync(actorIri, ct)
            .ConfigureAwait(false);

        var feed = new List<IObjectOrLink>();

        // The actor's own posts (54.17): always included, regardless of follows. The actor is local (the
        // feed endpoint only resolves local actors), so read their outbox from the local store.
        foreach (var item in await _persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false))
        {
            feed.Add(item);
        }

        foreach (var followIri in ordered)
        {
            if (blocked.Contains(followIri) || muted.Contains(followIri))
            {
                // The actor blocked or muted this follow: its content is excluded from the feed (F-07).
                continue;
            }

            IReadOnlyList<IObjectOrLink> items =
                await _localActors.IsLocalActorAsync(followIri, ct).ConfigureAwait(false)
                    ? await _persistence.Activities.GetOutboxAsync(followIri, ct).ConfigureAwait(false)
                    : await FetchRemoteOutboxAsync(followIri, ct).ConfigureAwait(false);

            foreach (var item in items)
            {
                feed.Add(item);
            }
        }

        return TruncateDedup(feed);
    }

    /// <summary>
    /// Filters the feed items to those whose content/name matches <paramref name="query"/>,
    /// case-insensitively (the same match as the community feed's <see cref="CommunityFeedService
    /// .SearchCommunityAsync"/>). An item matches when its <c>content</c> or <c>name</c> (either as a
    /// single value or a value within the multi-valued property) contains the query as a substring, and —
    /// for activities — when the content/name of any referenced object does. The order is preserved.
    /// </summary>
    private static IReadOnlyList<IObjectOrLink> FilterFeed(IReadOnlyList<IObjectOrLink> feed, string query)
    {
        var normalized = query.Trim();
        var matches = new List<IObjectOrLink>();
        foreach (var item in feed)
        {
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

    /// <summary>
    /// Filters the feed items to those whose ActivityStreams <c>type</c> includes the given type
    /// (case-sensitive, matching the standard ActivityStreams type names like <c>Create</c>, <c>Like</c>,
    /// <c>Announce</c>, etc.). Non-activity items (plain objects) are excluded when a type filter is
    /// active. The order is preserved.
    /// </summary>
    private static IReadOnlyList<IObjectOrLink> FilterFeedByType(IReadOnlyList<IObjectOrLink> feed, string activityType)
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

    /// <summary>
    /// Walks a remote followed actor's outbox (up to <see cref="FeedOptions.PagesPerActor"/> pages) over
    /// the wire and returns the items. A remote that cannot be resolved or fetched contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> FetchRemoteOutboxAsync(Iri followIri, CancellationToken ct)
    {
        // Read the remote actor's document to get its outbox IRI (a remote outbox is not always at the
        // conventional {actor}/outbox, so the advertised IRI is authoritative). The library's
        // collection properties are typed as a single <c>Link</c> (the OneOrMultiple shape), so the
        // first entry is read via its <c>Href</c>; when absent, fall back to the ActivityPub convention.
        //
        // The IActorDocumentFetcher contract is "return null, do not throw" on fetch failure, but the
        // implementation can still throw (a transport error, timeout, or a signing-key failure in the
        // outbound actor-doc fetch propagates uncaught). Guard it the same way the outbox walk below is
        // guarded: a broken remote actor-document contributes nothing rather than failing the whole feed.
        Actor? actor = null;
        try
        {
            actor = await _actorDocs.GetActorAsync(followIri, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A remote actor-document that errors (network, timeout, signing) contributes nothing; a
            // single broken remote must not fail the whole feed.
        }

        var outboxIri = actor?.Outbox is { } outboxRef
            ? outboxRef.ResolveCollectionIri() ?? followIri.OutboxOf()
            : followIri.OutboxOf();

        // Walk the outbox through the shared client enumeration (it resolves the collection's `first`
        // page, then follows `next` across pages — handling both the page-1 OrderedCollection shape and
        // the page-N>1 OrderedCollectionPage shape). Cap the walk at PagesPerActor pages; a fetch
        // failure (404, network error, not a page) yields nothing, so a broken remote contributes no
        // items rather than failing the whole feed.
        var items = new List<IObjectOrLink>();
        var pagesWalked = 0;
        try
        {
            await foreach (var page in _client.GetCollectionAsync(outboxIri, new CollectionQuery(), ct).ConfigureAwait(false))
            {
                if (pagesWalked >= _options.PagesPerActor)
                {
                    break;
                }

                pagesWalked++;
                items.AddRange(page.Items);
            }
        }
        catch (Exception)
        {
            // A remote outbox that errors mid-walk contributes what was already fetched (usually
            // nothing); a single broken remote must not fail the whole feed.
        }

        return items;
    }

    /// <summary>
    /// De-duplicates the merged items and truncates to <see cref="FeedOptions.MaxItems"/>.
    /// </summary>
    /// <remarks>
    /// Two de-dup passes, applied in order, then the cap:
    /// <list type="number">
    /// <item><term>By item IRI</term> — keep the first occurrence of each activity IRI (a cross-post
    /// scenario where the same activity IRI appears in two follows' outboxes).</item>
    /// <item><term>By content object</term> — a single object can surface in the feed under more than one
    /// activity type: an actor's own <c>Create</c> of a note, and a follower's <c>Announce</c> (boost) of
    /// the same note, are two distinct activities with two distinct IRIs but one piece of content. Left
    /// un-coalesced the home timeline renders the note twice (once as the author's post, once as the
    /// boost). Items are grouped by the IRI of the object a <c>Create</c>/<c>Announce</c> references; per
    /// group a single <em>representative</em> is kept — an item carrying the object <em>embedded</em>
    /// (rich, renderable without an extra fetch) is preferred over a <em>link-only</em> reference, so the
    /// author's content-bearing <c>Create</c> wins over a booster's bare <c>Announce</c>. Non-content
    /// items (plain objects, and social activities such as <c>Like</c>/<c>Follow</c>) are never coalesced.</item>
    /// </list>
    /// The cap (<see cref="FeedOptions.MaxItems"/>) is applied last, so a duplicate consuming a slot does
    /// not displace a legitimate item. Items without an IRI are kept (they cannot be de-duplicated).
    /// </remarks>
    private IReadOnlyList<IObjectOrLink> TruncateDedup(IReadOnlyList<IObjectOrLink> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        // Pass 1: de-duplicate by item IRI (keep the first occurrence).
        var seenIri = new HashSet<Iri>();
        var iriDeduped = new List<IObjectOrLink>(items.Count);
        foreach (var item in items)
        {
            if (item is IObject { Id: { Length: > 0 } id })
            {
                if (!seenIri.Add(new Iri(id)))
                {
                    continue;
                }
            }

            iriDeduped.Add(item);
        }

        // Pass 2: coalesce content items that reference the same object (a note surfaced as both a
        // Create and an Announce renders once). The representative per object IRI is chosen below.
        // repIndex[objIri] -> the index in `iriDeduped` of the item kept as the representative;
        // repEmbedded[objIri] -> whether that representative carries the object embedded (a richer
        // embedded item replaces an earlier link-only one, preserving the representative's position).
        var repIndex = new Dictionary<Iri, int>();
        var repEmbedded = new Dictionary<Iri, bool>();
        var drop = new bool[iriDeduped.Count];
        for (var i = 0; i < iriDeduped.Count; i++)
        {
            var (objIriOrNull, embedded) = ContentObjectIri(iriDeduped[i]);
            if (objIriOrNull is not { } objIri)
            {
                continue; // not a content item referencing an object; never coalesced
            }

            // `objIri` is narrowed to a non-null Iri by the pattern above (the per-object key).

            if (!repIndex.TryGetValue(objIri, out var existingIndex))
            {
                // First occurrence of this object: it is the representative.
                repIndex[objIri] = i;
                repEmbedded[objIri] = embedded;
                continue;
            }

            // A later item references the same object. Keep the richer one as the representative.
            if (embedded && !repEmbedded[objIri])
            {
                // The new item embeds the object and the current representative is link-only: promote the
                // new item to the representative, but keep the representative's original position (the
                // earlier slot) so the feed ordering is stable.
                drop[existingIndex] = true;
                repIndex[objIri] = i;
                repEmbedded[objIri] = true;
            }
            else
            {
                // The new item is not strictly better (link-only, or the representative already embeds):
                // it is a duplicate and is dropped.
                drop[i] = true;
            }
        }

        // Pass 3: emit the survivors (in order) capped to MaxItems.
        var result = new List<IObjectOrLink>(Math.Min(iriDeduped.Count, _options.MaxItems));
        for (var i = 0; i < iriDeduped.Count; i++)
        {
            if (result.Count >= _options.MaxItems)
            {
                break;
            }

            if (drop[i])
            {
                continue;
            }

            result.Add(iriDeduped[i]);
        }

        return result;
    }

    /// <summary>
    /// Resolves the content object a <c>Create</c>/<c>Announce</c> references, for the by-object
    /// coalescing pass. Returns the referenced object's IRI and whether it is <em>embedded</em> (the
    /// activity carries the full object, renderable without an extra fetch) rather than a link-only
    /// reference. Non-content activities and activities with no resolvable object IRI return
    /// <c>(null, false)</c> — they are never coalesced by object.
    /// </summary>
    private static (Iri? ObjectIri, bool Embedded) ContentObjectIri(IObjectOrLink item)
    {
        if (item is not Activity activity)
        {
            return (null, false);
        }

        var type = activity.Type?.FirstOrDefault();
        if (type is not ("Create" or "Announce"))
        {
            return (null, false);
        }

        var first = activity.Object?.FirstOrDefault();
        var objIri = first?.ResolveObjectIri();
        if (objIri is null)
        {
            return (null, false);
        }

        // Embedded when the activity carries the object as a full object (not a bare link) — that item
        // is the one that renders the content (and the server-rendered engagement counters) in place.
        return (objIri, first is IObject);
    }
}
