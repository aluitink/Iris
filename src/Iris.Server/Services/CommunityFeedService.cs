using Iris.Client;
using Iris.Core;
using Iris.Server.Caching;
using Iris.Server.Security;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Services;

/// <summary>
/// The default <see cref="ICommunityFeedService"/>: merges the members' outbox activities into a
/// single newest-first feed for a community, and searches that content.
/// </summary>
/// <remarks>
/// For each member of the community, reads the member's outbox (the member's posted activities,
/// newest first) and merges them into a single **newest-first** feed: items are ordered by (outbox
/// position, then member IRI) — a stable, deterministic merge that ranks a member's newest post above
/// its older posts and orders same-position posts by member IRI. The merged items are de-duplicated by
/// activity IRI (keeping the first, i.e. newest, occurrence). A member with no outbox contributes
/// nothing; an unknown community or a community with no members yields an empty feed. The
/// <see cref="SearchCommunityAsync"/> method runs a case-insensitive substring search over the feed's
/// items' <c>content</c>/<c>name</c>.
/// </remarks>
/// <remarks>
/// <strong>Local vs remote members.</strong> A <em>local</em> member's outbox is read from the local
/// activity store (no network). A <em>remote</em> member's outbox is fetched over the wire (walking the
/// outbox's pages, capped by <see cref="FeedOptions.PagesPerActor"/>). A remote outbox that cannot be
/// fetched contributes nothing — a single broken remote must not fail the whole feed. When the service
/// is constructed without an <see cref="ILocalActorResolver"/> (the default), every member is treated
/// as local (the legacy behavior).
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
    private readonly ILocalActorResolver? _localActors;
    private readonly IActorDocumentFetcher? _actorDocs;
    private readonly IActivityPubClient? _client;
    private readonly FeedOptions _options;
    private readonly Iri? _instanceBase;

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
        : this(persistence, communities, null, null, null, new FeedOptions())
    {
    }

    /// <summary>
    /// Initializes a new feed service with full local/remote member support.
    /// </summary>
    /// <param name="persistence">The persistence provider (the community + activity stores). Must not be null.</param>
    /// <param name="communities">The community store (19.5.4): when present, a member the community has
    /// <em>blocked</em> or <em>muted</em> is excluded from the feed. Null disables community-moderation
    /// filtering.</param>
    /// <param name="localActors">Resolves whether a member is local (its outbox is read from the local
    /// store) or remote (its outbox is fetched over the wire). Null (the default) treats every member
    /// as local (the legacy behavior).</param>
    /// <param name="actorDocs">Fetches a remote member's document to read its <c>outbox</c> IRI. Required
    /// when <paramref name="localActors"/> is non-null.</param>
    /// <param name="client">Fetches a remote member's outbox pages over the wire. Required when
    /// <paramref name="localActors"/> is non-null.</param>
    /// <param name="options">The feed options (pages per remote member + max items).</param>
    /// <param name="instanceBase">
    /// The instance's base IRI (e.g. <c>https://iris.example</c>). When set, a community contributor is
    /// only read from the local activity store when its IRI is hosted on this base; a community on
    /// another host (a followed remote community, e.g. a Lemmy community persisted by the remote
    /// community persister) is fetched over the wire (139.3 s5). When null, any community in the
    /// community store is treated as local (the legacy behavior for in-process test hosts without a base).
    /// </param>
    public CommunityFeedService(
        IPersistenceProvider persistence,
        ICommunityStore? communities,
        ILocalActorResolver? localActors,
        IActorDocumentFetcher? actorDocs,
        IActivityPubClient? client,
        FeedOptions options,
        Iri? instanceBase = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _communities = communities;
        _localActors = localActors;
        _actorDocs = actorDocs;
        _client = client;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _instanceBase = instanceBase;
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

        // Members are followers (change 221): the community's followers set is the membership.
        var memberIris = await _persistence.Communities.GetFollowersAsync(communityIri, ct).ConfigureAwait(false);

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
        // De-duplicate by activity IRI (keep the first, i.e. newest, occurrence). A local member's
        // outbox is read from the local activity store; a remote member's outbox is fetched over the
        // wire (walking the outbox's pages, capped by FeedOptions.PagesPerActor).
        //
        // 40.3: filter to community-tagged posts only. A member's personal posts (where the community
        // is NOT in the note's <c>attributedTo</c>) are excluded from the community feed. Only content
        // explicitly tagged to the community (the note's <c>attributedTo</c> carries the community IRI)
        // appears. This applies to <c>Create</c> (the embedded Note), <c>Announce</c> (the referenced
        // object), and <c>Like</c> (the liked object). A member's outbox items that are not community-
        // tagged are dropped before the merge.
        var communityIriValue = communityIri.Value;

        // Member branch: a member's content is admitted only when it is tagged to this community
        // (40.3 — the note's attributedTo carries the community IRI).
        // 147.2 follow-up: parallelize the per-member fan-out. Previously each member's outbox was
        // awaited sequentially, so a community with N remote members took the SUM of all fetch
        // latencies. With Task.WhenAll the total latency is bounded by the SLOWEST single member,
        // not the sum. A failed/slow member contributes an empty list (preserves the "one broken
        // remote must not fail the feed" guarantee).
        var memberResults = await Task.WhenAll(
            orderedMembers.Select(async memberIri =>
            {
                try
                {
                    return await MergeContributorOutboxAsync(
                        memberIri, communityIriValue, requireCommunityTagged: true, ct);
                }
                catch
                {
                    return (new List<(int Position, IObjectOrLink Item)>(), new List<IObjectOrLink>());
                }
            }));

        // Peering (89): the actors (communities or persons) the community follows contribute their
        // content to the feed too. A followed actor's content is attributed to <em>that</em> actor, not
        // this community, so it is admitted without the community-tagged filter — that is what makes the
        // community's unified feed a federated (peered) feed. A member the community also follows
        // contributes only once (deduplicated by activity IRI below).
        // 147.2 follow-up: parallelize the per-follow fan-out (same rationale as the member branch).
        var follows = await _persistence.Communities.GetFollowsAsync(communityIri, ct).ConfigureAwait(false);
        var orderedFollows = follows
            .OrderBy(f => f.Value, StringComparer.Ordinal)
            .ToList();
        var followResults = await Task.WhenAll(
            orderedFollows.Select(async followedIri =>
            {
                try
                {
                    return await MergeContributorOutboxAsync(
                        followedIri, communityIriValue, requireCommunityTagged: false, ct);
                }
                catch
                {
                    return (new List<(int Position, IObjectOrLink Item)>(), new List<IObjectOrLink>());
                }
            }));

        // Merge the results in deterministic IRI order (members first, then follows), applying
        // cross-contributor dedup by activity IRI (keep the first, i.e. newest, occurrence).
        var seen = new HashSet<Iri>();
        var merged = new List<(int Position, Iri ContributorIri, IObjectOrLink Item)>();
        var remoteOutboxItems = new List<IObjectOrLink>();

        for (var i = 0; i < orderedMembers.Count; i++)
        {
            var (memberItems, _) = memberResults[i];
            foreach (var (position, item) in memberItems)
            {
                if (item is IObject { Id: { Length: > 0 } id })
                {
                    if (!seen.Add(new Iri(id)))
                    {
                        continue;
                    }
                }
                merged.Add((position, orderedMembers[i], item));
            }
        }

        for (var i = 0; i < orderedFollows.Count; i++)
        {
            var (followItems, remoteItems) = followResults[i];
            foreach (var (position, item) in followItems)
            {
                if (item is IObject { Id: { Length: > 0 } id })
                {
                    if (!seen.Add(new Iri(id)))
                    {
                        continue;
                    }
                }
                merged.Add((position, orderedFollows[i], item));
            }
            // Collect remote outbox items for the 138.20 backfill persistence.
            remoteOutboxItems.AddRange(remoteItems);
        }

        // 138.20 (full-thread backfill on first peer): persist the remote outbox items' embedded
        // objects to the local object store and record the Create activities in the community's local
        // members' outboxes, so the historical content is available locally (not just proxied).
        if (remoteOutboxItems.Count > 0)
        {
            await PersistRemoteOutboxItemsAsync(communityIri, remoteOutboxItems, ct).ConfigureAwait(false);
        }

        var feed = merged
            .OrderBy(m => m.Position)
            .ThenBy(m => m.ContributorIri.Value, StringComparer.Ordinal)
            .Select(m => m.Item)
            .ToList();

        return TruncateDedup(feed);
    }

    /// <summary>
    /// Reads a single contributor's outbox (a member or a followed actor) and returns its items,
    /// subject to the contributor's community-tag filter. A contributor whose outbox cannot be read
    /// contributes nothing (an empty list). The caller is responsible for cross-contributor dedup
    /// (by activity IRI) when merging the results. Remote (wire-fetched) items are also returned
    /// for the 138.20 backfill persistence.
    /// </summary>
    /// <param name="contributorIri">The IRI of the actor whose outbox is read.</param>
    /// <param name="communityIriValue">The community's IRI (the community-tag filter's target).</param>
    /// <param name="requireCommunityTagged">When true, only items tagged to the community are admitted
    /// (the member branch); when false, the contributor's own content is admitted (the peered/followed
    /// branch).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (Items, RemoteItems): the contributor's admitted items (position, item) and
    /// the subset of those items that are remote (wire-fetched, eligible for backfill).</returns>
    private async Task<(List<(int Position, IObjectOrLink Item)> Items, List<IObjectOrLink> RemoteItems)> MergeContributorOutboxAsync(
        Iri contributorIri,
        string communityIriValue,
        bool requireCommunityTagged,
        CancellationToken ct)
    {
        IReadOnlyList<IObjectOrLink> outbox =
            await ReadOutboxAsync(contributorIri, ct).ConfigureAwait(false);
        // isRemote must agree with ReadOutboxAsync's routing: a followed contributor is remote (its
        // items are wire-fetched and eligible for the 138.20 backfill) when it is NOT a local community
        // (hosted on the instance base — 139.3 s5) AND not a local actor. A REMOTE community persisted
        // to the community store by the remote community persister (135.1) is wire-fetched here, so it
        // must count as remote; treating it as local (the pre-139.3 s5 behavior) would silently skip the
        // backfill persistence for its content.
        var isRemote = !requireCommunityTagged &&
                       _localActors is not null &&
                       _actorDocs is not null &&
                       _client is not null &&
                       !(await _persistence.Communities.TryGetCommunityAsync(contributorIri, out _, ct).ConfigureAwait(false)
                          && IsLocalCommunity(contributorIri)) &&
                       !await _localActors.IsLocalActorAsync(contributorIri, ct).ConfigureAwait(false);
        var items = new List<(int Position, IObjectOrLink Item)>();
        var remoteItems = new List<IObjectOrLink>();
        for (var position = 0; position < outbox.Count; position++)
        {
            var item = outbox[position];

            if (requireCommunityTagged && !IsCommunityTagged(item, communityIriValue))
            {
                continue;
            }

            items.Add((position, item));
            if (isRemote)
            {
                remoteItems.Add(item);
            }
        }
        return (items, remoteItems);
    }

    /// <summary>
    /// 138.20 (full-thread backfill on first peer): persists the embedded objects from remote outbox
    /// items to the local object store and records the <c>Create</c> activities in the community's local
    /// members' outboxes. This makes the historical content available locally (not just proxied), so
    /// the community feed still renders correctly when the remote is offline. Idempotent: objects
    /// already in the store are not re-stored (the object store's <c>PutObjectAsync</c> is a
    /// last-write-wins upsert).
    /// </summary>
    /// <param name="communityIri">The community whose members' outboxes the <c>Create</c> activities
    /// are recorded into.</param>
    /// <param name="items">The remote outbox items to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PersistRemoteOutboxItemsAsync(
        Iri communityIri,
        IReadOnlyList<IObjectOrLink> items,
        CancellationToken ct)
    {
        // Members are followers (change 221): the backfill is recorded in each follower's outbox.
        var members = await _persistence.Communities.GetFollowersAsync(communityIri, ct).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Unwrap the Lemmy relay envelope: an Announce whose object is an embedded Create.
            Create? create = null;
            if (item is Announce announce)
            {
                var objRef = announce.Object?.FirstOrDefault();
                create = objRef as Create;
            }
            else if (item is Create bareCreate)
            {
                create = bareCreate;
            }

            if (create is null)
            {
                continue;
            }

            // Extract the embedded content object (Page/Note/Article) from the Create.
            var contentRef = create.Object?.FirstOrDefault();
            if (contentRef is not IObject contentObj || contentObj is KristofferStrube.ActivityStreams.Tombstone)
            {
                continue;
            }

            if (contentObj.Id is not { Length: > 0 } contentIriValue)
            {
                continue;
            }

            var contentIri = new Iri(contentIriValue);

            // Store the content object in the local object store (idempotent upsert).
            if (!await _persistence.Objects.TryGetObjectAsync(contentIri, out _, ct).ConfigureAwait(false))
            {
                await _persistence.Objects.PutObjectAsync(contentObj, ct).ConfigureAwait(false);
            }

            // Record the Create in each local member's outbox (so the post surfaces in the community
            // feed from the local store).
            foreach (var memberIri in members)
            {
                await _persistence.Activities.AddToOutboxAsync(memberIri, create, ct).ConfigureAwait(false);
            }
        }
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
            // content/name of the objects referenced anywhere in its envelope. S53: remote (e.g. Lemmy)
            // communities arrive as an Announce of an embedded Create of a Note — the relay envelope
            // (138.20) — so a match at the first level (the Create) misses the content two levels down;
            // the recursive walk mirrors the backfill's unwrap (PersistRemoteOutboxItemsAsync).
            if (item is IObject obj && ItemMatchesQuery(obj, normalized))
            {
                matches.Add(item);
            }
        }

        return matches;
    }

    /// <summary>
    /// Returns whether <paramref name="obj"/> (or any object reachable through its activity envelope)
    /// contains <paramref name="query"/> in its <c>content</c> or <c>name</c>. The walk is bounded: an
    /// activity's referenced objects are visited, but a content object's own <c>object</c> reference
    /// (a Note's object, e.g. an attachment) is not traversed — only the activity envelope is unwrapped.
    /// </summary>
    private static bool ItemMatchesQuery(IObject obj, string query, int depth = 0)
    {
        if (ContainsInStrings(obj.Content, query) || ContainsInStrings(obj.Name, query))
        {
            return true;
        }

        // Guard the recursion: the envelope is shallow (Announce -> Create -> Note) and content objects
        // are not descended into, but a depth cap protects against a cyclic reference in stored data.
        if (depth >= 4)
        {
            return false;
        }

        if (obj is Activity activity)
        {
            foreach (var referenced in activity.Object ?? [])
            {
                if (referenced is IObject refObj && ItemMatchesQuery(refObj, query, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a member's outbox: local members from the local activity store, remote members over the
    /// wire (walking the outbox's pages, capped by <see cref="FeedOptions.PagesPerActor"/>). A remote
    /// outbox that cannot be fetched contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> ReadOutboxAsync(Iri contributorIri, CancellationToken ct)
    {
        // When the local-actor resolver is not configured, every contributor is treated as local (the
        // legacy behavior: the outbox is read from the local activity store).
        if (_localActors is null || _actorDocs is null || _client is null)
        {
            return await _persistence.Activities.GetOutboxAsync(contributorIri, ct).ConfigureAwait(false);
        }

        // Peering (89): a community (Group) contributor's outbox is read from the local activity store
        // when it is a LOCAL community. A community's outbox is always resolvable locally (a community
        // the reader's community follows that is local has a local outbox); routing it through the
        // remote-wire path (which would fail for an in-process test host, or be an unnecessary
        // round-trip) is avoided by treating a known local community as local regardless of the
        // person-actor resolver (which only consults the person store).
        //
        // IMPORTANT (139.3 s5): the community check must ALSO confirm the community is hosted on this
        // instance. A REMOTE community the instance has interacted with is persisted to the durable
        // store by the remote community persister (135.1), so <c>TryGetCommunityAsync</c> returns true
        // for remote communities too. Without the host-locality gate, a followed REMOTE community
        // (e.g. a Lemmy community) is misrouted to its (empty) local outbox instead of being fetched
        // over the wire — the first-peer backfill (138.20) silently captures nothing. When no instance
        // base is configured (some in-process test hosts), the legacy behavior is preserved: any
        // community in the community store is treated as local.
        if (await _persistence.Communities.TryGetCommunityAsync(contributorIri, out _, ct).ConfigureAwait(false)
            && IsLocalCommunity(contributorIri))
        {
            return await _persistence.Activities.GetOutboxAsync(contributorIri, ct).ConfigureAwait(false);
        }

        var isLocal = await _localActors.IsLocalActorAsync(contributorIri, ct).ConfigureAwait(false);
        if (isLocal)
        {
            return await _persistence.Activities.GetOutboxAsync(contributorIri, ct).ConfigureAwait(false);
        }

        return await FetchRemoteOutboxAsync(contributorIri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether a community IRI is hosted on this instance (139.3 s5). When no instance base is
    /// configured (some in-process test hosts), every community is treated as local (the legacy
    /// behavior) — there is no host to discriminate against. When a base is configured, only a
    /// community whose IRI starts with the base prefix is local; a community on another host (a
    /// followed remote community persisted by the remote community persister) is remote and must be
    /// fetched over the wire.
    /// </summary>
    private bool IsLocalCommunity(Iri communityIri)
    {
        if (_instanceBase is not { } instanceBase)
        {
            return true;
        }

        var prefix = instanceBase.Value.TrimEnd('/');
        return communityIri.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks a remote member's outbox (up to <see cref="FeedOptions.PagesPerActor"/> pages) over the
    /// wire and returns the items. A remote that cannot be resolved or fetched contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> FetchRemoteOutboxAsync(Iri memberIri, CancellationToken ct)
    {
        // Read the remote member's document to get its outbox IRI (a remote outbox is not always at the
        // conventional {actor}/outbox, so the advertised IRI is authoritative). The library's
        // collection properties are typed as a single <c>Link</c> (the OneOrMultiple shape), so the
        // first entry is read via its <c>Href</c>; when absent, fall back to the ActivityPub convention.
        //
        // The IActorDocumentFetcher contract is "return null, do not throw" on fetch failure, but the
        // implementation can still throw (a transport error, timeout, or a signing-key failure in the
        // outbound actor-doc fetch propagates uncaught). Guard it the same way the outbox walk below is
        // guarded: a broken remote member-document contributes nothing rather than failing the whole feed.
        Actor? actor = null;
        try
        {
            actor = await _actorDocs!.GetActorAsync(memberIri, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A remote member-document that errors (network, timeout, signing) contributes nothing; a
            // single broken remote must not fail the whole feed.
        }

        var outboxIri = actor?.Outbox is { } outboxRef
            ? outboxRef.ResolveCollectionIri() ?? memberIri.OutboxOf()
            : memberIri.OutboxOf();

        // Walk the outbox through the shared client enumeration (it resolves the collection's `first`
        // page, then follows `next` across pages — handling both the page-1 OrderedCollection shape and
        // the page-N>1 OrderedCollectionPage shape). Cap the walk at PagesPerActor pages; a fetch
        // failure (404, network error, not a page) yields nothing, so a broken remote contributes no
        // items rather than failing the whole feed.
        var items = new List<IObjectOrLink>();
        var pagesWalked = 0;
        try
        {
            await foreach (var page in _client!.GetCollectionAsync(outboxIri, new CollectionQuery(), ct).ConfigureAwait(false))
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
    /// Truncates the merged feed to <see cref="FeedOptions.MaxItems"/>. De-duplication is already
    /// applied during the merge; this cap bounds the total item count.
    /// </summary>
    private IReadOnlyList<IObjectOrLink> TruncateDedup(IReadOnlyList<IObjectOrLink> items)
    {
        if (items.Count <= _options.MaxItems)
        {
            return items;
        }

        return items.Take(_options.MaxItems).ToList();
    }

    /// <summary>
    /// Returns true when the feed item is community-tagged: the community IRI appears in the
    /// <c>attributedTo</c> of the note (for a <c>Create</c>), the referenced object (for an
    /// <c>Announce</c> or <c>Like</c>), or the item itself (a bare object). Items that are not
    /// community-tagged are excluded from the community feed (40.3). S53: the check recurses through
    /// the activity envelope — a remote (Lemmy) relay item is an <c>Announce</c> whose object is an
    /// embedded <c>Create</c> of the tagged <c>Note</c> (138.20), so the tag lives two levels down.
    /// The same bounded walk as <see cref="ItemMatchesQuery"/> is used, so the feed and the community
    /// search admit exactly the same items.
    /// </summary>
    private static bool IsCommunityTagged(IObjectOrLink item, string communityIriValue)
    {
        return item is IObject obj && TagWalk(obj, communityIriValue, depth: 0);
    }

    private static bool TagWalk(IObject obj, string communityIriValue, int depth)
    {
        if (HasCommunityInAttributedTo(obj, communityIriValue))
        {
            return true;
        }

        // The envelope is shallow (Announce -> Create -> Note); the depth cap protects against a cyclic
        // reference in stored data.
        if (depth >= 4 || obj is not Activity activity)
        {
            return false;
        }

        foreach (var referenced in activity.Object ?? [])
        {
            if (referenced is IObject refObj && TagWalk(refObj, communityIriValue, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the object's <c>attributedTo</c> collection contains the community IRI.
    /// </summary>
    private static bool HasCommunityInAttributedTo(IObject obj, string communityIriValue)
    {
        var attributedTo = (obj as ActivityObject)?.AttributedTo;
        if (attributedTo is null)
        {
            return false;
        }

        foreach (var attr in attributedTo)
        {
            if (attr.ResolveObjectIri() is { } iri &&
                string.Equals(iri.Value, communityIriValue, StringComparison.Ordinal))
            {
                return true;
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
}
