using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Iris.Core;
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
    public async Task<DeliveryResult> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default)
    {
        if (activity is not Activity)
        {
            throw new ArgumentException("The object must be an Activity to deliver.", nameof(activity));
        }

        var json = ActivityJson.Serialize(activity);
        var body = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, inboxId.Value)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new DeliveryResult((int)response.StatusCode, response.IsSuccessStatusCode, bodyText);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // The follow is published to the follower's OWN outbox (the write surface for the activities an
        // actor authors — the client never addresses a recipient's inbox for an activity it authors) and
        // is signed by the pipeline as actorId. The server records it in the follower's outbox and then
        // delivers it to the target's inbox (the server owns the recipient hop). The `Id` is a
        // deterministic, unique-per-(actor,target) IRI so a retried follow dedupes. The ActivityStreams
        // Follow type has no typed `id`/`actor`/`object` scalar beyond the library's, so the
        // object-initializer form is used and the constructor sets `Type = "Follow"`.
        var follow = new Follow
        {
            Id = $"{actorId.Value}/follows/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), follow, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // An un-follow is the ActivityStreams inverse of a Follow: an Undo whose object references the
        // original Follow by IRI. Per the delivery model, the Undo is published to the follower's OWN
        // outbox (actorId) — the party that made the follow undoes it, and an authored activity always
        // flows through the acting actor's own outbox — not the un-followed actor's inbox. The server
        // records it and delivers it to the target's inbox (the server owns the recipient hop). The
        // object IRI reuses FollowAsync's deterministic {actorId}/follows/{targetId} IRI so the receiver
        // resolves exactly the follow that was recorded; the Undo gets its own deterministic
        // unique-per-(actor,target) IRI so a retried un-follow dedupes.
        var followIri = new Iri($"{actorId.Value}/follows/{targetId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unfollows/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = followIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default)
    {
        // An Accept is the followed actor's response to an inbound Follow: it is published to the
        // FOLLOWED actor's OWN outbox (actorId) — the party that decides the follow authors the Accept,
        // and an authored activity always flows through the acting actor's own outbox — and signed by the
        // pipeline as actorId. The object references the original Follow by IRI (the deterministic
        // {follower}/follows/{target} IRI the follower recorded). The server records the Accept in the
        // actor's outbox, ensures the follower→actor edge, and delivers the Accept to the follower's
        // inbox (the server owns the recipient hop). The `Id` is deterministic per (actor,follow) so a
        // retried accept dedupes; the constructor sets `Type = "Accept"`.
        var accept = new Accept
        {
            Id = $"{actorId.Value}/accepts/{followIri.Value}",
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
        // references the original Follow by IRI. The server records the Reject in the actor's outbox,
        // removes the provisional follower→actor edge, and delivers the Reject to the follower's inbox
        // (the server owns the recipient hop). The `Id` is deterministic per (actor,follow) so a retried
        // reject dedupes; the constructor sets `Type = "Reject"`.
        var reject = new Reject
        {
            Id = $"{actorId.Value}/rejects/{followIri.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = followIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), reject, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
    {
        // A like is published to the liker's OWN outbox (the write surface for the activities an actor
        // authors): the instance records the like edge (liker → object) in the liker's `liked` collection
        // and the outbox, and the server federates it to the object's owner. A content object (the liked
        // note) has no inbox of its own — only actors do — so the recipient hop belongs to the server. The
        // `Id` is a deterministic, unique-per-(actor,object) IRI so a retried like dedupes on the
        // receiver. The ActivityStreams Like type has no typed scalar beyond the library's, so the
        // object-initializer form is used and the constructor sets `Type = "Like"`.
        var like = new Like
        {
            Id = $"{actorId.Value}/likes/{objectId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = objectId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), like, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default)
    {
        // An unlike is the ActivityStreams inverse of a Like: an Undo whose object references the
        // original Like by IRI. Per the delivery model, the Undo is published to the liker's OWN outbox
        // (actorId) — the party that made the like undoes it — not the liked object (a content object has
        // no inbox of its own). The object IRI reuses LikeAsync's deterministic {actorId}/likes/{objectId}
        // IRI so the receiver resolves exactly the like that was recorded; the Undo gets its own
        // deterministic unique-per-(actor,object) IRI so a retried unlike dedupes.
        var likeIri = new Iri($"{actorId.Value}/likes/{objectId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unlikes/{objectId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = likeIri.Uri }],
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
        // of F-03). The `Id` is a deterministic, unique-per-(actor,object) IRI so a retried delete dedupes.
        var delete = new Delete
        {
            Id = $"{actorId.Value}/deletes/{ObjectIdSuffix(objectId.Value)}",
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
        // owns the recipient hop). The `Id` is a deterministic, unique-per-(actor,target) IRI so a
        // retried block dedupes. The ActivityStreams Block type (a subclass of Ignore) has no typed
        // `id`/`actor`/`object` scalar beyond the library's, so the object-initializer form is used and
        // the constructor sets `Type = "Block"`.
        var block = new Block
        {
            Id = $"{actorId.Value}/blocks/{targetId.Value}",
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
    public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // An un-block is the ActivityStreams inverse of a Block: an Undo whose object references the
        // original Block by IRI. Per the delivery model the Undo is published to the blocker's OWN outbox
        // (an authored activity always flows through the acting actor's own outbox) and is signed by the
        // pipeline as actorId; the server records it and delivers it to the target's inbox (the server
        // owns the recipient hop, so the receiving instance removes the edge). The object IRI reuses
        // BlockAsync's deterministic {actor}/blocks/{target} IRI so it references exactly the block that
        // was recorded; the Undo gets its own deterministic unique-per-(actor,target) IRI so a retried
        // un-block dedupes on the receiver.
        var blockIri = new Iri($"{actorId.Value}/blocks/{targetId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unblocks/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = blockIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // A flag is published to the flagger's OWN outbox (the write surface for the activities an actor
        // authors — the client never addresses a recipient's inbox) and is signed by the pipeline as
        // actorId. The server records the flag edge and delivers it to the target's inbox (the server
        // owns the recipient hop). The `Id` is a deterministic, unique-per-(actor,target) IRI so a
        // retried flag dedupes. The ActivityStreams Flag type (a subclass of Activity) has no typed
        // `id`/`actor`/`object` scalar beyond the library's, so the object-initializer form is used and
        // the constructor sets `Type = "Flag"`.
        var flag = new Flag
        {
            Id = $"{actorId.Value}/flags/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), flag, ct);
    }

    /// <inheritdoc/>
    public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // An un-flag is the inverse of FlagAsync: the Undo references the deterministic Flag IRI
        // {actorId}/flags/{targetId} (the same IRI FlagAsync used), so the receiving instance resolves
        // the original Flag's parties from the stored Flag and removes the recorded edge. Per the
        // delivery model the Undo is published to the flagger's OWN outbox (an authored activity always
        // flows through the acting actor's own outbox) and is signed by the pipeline as actorId; the
        // server records it and delivers it to the target's inbox (the server owns the recipient hop).
        var flagIri = new Iri($"{actorId.Value}/flags/{targetId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unflags/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = flagIri.Uri }],
        };

        return DeliverAsync(actorId.OutboxOf(), undo, ct);
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
        // A deterministic, unique IRI per (actor, content) so a retried post dedupes on the receiver:
        // the note id derives from the actor + a content hash, and the Create id from the note id.
        var noteIri = $"{actorId.Value}/notes/{CreateNoteIdSuffix(content)}";
        var createIri = $"{actorId.Value}/creates/{CreateNoteIdSuffix(content)}";

        var note = new Note
        {
            Id = noteIri,
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

        // The constructor sets Type = "Create"; the embedded Note sets Type = "Note". The Create's
        // object is the embedded note (a full object, not a link) so the receiver stores the content
        // without a second fetch.
        var create = new Create
        {
            Id = createIri,
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
        // A deterministic, unique IRI per (actor, parent, content) so a retried reply dedupes on the
        // receiver: the note id derives from the actor + parent + a content hash, and the Create id
        // from the note id. Including the parent in the hash keeps replies to different parents distinct
        // even for identical content.
        var noteIri = $"{actorId.Value}/notes/{CreateReplyIdSuffix(parentIri.Value, content)}";
        var createIri = $"{actorId.Value}/creates/{CreateReplyIdSuffix(parentIri.Value, content)}";

        var note = new Note
        {
            Id = noteIri,
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
        var create = new Create
        {
            Id = createIri,
            Actor = [new Link { Href = actorId.Uri }],
            Object = [note],
        };

        // Published to the author's OWN outbox (the outbox is the write surface for the activities an
        // actor authors): the author's instance records the reply in the outbox and federates it.
        return DeliverAsync(actorId.OutboxOf(), create, ct);
    }

    /// <summary>
    /// Derives a short, deterministic suffix for a reply note/Create IRI from its parent IRI + content
    /// (a stable content hash), so identical replies to the same parent from the same actor map to the
    /// same IRI (dedupe) and replies to different parents (or with different content) map to distinct
    /// IRIs.
    /// </summary>
    private static string CreateReplyIdSuffix(string parentIri, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(parentIri + "\u0000" + content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Derives a short, deterministic suffix for a note/Create IRI from its content (a stable
    /// content hash), so identical posts from the same actor map to the same IRI (dedupe) and
    /// distinct posts map to distinct IRIs.
    /// </summary>
    private static string CreateNoteIdSuffix(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
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
