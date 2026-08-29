using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.CollectionPage;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IFollowFeedService"/> (F-14): merges an actor's local follows' outboxes (read
/// from the local activity store) with the remote follows' outboxes (fetched over the wire, walking each
/// outbox's pages) into a single newest-first, de-duplicated, capped feed.
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
    public async Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, CancellationToken ct = default)
    {
        var followed = await _persistence.Follows.GetFollowingAsync(actorIri, ct).ConfigureAwait(false);
        if (followed.Count == 0)
        {
            return [];
        }

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
    /// Walks a remote followed actor's outbox (up to <see cref="FeedOptions.PagesPerActor"/> pages) over
    /// the wire and returns the items. A remote that cannot be resolved or fetched contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> FetchRemoteOutboxAsync(Iri followIri, CancellationToken ct)
    {
        // Read the remote actor's document to get its outbox IRI (a remote outbox is not always at the
        // conventional {actor}/outbox, so the advertised IRI is authoritative). The library's
        // collection properties are typed as a single <c>Link</c> (the OneOrMultiple shape), so the
        // first entry is read via its <c>Href</c>; when absent, fall back to the ActivityPub convention.
        var actor = await _actorDocs.GetActorAsync(followIri, ct).ConfigureAwait(false);
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
    /// De-duplicates the merged items by IRI (keep the first occurrence) and truncates to
    /// <see cref="FeedOptions.MaxItems"/>. Items without an IRI are kept (they cannot be de-duplicated).
    /// </summary>
    private IReadOnlyList<IObjectOrLink> TruncateDedup(IReadOnlyList<IObjectOrLink> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<Iri>();
        var result = new List<IObjectOrLink>(Math.Min(items.Count, _options.MaxItems));
        foreach (var item in items)
        {
            if (result.Count >= _options.MaxItems)
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
