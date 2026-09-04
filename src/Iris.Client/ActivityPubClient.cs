using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Client;

/// <summary>
/// The default <see cref="IActivityPubClient"/>. Performs signed HTTP requests against remote
/// ActivityPub servers and operates on <c>KristofferStrube.ActivityStreams</c> types.
/// </summary>
/// <remarks>
/// Requests are signed by the <see cref="SigningHandler"/> (wired into the
/// <see cref="HttpMessageHandler"/> pipeline) using the <see cref="SigningProfile.ClientToServer"/>
/// profile for bodyless GETs and the <see cref="SigningProfile.ServerToServer"/> profile for
/// body-carrying POSTs. Responses are deserialized into <see cref="IObjectOrLink"/> and then
/// pattern-matched — never into a concrete type (see the coding-style rules for 3rd-party
/// ActivityStreams types).
/// </remarks>
public sealed class ActivityPubClient : IActivityPubClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHandler;
    private readonly ActorCache? _actorCache;
    private readonly CollectionPageCache? _collectionPageCache;

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/>.
    /// </summary>
    /// <param name="http">The HTTP client (its handler pipeline should include a
    /// <see cref="SigningHandler"/> for signed requests). The client does not dispose it.</param>
    public ActivityPubClient(HttpClient http)
        : this(http, null, false, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> that owns its <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="handler">The handler pipeline (typically a <see cref="SigningHandler"/> over a
    /// <see cref="HttpClientHandler"/>). The signing handler's <see cref="SigningHandler.ActorId"/>
    /// must be set before sending signed requests.</param>
    public ActivityPubClient(HttpMessageHandler handler)
        : this(null, handler, true, null, null)
    {
    }

    private ActivityPubClient(
        HttpClient? http,
        HttpMessageHandler? handler,
        bool disposeHandler,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache)
    {
        if (http is null && handler is null)
        {
            throw new ArgumentException("Either http or handler must be provided.", nameof(http));
        }

        _http = http ?? new HttpClient(handler!, disposeHandler);
        _ownsHandler = disposeHandler;
        _actorCache = actorCache;
        _collectionPageCache = collectionPageCache;
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> with optional read-through caches.
    /// </summary>
    /// <param name="http">The HTTP client (its handler pipeline should include a
    /// <see cref="SigningHandler"/> for signed requests). The client does not dispose it.</param>
    /// <param name="actorCache">Optional cache for <see cref="GetObjectAsync(Iri, CancellationToken)"/> reads. Null disables actor caching.</param>
    /// <param name="collectionPageCache">Optional cache for <see cref="GetCollectionAsync"/> page reads. Null disables page caching.</param>
    public ActivityPubClient(
        HttpClient http,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache)
        : this(http, null, false, actorCache, collectionPageCache)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> that owns its <see cref="HttpClient"/>, with
    /// optional read-through caches.
    /// </summary>
    /// <param name="handler">The handler pipeline (typically a <see cref="SigningHandler"/> over a
    /// <see cref="HttpClientHandler"/>).</param>
    /// <param name="actorCache">Optional cache for <see cref="GetObjectAsync(Iri, CancellationToken)"/> reads. Null disables actor caching.</param>
    /// <param name="collectionPageCache">Optional cache for <see cref="GetCollectionAsync"/> page reads. Null disables page caching.</param>
    public ActivityPubClient(
        HttpMessageHandler handler,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache)
        : this(null, handler, true, actorCache, collectionPageCache)
    {
    }

    /// <summary>
    /// Disposes the owned <see cref="HttpClient"/> (and its handler pipeline).
    /// </summary>
    public void Dispose()
    {
        if (_ownsHandler)
        {
            _http.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
    {
        if (_actorCache is null)
        {
            return await GetObjectFromNetworkAsync(objectId, ct).ConfigureAwait(false);
        }

        var (value, _) = await _actorCache.GetAsync(
            objectId,
            bypassCache: false,
            async iri => await GetObjectFromNetworkAsync(iri, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return value;
    }

    /// <summary>
    /// Fetches an object from the network (bypassing any actor cache) and deserializes it.
    /// </summary>
    /// <param name="objectId">The IRI of the object to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized object, or null if the request failed or the body was empty.</returns>
    private Task<IObject?> GetObjectFromNetworkAsync(Iri objectId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, objectId.Value);
        return GetObjectAsync(request, ct);
    }

    /// <summary>
    /// Fetches a collection page from the network, appending <c>?refresh=true</c> (or
    /// <c>&amp;refresh=true</c>) when <paramref name="refresh"/> is set. The Iris server caches the
    /// rendered local collection page (outbox, followers, following, liked, blocks, flags, mutes,
    /// relays) and re-renders it only on <c>?refresh=true</c>; without this, a client read that follows
    /// a write (e.g. relays after a subscribe/unsubscribe) would observe the stale cached page until the
    /// 60s fresh window lapses. The query parameter is only meaningful to Iris servers and is ignored by
    /// non-Iris ActivityPub implementations.
    /// </summary>
    private Task<IObject?> GetCollectionPageFromNetworkAsync(Iri pageIri, bool refresh, CancellationToken ct)
    {
        var uri = refresh
            ? (pageIri.Value.Contains('?', StringComparison.Ordinal)
                ? $"{pageIri.Value}&refresh=true"
                : $"{pageIri.Value}?refresh=true")
            : pageIri.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return GetObjectAsync(request, ct);
    }

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
        => (await GetObjectAsync(actorId, ct).ConfigureAwait(false)) as Actor;

    /// <inheritdoc/>
    public async Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default)
    {
        var nodeInfoIri = new Iri($"{instanceBase}/nodeinfo/2.0");
        using var request = new HttpRequestMessage(HttpMethod.Get, nodeInfoIri.Value);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return NodeInfo.FromJson(json);
    }

    /// <inheritdoc/>
    public async Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default)
    {
        if (activity is not Activity)
        {
            throw new ArgumentException("The object must be an Activity to deliver.", nameof(activity));
        }

        var json = ActivityJson.Serialize(activity);
        var body = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, targetId.Value)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Decision 055: when the server is the id authority it returns the created object (with its
        // minted id) in the 2xx body. Parse it so the caller can learn the id to reference for any
        // follow-up activity (an Undo, a reply, a delete). A non-2xx, empty, or non-Activity body yields
        // a null MintedId (the caller already has IsSuccess/StatusCode to decide how to proceed).
        var mintedId = response.IsSuccessStatusCode ? ExtractMintedId(bodyText) : null;
        return new DeliveryResult((int)response.StatusCode, response.IsSuccessStatusCode, bodyText, mintedId);
    }

    /// <summary>
    /// Parses a delivery response body (the created object the server returned, decision 055) and returns
    /// its <c>id</c> when present, or null when the body is empty, not valid ActivityStreams JSON, or
    /// carries no id. The id is read from the deserialized <see cref="IObjectOrLink"/> regardless of its
    /// concrete type (an Activity such as a Create, or a bare object).
    /// </summary>
    /// <param name="body">The response body text.</param>
    /// <returns>The activity's <c>id</c>, or null when it cannot be determined.</returns>
    private static string? ExtractMintedId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = ActivityJson.Deserialize<IObjectOrLink>(body);
            return parsed?.Id;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not ActivityStreams JSON (or an unexpected shape); no minted id to surface.
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // The follow is published to the follower's OWN outbox (the write surface for the activities an
        // actor authors — the client never addresses a recipient's inbox for an activity it authors) and
        // is signed by the pipeline as actorId. The server records it in the follower's outbox and then
        // delivers it to the target's inbox (the server owns the recipient hop).
        //
        // Decision 055 (server is the object-id authority): the client sends only the follow's *shape*
        // (actor + object) — no `Id`. The server mints the follow's id (an unguessable ULID) and returns
        // the created follow in the 2xx body, so the caller reads <see cref="DeliveryResult.MintedId"/>
        // to learn the id it should pass to <see cref="UndoFollowAsync"/> later.
        var follow = new Follow
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), follow, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default)
    {
        // An un-follow is the ActivityStreams inverse of a Follow: an Undo whose object references the
        // original Follow by IRI. Per the delivery model, the Undo is published to the follower's OWN
        // outbox (actorId) — the party that made the follow undoes it, and an authored activity always
        // flows through the acting actor's own outbox — not the un-followed actor's inbox. The server
        // records it and delivers it to the target's inbox (the server owns the recipient hop).
        //
        // Decision 055 (learned-id references): <paramref name="originalFollowId"/> is the id the server
        // minted for the original follow (learned from <see cref="DeliveryResult.MintedId"/> when the
        // follow was made via <see cref="FollowAsync"/>), not a client-derived formula — the client never
        // recomputes the server's ids. The client sends only the Undo's shape (actor + object); the server
        // mints the Undo's own id and returns it in the 2xx body.
        var undo = new Undo
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = originalFollowId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
    {
        // An Accept is the followed actor's response to an inbound Follow: it is published to the
        // FOLLOWED actor's OWN outbox (actorId) — the party that decides the follow authors the Accept,
        // and an authored activity always flows through the acting actor's own outbox — and signed by the
        // pipeline as actorId. The object references the original Follow by its id (<paramref
        // name="followIri"/>, the id the follower learned from its own outbox). The server records the
        // Accept in the actor's outbox, ensures the follower→actor edge, and delivers the Accept to the
        // follower's inbox (the server owns the recipient hop).
        //
        // Decision 055: the client sends only the Accept's shape (actor + object); the server mints the
        // Accept's id (an unguessable ULID) and returns it in the 2xx body.
        var accept = new Accept
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = followIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), accept, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
    {
        // A Reject is the followed actor's refusal of an inbound Follow: it is published to the
        // FOLLOWED actor's OWN outbox (actorId) and signed by the pipeline as actorId. The object
        // references the original Follow by its id (<paramref name="followIri"/>, the id the follower
        // learned from its own outbox). The server records the Reject in the actor's outbox, removes the
        // provisional follower→actor edge, and delivers the Reject to the follower's inbox (the server
        // owns the recipient hop).
        //
        // Decision 055: the client sends only the Reject's shape (actor + object); the server mints the
        // Reject's id (an unguessable ULID) and returns it in the 2xx body.
        var reject = new Reject
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = followIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), reject, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default)
    {
        // A Join is delivered to the community's inbox (the membership's owner). The community's
        // MembershipActivityHandler interprets it: when manuallyApprovesMembers is set, the server
        // records a pending join request; otherwise the server auto-grants membership (19.5.2).
        //
        // Decision 055: the client sends only the Join's shape (actor + object); the server mints the
        // Join's id (an unguessable ULID) and returns it in the 2xx body.
        var join = new Join
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = communityIri.Uri }],
        };

        return DeliverAsync(communityIri.InboxOf(), join, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
    {
        // An Accept of a join request is published to the community's OWN outbox (the operator decides).
        // The object references the original Join by its id. The server adds the requesting actor as a
        // member and removes the pending join request (19.5.2).
        //
        // Decision 055: the client sends only the Accept's shape (actor + object); the server mints the
        // Accept's id and returns it in the 2xx body.
        var accept = new Accept
        {
            Actor = [new Link { Href = communityIri.Uri }],
            Object = [new Link { Href = joinIri.Uri }],
        };

        return DeliverAsync(communityIri.OutboxOf(), accept, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default)
    {
        // A Reject of a join request is published to the community's OWN outbox (the operator decides).
        // The object references the original Join by its id. The server removes the pending join request
        // without granting membership (19.5.2).
        //
        // Decision 055: the client sends only the Reject's shape (actor + object); the server mints the
        // Reject's id and returns it in the 2xx body.
        var reject = new Reject
        {
            Actor = [new Link { Href = communityIri.Uri }],
            Object = [new Link { Href = joinIri.Uri }],
        };

        return DeliverAsync(communityIri.OutboxOf(), reject, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> SetManuallyApprovesMembersAsync(Iri communityIri, bool enabled, CancellationToken ct = default)
    {
        // An AP-native settings change (change 217): the community publishes an Add (enable) or Remove
        // (disable) of its OWN document carrying the manuallyApprovesMembers extension, to its own outbox.
        // The server (RecordCommunityAddAsync / RecordCommunityRemoveAsync) detects that the object is the
        // community's own document and updates the stored community's ExtensionData accordingly.
        //
        // Decision 055: the client sends only the activity's shape (actor + object); the server mints the
        // activity's id and returns it in the 2xx body.
        var objectWithFlag = new KristofferStrube.ActivityStreams.Object
        {
            Id = communityIri.Value,
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [ActivityPubExtensionNames.ManuallyApprovesMembers] =
                    System.Text.Json.JsonSerializer.SerializeToElement(enabled),
            },
        };

        var activity = enabled
            ? (Activity)new Add
            {
                Actor = [new Link { Href = communityIri.Uri }],
                Object = [objectWithFlag],
            }
            : new Remove
            {
                Actor = [new Link { Href = communityIri.Uri }],
                Object = [objectWithFlag],
            };

        return DeliverAsync(communityIri.OutboxOf(), activity, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> SetManuallyApprovesFollowersAsync(Iri actorIri, bool enabled, CancellationToken ct = default)
    {
        // An AP-native settings change (22.6.1): the actor publishes an Add (enable) or Remove (disable)
        // of its OWN document carrying the manuallyApprovesFollowers extension, to its own outbox. The
        // server (RecordPersonAddAsync / RecordPersonRemoveAsync) detects that the object is the actor's
        // own document and updates the stored actor's ExtensionData accordingly.
        //
        // Decision 055: the client sends only the activity's shape (actor + object); the server mints the
        // activity's id and returns it in the 2xx body.
        var objectWithFlag = new KristofferStrube.ActivityStreams.Object
        {
            Id = actorIri.Value,
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [ActivityPubExtensionNames.ManuallyApprovesFollowers] =
                    System.Text.Json.JsonSerializer.SerializeToElement(enabled),
            },
        };

        var activity = enabled
            ? (Activity)new Add
            {
                Actor = [new Link { Href = actorIri.Uri }],
                Object = [objectWithFlag],
            }
            : new Remove
            {
                Actor = [new Link { Href = actorIri.Uri }],
                Object = [objectWithFlag],
            };

        return DeliverAsync(actorIri.OutboxOf(), activity, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
    {
        // A like is published to the liker's OWN outbox (the write surface for the activities an actor
        // authors): the instance records the like edge (liker → object) in the liker's `liked` collection
        // and the outbox, and the server federates it to the object's owner. A content object (the liked
        // note) has no inbox of its own — only actors do — so the recipient hop belongs to the server.
        //
        // Decision 055: the client sends only the Like's shape (actor + object); the server mints the
        // Like's id (an unguessable ULID) and returns it in the 2xx body, so the caller reads
        // <see cref="DeliveryResult.MintedId"/> to learn the id to pass to <see cref="UnlikeAsync"/>.
        var like = new Like
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = objectId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), like, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default)
    {
        // An unlike is the ActivityStreams inverse of a Like: an Undo whose object references the
        // original Like by id. Per the delivery model, the Undo is published to the liker's OWN outbox
        // (actorId) — the party that made the like undoes it — not the liked object (a content object has
        // no inbox of its own).
        //
        // Decision 055 (learned-id references): <paramref name="originalLikeId"/> is the id the server
        // minted for the original like (learned from <see cref="DeliveryResult.MintedId"/> via
        // <see cref="LikeAsync"/>) — the client never recomputes the server's ids. The client sends only
        // the Undo's shape; the server mints the Undo's own id and returns it in the 2xx body.
        var undo = new Undo
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = originalLikeId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
    {
        // A boost (Announce) is published to the announcer's OWN outbox (the write surface for the
        // activities an actor authors): the instance records the Announce in the announcer's outbox (so
        // the boost surfaces in the announcer's feed) and the activity store, and fans it out to the
        // announcer's remote, non-blocked followers (mirroring the Create fan-out). An Announce carries
        // no embedded object — it is a reference to an existing object IRI — so no object-store write is
        // needed.
        //
        // Decision 055: the client sends only the Announce's shape (actor + object); the server mints the
        // Announce's id (an unguessable ULID, minted once at record-time and reused for every propagated
        // copy) and returns it in the 2xx body, so the caller reads <see cref="DeliveryResult.MintedId"/>
        // to learn the id to pass to <see cref="UnannounceAsync"/>.
        var announce = new Announce
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = objectId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), announce, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default)
    {
        // An unboost is the ActivityStreams inverse of an Announce: an Undo whose object references the
        // original Announce by id. Per the delivery model, the Undo is published to the announcer's OWN
        // outbox (actorId) — the party that made the boost undoes it — not the boosted object (a content
        // object has no inbox of its own).
        //
        // Decision 055 (learned-id references): <paramref name="originalAnnounceId"/> is the id the
        // server minted for the original announce (learned from <see cref="DeliveryResult.MintedId"/> via
        // <see cref="AnnounceAsync"/>) — the client never recomputes the server's ids. The client sends
        // only the Undo's shape; the server mints the Undo's own id and returns it in the 2xx body.
        var undo = new Undo
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = originalAnnounceId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
    {
        // A delete is published to the author's OWN outbox (the write surface for the activities an actor
        // authors — the client never addresses a recipient's inbox) and is signed by the pipeline as
        // actorId. The Delete references the object being deleted by IRI (a bare link — the common case,
        // mirroring the server's DeleteActivityHandler). The server records the Delete in the outbox and
        // activity store, then routes it to the DeleteActivityHandler, which tombstones the object, removes
        // its reply edge, and propagates the tombstone to the author's remote followers (the federated half
        // of F-03).
        //
        // Decision 055: the client sends only the Delete's shape (actor + object, where object is the id
        // the client learned for the object); the server mints the Delete's id (an unguessable ULID) and
        // returns it in the 2xx body.
        var delete = new Delete
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = objectId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), delete, ct);
    }

    /// <summary>
    /// Extracts the deterministic IRI suffix (the final path segment) from an object IRI so a stable,
    /// unique-per-object activity IRI can be minted (a delete at
    /// <c>{actor}/deletes/{suffix}</c> for an object at <c>{actor}/notes/{suffix}</c>).
    /// </summary>
    /// <param name="objectIri">The object's IRI value (e.g. <c>http://host/ap/v1/u/alice/notes/abc123</c>).</param>
    /// <returns>The final path segment (e.g. <c>abc123</c>), or the whole value if there is no segment.</returns>
    private static string ObjectIdSuffix(string objectIri)
    {
        var lastSlash = objectIri.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < objectIri.Length - 1
            ? objectIri[(lastSlash + 1)..]
            : objectIri;
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // A block is published to the blocker's OWN outbox (the write surface for the activities an actor
        // authors — the client never addresses a recipient's inbox) and is signed by the pipeline as
        // actorId. The server records the block edge and delivers it to the target's inbox (the server
        // owns the recipient hop).
        //
        // Decision 055: the client sends only the Block's shape (actor + object); the server mints the
        // Block's id (an unguessable ULID) and returns it in the 2xx body, so the caller reads
        // <see cref="DeliveryResult.MintedId"/> to learn the id to pass to <see cref="UnblockAsync"/>.
        var block = new Block
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), block, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The actors an actor has blocked form a stable, paged collection at {actor}/blocks, so it is
        // enumerated exactly like any other collection (GetCollectionItemsAsync reads through the
        // CollectionPageCache). The items are the blocked actors' IRIs (links).
        return GetCollectionItemsAsync(actorId.BlocksOf(), query, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default)
    {
        // An un-block is the ActivityStreams inverse of a Block: an Undo whose object references the
        // original Block by id. Per the delivery model the Undo is published to the blocker's OWN outbox
        // (an authored activity always flows through the acting actor's own outbox) and is signed by the
        // pipeline as actorId; the server records it and delivers it to the target's inbox (the server
        // owns the recipient hop, so the receiving instance removes the edge).
        //
        // Decision 055 (learned-id references): <paramref name="originalBlockId"/> is the id the server
        // minted for the original block (learned from <see cref="DeliveryResult.MintedId"/> via
        // <see cref="BlockAsync"/>) — the client never recomputes the server's ids. The client sends only
        // the Undo's shape; the server mints the Undo's own id and returns it in the 2xx body.
        var undo = new Undo
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = originalBlockId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // A flag is published to the flagger's OWN outbox (the write surface for the activities an actor
        // authors — the client never addresses a recipient's inbox) and is signed by the pipeline as
        // actorId. The server records the flag edge and delivers it to the target's inbox (the server
        // owns the recipient hop).
        //
        // Decision 055: the client sends only the Flag's shape (actor + object); the server mints the
        // Flag's id (an unguessable ULID) and returns it in the 2xx body, so the caller reads
        // <see cref="DeliveryResult.MintedId"/> to learn the id to pass to <see cref="UnflagAsync"/>.
        var flag = new Flag
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), flag, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default)
    {
        // An un-flag is the inverse of FlagAsync: the Undo references the id the server minted for the
        // original Flag, so the receiving instance resolves the original Flag's parties from the stored
        // Flag and removes the recorded edge. Per the delivery model the Undo is published to the
        // flagger's OWN outbox (an authored activity always flows through the acting actor's own outbox)
        // and is signed by the pipeline as actorId; the server records it and delivers it to the target's
        // inbox (the server owns the recipient hop).
        //
        // Decision 055 (learned-id references): <paramref name="originalFlagId"/> is the id the server
        // minted for the original flag (learned from <see cref="DeliveryResult.MintedId"/> via
        // <see cref="FlagAsync"/>) — the client never recomputes the server's ids. The client sends only
        // the Undo's shape; the server mints the Undo's own id and returns it in the 2xx body.
        var undo = new Undo
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = originalFlagId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
    {
        // A community manages its own membership (19.5.2 self-management): the Add's actor is the
        // community, so the server's community-outbox gate (actor == community) passes and the member is
        // added. The request is signed as the community (the client's signing identity), so the actor and
        // the signature agree.
        //
        // Decision 055: posted to the community's OWN outbox (the authoring surface) — the server mints
        // the Add's id (an unguessable ULID) and returns it in the 2xx body, so the client never chooses
        // (or recomputes) the id. The client sends only the Add's shape (actor + object).
        var add = new Add
        {
            Actor = [new Link { Href = communityId.Uri }],
            Object = [new Link { Href = memberId.Uri }],
        };

        return DeliverAsync(communityId.OutboxOf(), add, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default)
    {
        // The inverse of AddMemberAsync: the community removes a member from its own member set. The
        // Remove's actor is the community (the 19.5.2 gate), the request is signed as the community, and
        // it is published to the community's OWN outbox (the authoring surface, decision 055).
        //
        // Decision 055: the server mints the Remove's id (an unguessable ULID) and returns it in the 2xx
        // body; the client sends only the Remove's shape (actor + object).
        var remove = new Remove
        {
            Actor = [new Link { Href = communityId.Uri }],
            Object = [new Link { Href = memberId.Uri }],
        };

        return DeliverAsync(communityId.OutboxOf(), remove, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> CreateCommunityAsync(
        Iri actorId,
        string name,
        string displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(displayName);

        // The new community lives on the creator's instance: its IRI is {instanceBase}/ap/v1/c/{name},
        // where the instance base is the origin of the creator's actor IRI (scheme + authority). The
        // server's outbox-publish handler, on seeing this Create's embedded Group, materializes the
        // community in its community store (19.5.1 community-creation write path).
        var origin = $"{actorId.Uri.Scheme}://{actorId.Uri.Authority}";
        var communityIri = new Iri($"{origin}/ap/v1/c/{name}");

        // The community's IRI is its public handle ({instanceBase}/ap/v1/c/{name}) — a stable,
        // meaningful identifier chosen by the creator (the community is addressed by name), not a
        // server-minted object id. Decision 055 leaves it client-chosen because the community's
        // identity is its name; the server mints only the wrapping Create's id (and returns it in the
        // 2xx body).
        var group = new Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [displayName],
        };

        // Decision 055: the client sends only the Create's shape (actor + the embedded Group); the server
        // mints the Create's id (an unguessable ULID) and returns it in the 2xx body.
        var create = new Create
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [group],
        };

        // Publish to the creator's OWN outbox (the AP-native outbox-publish pattern): the server records
        // the Create in the creator's outbox and, because the embedded object is a local Group, stores
        // the community (document endpoint, members, feed, collections now resolve).
        return DeliverAsync(actorId.OutboxOf(), create, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The actors an actor has flagged form a stable, paged collection at {actor}/flags, so it is
        // enumerated exactly like any other collection (GetCollectionItemsAsync reads through the
        // CollectionPageCache). The items are the flagged actors' IRIs (links).
        return GetCollectionItemsAsync(actorId.FlagsOf(), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The actors an actor has muted form a stable, paged collection at {actor}/mutes, so it is
        // enumerated exactly like any other collection (GetCollectionItemsAsync reads through the
        // CollectionPageCache). The items are the muted actors' IRIs (links).
        return GetCollectionItemsAsync(actorId.MutesOf(), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The relays an actor subscribes to form a stable, paged collection at {actor}/relays (the
        // ActivityPub `star` set), so it is enumerated exactly like any other collection
        // (GetCollectionItemsAsync reads through the CollectionPageCache). The items are the relays'
        // IRIs (links).
        return GetCollectionItemsAsync(actorId.RelaysOf(), query, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
    {
        // Decision 055 (server is the object-id authority): the client sends only the post's *shape*
        // (content, attributedTo, audience) — no note id, no Create id. The server mints both the
        // Create's id and the embedded note's id (unguessable ULIDs) and returns the created Create in
        // the 2xx body, so the caller reads <see cref="DeliveryResult.MintedId"/> to learn the Create's
        // id. (The embedded note's own id is carried in the returned body's `object`; a caller that needs
        // to reference the note — e.g. to reply to or delete it — parses the returned object's id.)
        var note = new Note
        {
            Content = [content],
            AttributedTo = [new Link { Href = actorId.Uri }],
        };

        if (to is not null)
        {
            var audience = to.Select(i => new Link { Href = i.Uri }).ToList();
            if (audience.Count > 0)
            {
                note.To = audience;
            }
        }

        return PostNoteAsync(actorId, note, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        // The constructor sets Type = "Create"; the embedded Note sets Type = "Note". The Create's
        // object is the embedded note (a full object, not a link) so the receiver stores the content
        // (and its attachments) without a second fetch.
        var create = new Create
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [note],
        };

        // Published to the author's OWN outbox (the "local post" path — the outbox is the write surface
        // for the activities an actor authors): the author's instance records the note in the outbox and
        // federates it to followers.
        return DeliverAsync(actorId.OutboxOf(), create, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> PostReplyAsync(
        Iri actorId,
        Iri parentIri,
        string content,
        IEnumerable<Iri>? mentions = null,
        IEnumerable<Iri>? to = null,
        CancellationToken ct = default)
    {
        // Decision 055 (server is the object-id authority): the client sends only the reply's *shape*
        // (content, attributedTo, the parent's learned id as inReplyTo, mentions, audience) — no note id,
        // no Create id. <paramref name="parentIri"/> is the id the server minted for the parent note
        // (learned when the parent was posted). The server mints the Create's id and the embedded note's
        // id (unguessable ULIDs) and returns the created Create in the 2xx body.
        var note = new Note
        {
            Content = [content],
            AttributedTo = [new Link { Href = actorId.Uri }],
            // F-12 threading: the parent note is the reply's inReplyTo (a link to the parent).
            InReplyTo = [new Link { Href = parentIri.Uri }],
        };

        // F-12 mentions: each mentioned actor becomes a Mention tag whose href is the actor IRI (the
        // ActivityPub @mention convention). Non-mention tags (e.g. hashtags) are not part of this API.
        if (mentions is not null)
        {
            var mentionTags = mentions
                .Select(mentionIri => new Mention { Href = mentionIri.Uri })
                .ToList();
            if (mentionTags.Count > 0)
            {
                note.Tag = mentionTags;
            }
        }

        if (to is not null)
        {
            var audience = to.Select(i => new Link { Href = i.Uri }).ToList();
            if (audience.Count > 0)
            {
                note.To = audience;
            }
        }

        // The embedded Note (a full object, not a link) carries inReplyTo + the mention tags, so the
        // receiver's Create handler records the parent → child reply edge from the note's inReplyTo.
        // Decision 055: the client sends only the Create's shape (actor + the embedded note); the server
        // mints the Create's id and the note's id (unguessable ULIDs) and returns the created Create in
        // the 2xx body.
        var create = new Create
        {
            Actor = [new Link { Href = actorId.Uri }],
            Object = [note],
        };

        // Published to the author's OWN outbox (the outbox is the write surface for the activities an
        // actor authors): the author's instance records the reply in the outbox and federates it.
        return DeliverAsync(actorId.OutboxOf(), create, ct);
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync(request, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<CollectionPage> GetCollectionAsync(
        Iri collectionId,
        CollectionQuery? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int? limit = query?.Limit;
        bool bypassCache = query?.BypassCache ?? false;
        int yielded = 0;

        // Resolve the first page: fetch the collection and follow its `first` link if needed.
        IObject? first = await GetObjectAsync(collectionId, ct).ConfigureAwait(false);
        Iri? pageIri = ResolveFirstPageIri(first, collectionId);
        if (pageIri is null)
        {
            yield break;
        }

        while (pageIri is { } current)
        {
            var page = await FetchCollectionPageAsync(current, bypassCache, ct).ConfigureAwait(false);
            if (page is null)
            {
                yield break;
            }

            yield return page;

            foreach (var _ in page.Items)
            {
                yielded++;
                if (limit is not null && yielded >= limit)
                {
                    yield break;
                }
            }

            pageIri = page.NextPage;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
        Iri collectionId,
        CollectionQuery? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int? limit = query?.Limit;
        int remaining = limit ?? int.MaxValue;

        await foreach (var page in GetCollectionAsync(collectionId, query, ct).ConfigureAwait(false))
        {
            foreach (var item in page.Items)
            {
                if (remaining <= 0)
                {
                    yield break;
                }

                yield return item;
                remaining--;
            }
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
        Iri communityId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The community feed is a paged collection at {community}/feed, so it is enumerated exactly like
        // any other collection (GetCollectionItemsAsync reads through the CollectionPageCache).
        return GetCollectionItemsAsync(communityId.FeedOf(), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The followed feed is a paged collection at {actor}/feed, so it is enumerated exactly like
        // any other collection (GetCollectionItemsAsync reads through the CollectionPageCache).
        return GetCollectionItemsAsync(new Iri($"{actorId.Value}/feed"), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The replies to an object are a paged collection at {object}/replies, so they are enumerated
        // exactly like any other collection (GetCollectionItemsAsync reads through the
        // CollectionPageCache). The items are the reply objects' IRIs (links).
        return GetCollectionItemsAsync(objectIri.RepliesOf(), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The object's likers are a full, non-paged extension collection at {object}/likes (the bare,
        // non-namespaced ecosystem term), so they are enumerated exactly like any other collection
        // (GetCollectionItemsAsync reads through the CollectionPageCache). The items are the likers'
        // IRIs (links); the count of yielded items is the object's like count.
        return GetCollectionItemsAsync(objectIri.LikesOf(), query, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default)
    {
        // The object's announcers (boosters) are a full, non-paged extension collection at
        // {object}/shares (the bare, non-namespaced ecosystem term), so they are enumerated exactly
        // like any other collection. The items are the announcers' IRIs (links); the count of yielded
        // items is the object's boost count.
        return GetCollectionItemsAsync(objectIri.SharesOf(), query, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
        Iri actorId,
        ProxyCredentials credentials,
        CollectionQuery? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // Decision 056: the inbox is the activities DELIVERED TO the actor (what they received), as
        // opposed to the outbox (what they authored). Unlike the public collections, it is PRIVATE —
        // the server serves it only to the owner via Basic auth (the same seam that gates the
        // owner-only privateKey extension) and with no-store caching. So this read carries the owner's
        // Basic credentials on every page fetch and does NOT route through the CollectionPageCache
        // (private, owner-scoped data must not be cached like a public collection).
        var inboxIri = new Iri($"{actorId.Value}/inbox");
        Iri? pageIri = inboxIri;
        int? limit = query?.Limit;
        int yielded = 0;

        while (pageIri is { } current)
        {
            var page = await FetchAuthenticatedCollectionPageAsync(current, credentials, ct).ConfigureAwait(false);
            if (page is null)
            {
                yield break;
            }

            foreach (var item in page.Items)
            {
                yield return item;
                yielded++;
                if (limit is not null && yielded >= limit)
                {
                    yield break;
                }
            }

            pageIri = page.NextPage;
        }
    }

    /// <summary>
    /// Fetches a single page of the owner-only inbox (a Basic-authenticated GET), returning a
    /// <see cref="CollectionPage"/> (its flattened <see cref="CollectionPage.Items"/> are the inbox
    /// entries; <see cref="CollectionPage.NextPage"/> is the <c>next</c> pointer, or null when there is
    /// no further page). The inbox is private + no-store, so the page is always fetched from the network
    /// (never from the collection page cache) and every request carries the owner's Basic credentials.
    /// </summary>
    private async Task<CollectionPage?> FetchAuthenticatedCollectionPageAsync(
        Iri pageIri,
        ProxyCredentials credentials,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageIri.Value);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A 403 (not the owner) or 404 (unknown actor) means there is no readable inbox — stop.
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // The inbox page is an OrderedCollectionPage (or, on a single page, an OrderedCollection). Both
        // carry `items`; read them via the shared IObject/Collection pattern.
        var obj = ActivityJson.Deserialize<IObjectOrLink>(json);
        if (obj is not IObject pageObj || pageObj is not Collection)
        {
            return null;
        }

        var items = (pageObj as OrderedCollectionPage)?.Items ?? (pageObj as Collection)?.Items;
        var itemList = items is { } i ? i.ToList() : [];

        // The `next` pointer lives on the page (ExtensionData for an OrderedCollection, typed on an
        // OrderedCollectionPage).
        Iri? next = pageObj switch
        {
            OrderedCollectionPage { Next: { } n } => n.ResolveCollectionIri(),
            OrderedCollection collection => ResolveCollectionNextLink(collection),
            _ => null,
        };

        return new CollectionPage
        {
            Page = new OrderedCollectionPage
            {
                Id = pageObj.Id,
                Items = itemList,
                TotalItems = (pageObj as Collection)?.TotalItems,
            },
            Items = itemList,
            NextPage = next,
            PrevPage = null,
            TotalItems = (pageObj as Collection)?.TotalItems is { } total ? (int)total : null,
            PageId = pageIri,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IObjectOrLink> SearchAsync(
        Iri instanceBase,
        string? query = null,
        SearchOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Global search (F-13) uses the limit/offset pagination shape, not the first/next shape of a
        // stable collection, so the client requests a single page of up to `limit` items at `offset` and
        // returns its items. The response is a fresh query (not cached) and the request is signed with
        // the ClientToServer profile like any other GET.
        var limit = options?.Limit ?? 100;
        var offset = options?.Offset ?? 0;

        // The search endpoint is the instance base's `SearchOf` derivation (`/ap/v1/search`) with the
        // query appended — the single source of truth for where global search lives. The q value is
        // URL-encoded (it may contain spaces / non-ASCII); limit/offset are numeric.
        var encodedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var searchIri = new Iri($"{instanceBase.SearchOf()}?q={encodedQuery}&limit={limit}&offset={offset}");

        using var request = new HttpRequestMessage(HttpMethod.Get, searchIri.Value);
        var page = await GetObjectAsync(request, ct).ConfigureAwait(false);
        // The first page (offset=0) is an OrderedCollection; subsequent pages are OrderedCollectionPage.
        // Both carry `items`, so accept either and read the items from the shared property.
        if (page is not (OrderedCollection or OrderedCollectionPage))
        {
            yield break;
        }

        var items = page switch
        {
            OrderedCollection { Items: { } c } => c,
            OrderedCollectionPage { Items: { } p } => p,
            _ => null,
        };
        if (items is null)
        {
            yield break;
        }

        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static Iri? ResolveFirstPageIri(IObject? collection, Iri collectionId)
    {
        // If the fetched object is itself a page, use it directly.
        if (collection is OrderedCollectionPage)
        {
            return collectionId;
        }

        // Otherwise follow the collection's `first` link to reach the first page.
        if (collection is Collection { First: { } first })
        {
            return first.ResolveCollectionIri();
        }

        return null;
    }

    private async Task<CollectionPage?> FetchCollectionPageAsync(Iri pageIri, bool bypassCache, CancellationToken ct)
    {
        IObject? obj;
        if (_collectionPageCache is null)
        {
            obj = await GetCollectionPageFromNetworkAsync(pageIri, bypassCache, ct).ConfigureAwait(false);
        }
        else
        {
            var (value, _) = await _collectionPageCache.GetAsync(
                pageIri,
                bypassCache,
                async iri => await GetCollectionPageFromNetworkAsync(iri, bypassCache, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            obj = value;
        }

        // A collection page is either an OrderedCollectionPage (page N>1) or the collection's first
        // page served as an OrderedCollection (page 1 — the server serves the collection document
        // itself, carrying its first page of items + a self `first`, with the `next` pointer living
        // on the page). Both are valid first/current pages, so both are accepted.
        if (obj is OrderedCollectionPage)
        {
            return CollectionPageFactory.FromOrderedCollectionPage(obj);
        }

        if (obj is OrderedCollection collection)
        {
            // Page 1 served as the collection document itself. CollectionPage.Page is typed
            // OrderedCollectionPage, so a minimal page carrying the collection's own id/items/total
            // is synthesized (the flattened Items below is the source of truth for callers).
            //
            // The ActivityStreams OrderedCollection type has no typed `next` property (only
            // OrderedCollectionPage does), so a well-formed server carries the pointer in
            // ExtensionData. It is what lets enumeration walk past page 1 — without it the client
            // stops after the first page for any multi-page collection served this way.
            //
            // The page-1 IRI is the `next` pointer, NOT the collection's own IRI: the server serves
            // page N>1 at {collection}?page=N, so fetching the bare collection IRI again would
            // re-serve page 1 and loop forever. When there is no `next` (single-page collection) the
            // collection's own IRI is the first page and the walk terminates.
            var items = collection.Items is { } itemsEnumerable ? itemsEnumerable.ToList() : [];
            var nextLink = ResolveCollectionNextLink(collection);
            var firstPageIri = nextLink
                ?? (collection.Id is { Length: > 0 } collectionId ? new Iri(collectionId) : null);
            return new CollectionPage
            {
                Page = new OrderedCollectionPage
                {
                    Id = collection.Id,
                    Items = items,
                    TotalItems = collection.TotalItems,
                },
                Items = items,
                NextPage = nextLink,
                PrevPage = null,
                TotalItems = collection.TotalItems is { } total ? (int)total : null,
                PageId = firstPageIri,
            };
        }

        // An unordered Collection (F-18) served as its first page (the collection document carrying its
        // first page of items + a self `first`). OrderedCollection derives from Collection, so this
        // branch is guarded by `is not OrderedCollection` to preserve the extension-data `next`
        // resolution above. An unordered Collection has no typed `next` (only CollectionPage does), so
        // the walk terminates after page 1 — acceptable for a rarely-used, low-priority shape.
        if (obj is Collection { Id: not null } unordered)
        {
            var items = unordered.Items is { } itemsEnumerable ? itemsEnumerable.ToList() : [];
            return new CollectionPage
            {
                Page = new OrderedCollectionPage
                {
                    Id = unordered.Id,
                    Items = items,
                    TotalItems = unordered.TotalItems,
                },
                Items = items,
                NextPage = null,
                PrevPage = null,
                TotalItems = unordered.TotalItems is { } total ? (int)total : null,
                PageId = new Iri(unordered.Id),
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves the <c>next</c> pointer of an <see cref="OrderedCollection"/> first page (the
    /// collection document served as page 1) into a page IRI, or <see langword="null"/> when the
    /// collection has no further page. The ActivityStreams <c>OrderedCollection</c> type exposes no
    /// typed <c>next</c> property (only <c>OrderedCollectionPage</c> does), so a well-formed server
    /// carries the pointer in <see cref="IObject.ExtensionData"/>; both a JSON-object link
    /// (<c>{"href": "..."}</c>) and a bare IRI string are accepted for leniency.
    /// </summary>
    private static Iri? ResolveCollectionNextLink(OrderedCollection collection)
    {
        if (collection.ExtensionData is not { } ext ||
            !ext.TryGetValue("next", out var nextElement))
        {
            return null;
        }

        // A bare IRI string (the wire shape Iris's server emits).
        if (nextElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var value = nextElement.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new Iri(value);
        }

        // A JSON-LD link object with an `href`.
        if (nextElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            nextElement.TryGetProperty("href", out var href) &&
            href.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var value = href.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new Iri(value);
        }

        return null;
    }

    private async Task<IObject?> GetObjectAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var objectOrLink = ActivityJson.Deserialize<IObjectOrLink>(json);
        return objectOrLink is IObject obj ? obj : null;
    }
}
