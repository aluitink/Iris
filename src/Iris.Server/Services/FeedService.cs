using System.Collections.Concurrent;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CollectionPage = Iris.Core.Collections.CollectionPage;
using Stopwatch = System.Diagnostics.Stopwatch;

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
/// <remarks>
/// <strong>Reply filtering (117.1, thread-aware feed).</strong> Replies from followed actors are excluded
/// from the home feed (they appear under the parent post's replies section instead). A reply is detected
/// deterministically via the content object's <c>inReplyTo</c> field (the primary signal); when
/// <c>inReplyTo</c> is absent, the audience heuristic is used as a fallback (a non-public audience
/// indicates a directed reply). The actor's <em>own</em> replies are always kept in the feed. The
/// <c>threadDepth</c> parameter (117.1) enables inclusion of replies up to a given depth:
/// when 1, first-level replies to followed actors' top-level posts are included; when 2, second-level
/// replies are also included. Depth 0 (default) filters all replies.
/// </remarks>
/// <remarks>
/// <strong>Server-side per-actor feed cache.</strong> The merged (pre-thread-filter) feed is cached per
/// actor IRI with a 30-second TTL. Within the window, repeated requests for the same actor return the
/// cached list without re-walking every follow's outbox. The cache is process-local; a multi-instance
/// deployment holds one copy per instance (bounded by the same TTL). Thread-depth, query, type,
/// visibility, and source filters are all applied per-request to the cached list.
/// </remarks>
public sealed class FeedService : IFollowFeedService
{
    private readonly ConcurrentDictionary<Iri, (List<IObjectOrLink> Items, DateTime BuiltUtc)> _feedCache = new();
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;
    private readonly IActorDocumentFetcher _actorDocs;
    private readonly IActivityPubClient _client;
    private readonly FeedOptions _options;
    private readonly IModerationStore? _moderation;
    private readonly IFeedCircuitBreaker _circuitBreaker;
    private readonly ILogger<FeedService> _logger;

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
    /// <param name="circuitBreaker">The per-peer circuit breaker (Phase 146): when a remote follow's
    /// circuit is open, its fetch is skipped (the peer is assumed dead) instead of being re-probed on
    /// every rebuild. Null disables circuit breaking (every remote follow is fetched on every rebuild —
    /// the pre-146 behavior).</param>
    /// <param name="logger">The logger for feed observability (per-follow fan-out timing, item count
    /// by type, cache hit/miss). Null falls back to <see cref="NullLogger{T}"/>.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public FeedService(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        IActorDocumentFetcher actorDocs,
        IActivityPubClient client,
        IOptions<FeedOptions> optionsAccessor,
        IModerationStore? moderation = null,
        IFeedCircuitBreaker? circuitBreaker = null,
        ILogger<FeedService>? logger = null)
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
        _circuitBreaker = circuitBreaker ?? DisabledFeedCircuitBreaker.Instance;
        _logger = logger ?? NullLogger<FeedService>.Instance;
    }

    /// <summary>
    /// Removes all per-actor feed cache entries (test isolation / teardown).
    /// </summary>
    public void ClearFeedCache() => _feedCache.Clear();

    /// <inheritdoc/>
    public void InvalidateActorFeedCache(Iri actorIri, CancellationToken ct = default)
        => _feedCache.TryRemove(actorIri, out _);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(Iri actorIri, string? query = null, string? activityType = null, int? threadDepth = null, Iri? requesterIri = null, string? source = null, bool bypassCache = false, CancellationToken ct = default)
    {
        var feed = await BuildFeedAsync(actorIri, threadDepth, bypassCache, ct).ConfigureAwait(false);

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

        // Audience/visibility filter (139.2-s5, follow-feed surface): drop non-public items (followers-only
        // or direct) that are not addressed to the requesting actor and not authored by them. An
        // anonymous / unsigned request (requesterIri null) therefore sees only public content; the feed's
        // owner (a signed request as the actor) additionally sees their own non-public posts (the author
        // clause) and any non-public items addressed to them; a signed non-recipient sees only public
        // content. Filtering after the query/type filters (and before the handler's paginate) means the
        // viewer is not returned a page dominated by content they cannot legitimately see. A
        // followers-visibility item (to/cc = the author's …/followers collection) is visible to the
        // requester when the requester follows the author — resolved via the follow store (S46): the
        // collection IRI names the followers, not a literal recipient, so a plain IRI match never hits.
        var follows = _persistence.Follows;
        Iri? requester = requesterIri;
        var visible = await Task.WhenAll(feed.Select(async item =>
        {
            var ok = requester is { } r
                ? await VisibilityFilter.IsFeedItemVisibleToAsync(item, r, owner => follows.IsFollowingAsync(r, owner, ct)).ConfigureAwait(false)
                : VisibilityFilter.IsFeedItemVisibleTo(item, null);
            return (item, ok);
        }));
        feed = visible.Where(t => t.ok).Select(t => t.item).ToList();

        // Source filter (unified-home-feed Phase 2): split the feed into "people" (attributedTo has no
        // Group) and "communities" (attributedTo includes a Group). A null/absent source returns the
        // full merged feed (back-compat).
        if (!string.IsNullOrWhiteSpace(source))
        {
            var communitiesOnly = string.Equals(source, "communities", StringComparison.OrdinalIgnoreCase);
            feed = feed.Where(item => IsFromCommunity(item) == communitiesOnly).ToList();
        }

        return feed;
    }

    /// <summary>
    /// Builds (or retrieves from the per-actor cache) the unfiltered followed feed for the given actor:
    /// the actor's <em>own</em> outbox items plus the union of the actor's local and remote follows'
    /// outbox items, newest-first, de-duplicated, capped by <see cref="FeedOptions"/>.
    /// </summary>
    /// <remarks>
    /// The actor's own outbox is always merged in (54.17): a home timeline shows the signed-in actor's own
    /// posts alongside the posts of the actors they follow. The actor is always local here (the feed
    /// endpoint only resolves local actors), so their outbox is read from the local store. The own-outbox
    /// items are prepended before the followed actors' items; <see cref="TruncateDedup"/> de-duplicates by
    /// IRI (a post the actor made cannot also appear in a follow's outbox, but the de-dup is a cheap
    /// safeguard) and caps the result to <see cref="FeedOptions.MaxItems"/>.
    /// </remarks>
    /// <remarks>
    /// <strong>Server-side per-actor caching (feed load feel).</strong> The merged feed is cached per
    /// actor IRI with a short TTL (<see cref="FeedOptions.CacheTtl"/> = 30 s by default). Within the window, a repeated
    /// <c>GetFeedAsync</c> call for the same actor returns the cached item list without re-walking
    /// every follow's outbox (the expensive path). The cache is keyed by the actor's IRI alone (the
    /// merge is deterministic for a given set of follows); thread-depth filtering is applied to the
    /// cached list per-request (the cached list is the <em>pre-thread-filter</em> union, so a depth
    /// parameter can still narrow it without a rebuild). Stale entries beyond the TTL are rebuilt on
    /// the next request. The cache is process-local (a <see cref="ConcurrentDictionary{TKey,TValue}"/>);
    /// in a multi-instance deployment each instance holds its own copy (bounded by the same TTL).
    /// </remarks>
    /// <param name="actorIri">The local actor whose feed is being built.</param>
    /// <param name="threadDepth">When non-null and > 0, replies (from both the actor's own outbox and
    /// followed actors' outboxes) are included in the feed (117.1, 117.5). When null or 0, all replies
    /// are filtered out — the home timeline shows only top-level content.</param>
    /// <param name="bypassCache">When true, the per-actor cache is skipped (the feed is rebuilt and the
    /// cache entry is refreshed).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IReadOnlyList<IObjectOrLink>> BuildFeedAsync(Iri actorIri, int? threadDepth, bool bypassCache, CancellationToken ct)
    {
        if (!bypassCache
            && _feedCache.TryGetValue(actorIri, out var cached)
            && DateTime.UtcNow - cached.BuiltUtc < _options.CacheTtl)
        {
            _logger.LogDebug("Feed cache HIT for {ActorIri} ({Age}s old, {Items} items)",
                actorIri.Value, (int)(DateTime.UtcNow - cached.BuiltUtc).TotalSeconds, cached.Items.Count);
            return ApplyThreadFilter(cached.Items, threadDepth);
        }

        if (!bypassCache)
        {
            _logger.LogDebug("Feed cache MISS for {ActorIri} (rebuilding)", actorIri.Value);
        }
        else
        {
            _logger.LogDebug("Feed cache BYPASS for {ActorIri} (rebuilding)", actorIri.Value);
        }

        var rebuilt = await BuildFeedUncachedAsync(actorIri, ct).ConfigureAwait(false);
        _feedCache[actorIri] = (new List<IObjectOrLink>(rebuilt), DateTime.UtcNow);
        return ApplyThreadFilter(rebuilt, threadDepth);
    }

    /// <summary>
    /// Applies the thread-depth filter to a pre-built feed list: when <paramref name="threadDepth"/> is
    /// null or 0, follow-replies are excluded (the default home-timeline behavior); when &gt; 0, all
    /// items are kept.
    /// </summary>
    private static IReadOnlyList<IObjectOrLink> ApplyThreadFilter(IReadOnlyList<IObjectOrLink> items, int? threadDepth)
    {
        if (threadDepth is > 0)
        {
            return items;
        }

        return items.Where(item => !IsFollowReply(item)).ToList();
    }

    /// <summary>
    /// Builds the unfiltered followed feed for the given actor (no cache): the actor's <em>own</em>
    /// outbox items plus the union of the actor's local and remote follows' outbox items, de-duplicated,
    /// capped by <see cref="FeedOptions"/>.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> BuildFeedUncachedAsync(Iri actorIri, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

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

        // The actor's own posts (54.17): included regardless of follows. The actor is local (the feed
        // endpoint only resolves local actors), so read their outbox from the local store. Own replies
        // to other actors' content are filtered out by default (117.5) — the home timeline shows
        // top-level content; replies are visible on the parent post's page. The threadDepth parameter
        // allows opting in to include own replies (consistent with the followed-actor reply filter).
        //
        // S24-D2 / S36: the local outbox is not pure "own content" — the boost fan-out
        // (AnnounceActivityHandler) records a local follower's *boost of someone else's note* in the
        // follower's own outbox, and actor-document activity (Update/Follow/Like/Undo/Delete) is
        // recorded there too. Without a filter, the home feed is dominated by that foreign content and
        // actor-doc noise, and the actor's real posts get pushed out of the MaxItems cap. Keep only the
        // content items the actor THEMSELVES authored (a Create/Announce whose `actor` is the feed
        // owner). The feed endpoint is owner-gated (private), so this filtering does not affect any
        // cross-instance reader, which federates via the public outbox, not this feed.
        foreach (var item in await _persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false))
        {
            if (IsOwnContentItem(item, actorIri))
            {
                feed.Add(item);
            }
        }

        // 147.2: parallelize the per-follow fan-out. Previously each follow's outbox (local or
        // remote) was awaited sequentially, so a feed with N remote follows took the SUM of all
        // their fetch latencies (11 follows × 0.5–10 s = 7–10 s). With Task.WhenAll the total
        // latency is bounded by the SLOWEST single follow (plus the local DB reads), not the sum.
        // A failed or slow remote contributes an empty list (it must not fail the whole feed),
        // preserving the existing "one broken remote must not fail the feed" guarantee.
        var eligible = ordered
            .Where(f => !blocked.Contains(f) && !muted.Contains(f))
            .ToList();

        var perFollowTimed = await Task.WhenAll(
            eligible.Select(async followIri =>
            {
                var followSw = Stopwatch.StartNew();
                List<IObjectOrLink> items;
                var isLocal = false;
                var skippedByCircuit = false;
                var remoteFetchFailed = false;
                try
                {
                    isLocal = await _localActors.IsLocalActorAsync(followIri, ct).ConfigureAwait(false);
                    if (isLocal)
                    {
                        // A local follow's outbox is read from the local store (no network).
                        items = (await _persistence.Activities.GetOutboxAsync(followIri, ct).ConfigureAwait(false)).ToList();
                    }
                    else
                    {
                        // Phase 146: consult the per-peer circuit breaker before probing a remote
                        // follow. When the peer's circuit is open (dead — DNS/connection refused, or a
                        // half-open probe already in flight) the follow contributes nothing for this
                        // rebuild — both the live outbox walk AND the local delivered-content read are
                        // skipped (the delivered read is part of the per-follow work that is gated on the
                        // circuit, so an open peer's already-received content is held rather than
                        // re-surfaced until the circuit half-opens and a probe succeeds again). This
                        // stops re-probing a dead instance on every 30-s feed rebuild; the trade-off is
                        // that a dead peer's delivered content is paused while its circuit is open.
                        var permitted = await _circuitBreaker.TryAcquireAsync(followIri, ct).ConfigureAwait(false);
                        if (!permitted)
                        {
                            skippedByCircuit = true;
                            items = [];
                        }
                        else
                        {
                            // A remote follow's feed is the union of (a) its outbox walked over the wire and
                            // (b) the content this instance has already received in its inbox from that author
                            // (stored in the object store by the CreateActivityHandler's StoreEmbeddedObjectAsync
                            // when a remote Create was delivered to a local recipient — S25: the delivered post
                            // must surface in the follower's home feed even when the live outbox walk yields
                            // nothing, e.g. a broken/unreachable remote outbox or a fresh delivery not yet
                            // reflected in the walked page). De-duplicated by IRI + content object in
                            // TruncateDedup, so an item present in both is rendered once.
                            var outbox = await FetchRemoteOutboxAsync(followIri, ct).ConfigureAwait(false);
                            items = new List<IObjectOrLink>(outbox);
                            items.AddRange(await GetDeliveredContentAsync(followIri, ct).ConfigureAwait(false));
                            // Phase 146: a remote fetch that returned nothing and did not throw is treated
                            // as a failure for the circuit breaker (a dead peer's outbox resolves to no
                            // items), so a dead instance opens its circuit instead of being re-probed on
                            // every rebuild. A healthy remote that legitimately has an empty outbox is the
                            // rare case (the breaker is opt-in and thresholded); the open duration bounds
                            // the cost. A fetch that threw is a hard failure too.
                            remoteFetchFailed = outbox.Count == 0;
                        }
                    }
                }
                catch
                {
                    // A single broken follow must not fail the whole feed (147.2).
                    items = [];
                    if (!isLocal)
                    {
                        remoteFetchFailed = true;
                    }
                }
                followSw.Stop();

                // Record the outcome for the remote follow's peer (local follows are never tracked).
                // The acquire was only called when the follow was remote and permitted, so the
                // success/failure record is balanced with it.
                if (!isLocal && !skippedByCircuit)
                {
                    if (remoteFetchFailed)
                    {
                        await _circuitBreaker.RecordFailureAsync(followIri, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _circuitBreaker.RecordSuccessAsync(followIri, ct).ConfigureAwait(false);
                    }
                }

                return (Items: items, IsLocal: isLocal, ElapsedMs: followSw.ElapsedMilliseconds, SkippedByCircuit: skippedByCircuit);
            }));

        // Merge in the deterministic IRI order of `eligible` (matches the previous sequential
        // iteration order, so the feed is reproducible for a given set of follows).
        for (var i = 0; i < eligible.Count; i++)
        {
            feed.AddRange(perFollowTimed[i].Items);
        }

        // S77: sort newest-first by published date before the MaxItems cap. Without this, a large own
        // outbox (e.g. 2000+ posts) fills the entire cap and followed content is never shown — the
        // home feed reduces to "own content only" while notifications (which read the inbox, not the
        // outbox merge) still show followed content. Mirrors PublicFeedService.SortByDateNewestFirst.
        feed = SortByDateNewestFirst(feed).ToList();
        var result = TruncateDedup(feed);
        sw.Stop();

        // Phase 146 feed observability: structured log of the build (latency, follow counts,
        // item count by type, slowest follow). Helps diagnose the S36 class of issues (feed
        // dominated by noise) and track feed performance in production. The local/remote split reuses
        // the per-follow determination captured in the fan-out above (no redundant store lookups).
        var localCount = 0;
        var remoteCount = 0;
        var skippedByCircuit = 0;
        var slowestFollowMs = 0L;
        for (var i = 0; i < eligible.Count; i++)
        {
            if (perFollowTimed[i].IsLocal)
            {
                localCount++;
            }
            else
            {
                remoteCount++;
            }

            if (perFollowTimed[i].SkippedByCircuit)
            {
                skippedByCircuit++;
            }

            slowestFollowMs = Math.Max(slowestFollowMs, perFollowTimed[i].ElapsedMs);
        }

        var typeCounts = new Dictionary<string, int>();
        foreach (var item in result)
        {
            var type = item is Activity a && a.Type is { } types && types.FirstOrDefault() is { } t ? t : "Object";
            typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
        }

        _logger.LogInformation(
            "Feed built for {ActorIri}: {TotalMs} ms, {Follows} follows ({Local} local, {Remote} remote), " +
            "{Items} items, slowest follow {SlowestMs} ms, {SkippedByCircuit} follows skipped (circuit open), types: {Types}",
            actorIri.Value, sw.ElapsedMilliseconds, eligible.Count, localCount, remoteCount,
            result.Count, slowestFollowMs, skippedByCircuit, string.Join(", ", typeCounts.Select(kv => $"{kv.Key}={kv.Value}")));

        return result;
    }

    /// <summary>
    /// Reports whether a feed item is a reply made by a followed actor (not a top-level post).
    /// A reply is a <c>Create</c> activity whose content object has an <c>inReplyTo</c> field
    /// (the deterministic primary signal). When <c>inReplyTo</c> is absent, the audience heuristic is
    /// used as a fallback: a <c>to</c> field that names a specific actor IRI (not just the public
    /// sentinel) indicates a directed reply. Top-level posts (no <c>inReplyTo</c>, public-only <c>to</c>)
    /// and non-<c>Create</c> activities (Announce, Like, etc.) return <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The fallback inspects only the <c>to</c> field (not <c>cc</c>): a top-level post's
    /// <c>cc=[followers]</c> (the standard public-post shape) must not be mistaken for a directed
    /// reply. A true reply addresses its parent's author in <c>to</c> (the ActivityPub reply
    /// convention); a top-level post's <c>to</c> is the public sentinel (or absent). S36: the prior
    /// heuristic (any non-public <c>to</c>/<c>cc</c> entry) dropped every top-level post carrying
    /// <c>cc=[followers]</c> — the dominant live wire shape — from the home timeline.
    /// </remarks>
    private static bool IsFollowReply(IObjectOrLink item)
    {
        if (item is not Create create)
        {
            return false;
        }

        var contentObj = create.Object?.FirstOrDefault() as IObject;
        if (contentObj is null)
        {
            return false;
        }

        // Primary signal: inReplyTo is set (deterministic, 117.1).
        if (contentObj.GetParentIri() is not null)
        {
            return true;
        }

        // Fallback: a named (non-public) `to` audience indicates a directed reply (Phase 101
        // heuristic, S36-corrected). Only `to` is inspected: a top-level post's `cc=[followers]`
        // is a public-carbon-copy, not a directed recipient. A `to` of just the public sentinel
        // (or an absent `to`) is a top-level post, not a reply. S46: a `to` that is a followers
        // collection (`…/followers`) is likewise a top-level post (followers-only visibility), not a
        // directed reply — the collection names the owner's followers, never a single addressed actor.
        if (contentObj.To is { } toEntries)
        {
            foreach (var entry in toEntries)
            {
                if (entry.ResolveObjectIri() is { } iri
                    && !iri.IsPublicAudience()
                    && !iri.IsFollowersCollection()
                    // S50: a community (Group) in `to` is a broadcast recipient (the community's
                    // followers), not a single addressed actor. A top-level cross-post to a remote
                    // community (a Page, 138.11) carries the community in `to`; without this exemption
                    // the fallback misclassifies it as a directed reply and the home timeline drops it.
                    && !iri.IsCommunityActorIri())
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether an own-outbox item is <em>content the actor themselves authored</em> — a
    /// <c>Create</c> of a <c>Note</c>/<c>Article</c>/<c>Question</c> or an <c>Announce</c> (boost) whose
    /// <c>actor</c> is the feed owner. Used to filter the actor's own outbox in the home feed (S24-D2 /
    /// S36): the local outbox also contains foreign content mirrored by the boost fan-out (a local
    /// follower's boost of someone else's note is recorded in the follower's own outbox) and
    /// actor-document activity (Update/Follow/Like/Undo/Delete), neither of which belongs in the home
    /// timeline. Items that are not content, or whose actor is a different actor (the foreign boost case),
    /// return <see langword="false"/>.
    /// </summary>
    private static bool IsOwnContentItem(IObjectOrLink item, Iri ownerIri)
    {
        if (item is not Activity activity)
        {
            return false;
        }

        // Content activities only: a Create (a post) or an Announce (a boost the actor themselves made).
        // Actor-document activity (Update/Follow/Like/Undo/Delete/...) is never content.
        var type = activity.Type?.FirstOrDefault();
        if (type is not ("Create" or "Announce"))
        {
            return false;
        }

        // A Create must reference a content object (a Note/Article/Question/Page); a Create of a Group (a
        // community join) or a bare link is not feedable content. S50: a top-level cross-post to a remote
        // (non-Iris, e.g. Lemmy) community carries a Page (138.11) — a Page is content, so the author's
        // home feed must show it (it is recorded in the author's outbox by the local-outbox publish path).
        if (type == "Create")
        {
            var hasContentObject = activity.Object is { } objects
                && objects.Any(o => o is Note || o is Article || o is Question || o is Page);
            if (!hasContentObject)
            {
                return false;
            }
        }

        // The activity must be authored by the feed owner. The `actor` is a bare IRI (an ILink) in the
        // common case (Iris emits "actor": "https://…/u/handle") but may be an embedded object (an
        // IObject carrying an id); both shapes are resolved and compared to the owner IRI. A foreign
        // boost recorded in the owner's outbox has the *booster's* actor, so it is excluded here.
        var actorIri = ResolveActorIri(activity.Actor);
        if (actorIri is null)
        {
            return false;
        }

        return new Iri(actorIri) == ownerIri;
    }

    /// <summary>
    /// Resolves the IRI string of the first resolvable actor reference in an ActivityStreams actor
    /// collection. An <c>actor</c> on an activity is a bare IRI string (an <see cref="ILink"/>) in the
    /// common case but may be an embedded object (an <see cref="IObject"/> carrying an <c>id</c>); both
    /// shapes are accepted so the comparison is correct regardless of how the activity serialized the
    /// actor (mirrors the client's <c>OutboxFilter.ResolveActorIri</c>).
    /// </summary>
    private static string? ResolveActorIri(IEnumerable<IObjectOrLink>? refs)
    {
        if (refs is null)
        {
            return null;
        }

        foreach (var reference in refs)
        {
            if (reference is ILink { Href: { } href })
            {
                return href.ToString();
            }

            if (reference is IObject { Id: { Length: > 0 } id })
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether a feed item is from a community (unified-home-feed Phase 2, <c>?source=</c>
    /// filter): its <c>attributedTo</c> (or, for <c>Create</c>/<c>Announce</c>, the referenced
    /// object's <c>attributedTo</c>) includes a <c>Group</c>. A <c>Group</c> is detected either as an
    /// inline <c>Group</c> object in the attributedTo collection, or as an IRI whose path matches the
    /// common community pattern (<c>/c/</c> segment — Lemmy, Pleroma, Mastodon, Iris).
    /// </summary>
    private static bool IsFromCommunity(IObjectOrLink item)
    {
        if (item is not IObject obj)
        {
            return false;
        }

        // For activities (Create, Announce, etc.), check the referenced object's attributedTo.
        if (obj is Activity activity)
        {
            foreach (var referenced in activity.Object ?? [])
            {
                if (referenced is IObject refObj && AttributedToIncludesGroup(refObj))
                {
                    return true;
                }
            }

            return false;
        }

        // For bare objects (Note, Article, Page, etc.), check their own attributedTo.
        return AttributedToIncludesGroup(obj);
    }

    /// <summary>
    /// Returns true when the object's <c>attributedTo</c> collection includes a <c>Group</c>: either an
    /// inline <c>Group</c> document or an IRI whose path contains a <c>/c/</c> segment (the common
    /// community path in Lemmy, Pleroma, and Iris).
    /// </summary>
    private static bool AttributedToIncludesGroup(IObject obj)
    {
        var attributedTo = obj.AttributedTo;
        if (attributedTo is null)
        {
            return false;
        }

        foreach (var attr in attributedTo)
        {
            // Inline Group document (some platforms embed the full actor in attributedTo).
            if (attr is Group)
            {
                return true;
            }

            // IRI (link): check if the path matches the community pattern.
            if (attr.ResolveObjectIri() is { } iri &&
                iri.Value.Contains("/c/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
            if (item is IObject obj && MatchesQueryAtAnyDepth(obj, normalized))
            {
                matches.Add(item);
            }
        }

        return matches;
    }

    /// <summary>
    /// Returns true when <paramref name="obj"/> (or any content object it embeds, at any nesting depth)
    /// has a <c>content</c> or <c>name</c> that contains <paramref name="query"/> as a substring
    /// (case-insensitive).
    /// </summary>
    /// <remarks>
    /// S96: the match recurses through nested activities, not just one level. A community post delivered
    /// from a remote instance arrives nested (an <c>Announce</c> whose <c>object</c> is a <c>Create</c>
    /// whose <c>object</c> is the actual <c>Page</c>/note), so the post's content sits TWO levels deep.
    /// A single-level match (the pre-S96 behavior) only inspected the item itself and one referenced
    /// object — it saw the <c>Announce</c> and the empty <c>Create</c>, never the <c>Page</c> — and dropped
    /// the post from a query-filtered feed (so cross-instance search for a token in a nested post returned
    /// nothing). Recursing reaches the embedded content object at any depth, mirroring the recursive
    /// unwrap in <c>GlobalSearchService.UnwrapContentObject</c>.
    /// </remarks>
    private static bool MatchesQueryAtAnyDepth(IObject obj, string query)
    {
        if (ContainsInStrings(obj.Content, query) || ContainsInStrings(obj.Name, query))
        {
            return true;
        }

        if (obj is Activity activity)
        {
            foreach (var referenced in activity.Object ?? [])
            {
                if (referenced is IObject refObj && MatchesQueryAtAnyDepth(refObj, query))
                {
                    return true;
                }
            }
        }

        return false;
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
    /// Returns the content this instance has already received in its inbox from <paramref name="actorIri"/>
    /// (a remote followed author), as feed items — the S25 "delivered remote post" half of the home feed.
    /// When a remote <c>Create</c> is delivered to a local recipient, the <see cref="CreateActivityHandler"/>
    /// stores the embedded object in the <see cref="IObjectStore"/> under the object's own IRI (keyed by its
    /// <c>attributedTo</c>). <see cref="IObjectStore.ListByActorAsync"/> lists those objects; each is wrapped
    /// in a synthetic <c>Create</c> (activity IRI = the object IRI) so it flows through the feed's existing
    /// de-dup/coalesce, reply filter, and visibility filter exactly like a wire-walked outbox <c>Create</c>.
    /// </summary>
    /// <remarks>
    /// This is the same store/path the inbox write lands in (the object store), so a delivered remote post
    /// surfaces in the follower's home feed even when the live outbox walk of the remote author contributes
    /// nothing (an unreachable/broken remote outbox, or a delivery not yet reflected in the walked page).
    /// Objects attributed to the author that are not posts (e.g. a <see cref="Tombstone"/>) are skipped. A
    /// store failure contributes nothing (a single broken follow must not fail the whole feed — 147.2).
    /// </remarks>
    private async Task<IReadOnlyList<IObjectOrLink>> GetDeliveredContentAsync(Iri actorIri, CancellationToken ct)
    {
        try
        {
            var objects = await _persistence.Objects.ListByActorAsync(actorIri, ct).ConfigureAwait(false);
            var result = new List<IObjectOrLink>(objects.Count);
            foreach (var obj in objects)
            {
                if (obj is Tombstone)
                {
                    continue; // a deleted object has no feedable content
                }

                var objectIri = obj.ResolveObjectIri();
                if (objectIri is null)
                {
                    continue;
                }

                // Wrap the bare object in a synthetic Create (activity IRI = object IRI) so the feed's
                // Create-oriented de-dup/coalesce and reply/visibility filters apply uniformly. The object
                // is embedded (not link-only), so it renders in place.
                result.Add(new Create
                {
                    Id = objectIri.ToString(),
                    Actor = [new Link { Href = new Uri(actorIri.Value) }],
                    Object = [obj],
                });
            }

            return result;
        }
        catch (Exception)
        {
            // A store failure contributes nothing; a single broken follow must not fail the whole feed.
            return [];
        }
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

    private static IReadOnlyList<IObjectOrLink> SortByDateNewestFirst(IReadOnlyList<IObjectOrLink> feed)
        => feed
            .OrderByDescending(item => ExtractPublishedDate(item))
            .ToList();

    private static DateTime? ExtractPublishedDate(IObjectOrLink item)
    {
        if (item is Activity activity)
        {
            return activity.Published;
        }

        if (item is IObject { Published: { } published })
        {
            return published;
        }

        return null;
    }
}

/// <summary>
/// A no-op <see cref="IFeedCircuitBreaker"/>: every remote follow's fetch is always permitted and the
/// record methods are no-ops. This is the default when the feed circuit breaker is not enabled
/// (<see cref="FeedRemoteFollowCircuitBreakerOptions.FailureThreshold"/> is 0), preserving the pre-146
/// behavior (every remote follow is fetched on every rebuild).
/// </summary>
internal sealed class DisabledFeedCircuitBreaker : IFeedCircuitBreaker
{
    internal static readonly DisabledFeedCircuitBreaker Instance = new();

    /// <summary>
    /// Initializes a new no-op circuit breaker.
    /// </summary>
    public DisabledFeedCircuitBreaker()
    {
    }

    /// <inheritdoc/>
    public Task<bool> TryAcquireAsync(Iri followIri, CancellationToken ct) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task RecordSuccessAsync(Iri followIri, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RecordFailureAsync(Iri followIri, CancellationToken ct) => Task.CompletedTask;
}
