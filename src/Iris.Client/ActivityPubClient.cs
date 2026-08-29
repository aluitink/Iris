using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.CollectionPage;

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
    private readonly LocalAuthHandler? _localAuth;

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/>.
    /// </summary>
    /// <param name="http">The HTTP client (its handler pipeline should include a
    /// <see cref="SigningHandler"/> for signed requests). The client does not dispose it.</param>
    public ActivityPubClient(HttpClient http)
        : this(http, null, false, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> that owns its <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="handler">The handler pipeline (typically a <see cref="SigningHandler"/> over a
    /// <see cref="HttpClientHandler"/>). The signing handler's <see cref="SigningHandler.ActorId"/>
    /// must be set before sending signed requests.</param>
    public ActivityPubClient(HttpMessageHandler handler)
        : this(null, handler, true, null, null, null)
    {
    }

    private ActivityPubClient(
        HttpClient? http,
        HttpMessageHandler? handler,
        bool disposeHandler,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache,
        LocalAuthHandler? localAuth)
    {
        if (http is null && handler is null)
        {
            throw new ArgumentException("Either http or handler must be provided.", nameof(http));
        }

        _http = http ?? new HttpClient(handler!, disposeHandler);
        _ownsHandler = disposeHandler;
        _actorCache = actorCache;
        _collectionPageCache = collectionPageCache;
        _localAuth = localAuth;
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
        : this(http, null, false, actorCache, collectionPageCache, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> with optional read-through caches and an
    /// optional local-auth handler (for F-07 local moderation).
    /// </summary>
    /// <param name="http">The HTTP client (its handler pipeline should include a
    /// <see cref="SigningHandler"/> for signed requests). The client does not dispose it.</param>
    /// <param name="actorCache">Optional cache for <see cref="GetObjectAsync(Iri, CancellationToken)"/> reads. Null disables actor caching.</param>
    /// <param name="collectionPageCache">Optional cache for <see cref="GetCollectionAsync"/> page reads. Null disables page caching.</param>
    /// <param name="localAuth">Optional <see cref="LocalAuthHandler"/> for local, Basic-authenticated
    /// moderation requests (F-07 mute). Null disables local moderation (the no-credential
    /// <c>MuteAsync</c>/<c>UnmuteAsync</c> overloads throw).</param>
    public ActivityPubClient(
        HttpClient http,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache,
        LocalAuthHandler? localAuth)
        : this(http, null, false, actorCache, collectionPageCache, localAuth)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ActivityPubClient"/> that owns its <see cref="HttpClient"/>, with
    /// optional read-through caches and an optional local-auth handler (for F-07 local moderation).
    /// </summary>
    /// <param name="handler">The handler pipeline (typically a <see cref="SigningHandler"/> over a
    /// <see cref="HttpClientHandler"/>).</param>
    /// <param name="actorCache">Optional cache for <see cref="GetObjectAsync(Iri, CancellationToken)"/> reads. Null disables actor caching.</param>
    /// <param name="collectionPageCache">Optional cache for <see cref="GetCollectionAsync"/> page reads. Null disables page caching.</param>
    /// <param name="localAuth">Optional <see cref="LocalAuthHandler"/> for local, Basic-authenticated
    /// moderation requests (F-07 mute). Null disables local moderation (the no-credential
    /// <c>MuteAsync</c>/<c>UnmuteAsync</c> overloads throw).</param>
    public ActivityPubClient(
        HttpMessageHandler handler,
        ActorCache? actorCache,
        CollectionPageCache? collectionPageCache,
        LocalAuthHandler? localAuth = null)
        : this(null, handler, true, actorCache, collectionPageCache, localAuth)
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

    /// <inheritdoc/>
    public async Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
        => (await GetObjectAsync(actorId, ct).ConfigureAwait(false)) as Actor;

    /// <inheritdoc/>
    public async Task<int> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default)
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
        return (int)response.StatusCode;
    }

    /// <inheritdoc/>
    public Task<int> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // The follow is delivered to the target's inbox (derived from the actor IRI) and is signed by
        // the pipeline as actorId. The `Id` is a deterministic, unique-per-(actor,target) IRI so the
        // receiving store can dedupe a retried follow. The ActivityStreams Follow type has no typed
        // `id`/`actor`/`object` scalar beyond the library's, so the object-initializer form is used and
        // the constructor sets `Type = "Follow"`.
        var follow = new Follow
        {
            Id = $"{actorId.Value}/follows/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(targetId.InboxOf(), follow, ct);
    }

    /// <inheritdoc/>
    public Task<int> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // A block is delivered to the target's inbox (per ActivityPub §5.2.1.3) and is signed by the
        // pipeline as actorId. The `Id` is a deterministic, unique-per-(actor,target) IRI so the
        // receiving moderation store can dedupe a retried block. The ActivityStreams Block type
        // (a subclass of Ignore) has no typed `id`/`actor`/`object` scalar beyond the library's, so the
        // object-initializer form is used and the constructor sets `Type = "Block"`.
        var block = new Block
        {
            Id = $"{actorId.Value}/blocks/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(targetId.InboxOf(), block, ct);
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
    public Task<int> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // An un-block is the ActivityStreams inverse of a Block: an Undo whose object references the
        // original Block by IRI. The Undo is delivered to targetId.InboxOf() (the previously-blocked
        // actor's inbox, so the receiving instance removes the edge) and is signed by the pipeline as
        // actorId. The object IRI reuses BlockAsync's deterministic {actor}/blocks/{target} IRI so it
        // references exactly the block that was recorded; the Undo gets its own deterministic
        // unique-per-(actor,target) IRI so a retried un-block dedupes on the receiver.
        var blockIri = new Iri($"{actorId.Value}/blocks/{targetId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unblocks/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = blockIri.Uri }],
        };

        return DeliverAsync(targetId.InboxOf(), undo, ct);
    }

    /// <inheritdoc/>
    public Task<int> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // A flag is delivered to the target's inbox (the flagged actor's inbox) and is signed by the
        // pipeline as actorId. The `Id` is a deterministic, unique-per-(actor,target) IRI so the
        // receiving moderation store can dedupe a retried flag. The ActivityStreams Flag type
        // (a subclass of Activity) has no typed `id`/`actor`/`object` scalar beyond the library's, so
        // the object-initializer form is used and the constructor sets `Type = "Flag"`.
        var flag = new Flag
        {
            Id = $"{actorId.Value}/flags/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = targetId.Uri }],
        };

        return DeliverAsync(targetId.InboxOf(), flag, ct);
    }

    /// <inheritdoc/>
    public Task<int> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
    {
        // An un-flag is the inverse of FlagAsync: the Undo references the deterministic Flag IRI
        // {actorId}/flags/{targetId} (the same IRI FlagAsync used), so the receiving instance resolves
        // the original Flag's parties from the stored Flag and removes the recorded edge. The Undo is
        // delivered to the flagged actor's inbox (the same inbox the Flag went to) and is signed by the
        // pipeline as actorId.
        var flagIri = new Iri($"{actorId.Value}/flags/{targetId.Value}");
        var undo = new Undo
        {
            Id = $"{actorId.Value}/unflags/{targetId.Value}",
            Actor = [new Link { Href = actorId.Uri }],
            Object = [new Link { Href = flagIri.Uri }],
        };

        return DeliverAsync(targetId.InboxOf(), undo, ct);
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
    public Task<int> MuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => LocalModerateAsync(actorId, targetId, remove: false, credentials: null, ct);

    /// <inheritdoc/>
    public Task<int> MuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalModerateAsync(actorId, targetId, remove: false, credentials, ct);

    /// <inheritdoc/>
    public Task<int> UnmuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
        => LocalModerateAsync(actorId, targetId, remove: true, credentials: null, ct);

    /// <inheritdoc/>
    public Task<int> UnmuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalModerateAsync(actorId, targetId, remove: true, credentials, ct);

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
    public Task<int> SubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default)
        => LocalLocalDecisionAsync(actorId, relayId, path: "relays", remove: false, removeQuery: "unsubscribe", credentials: null, ct);

    /// <inheritdoc/>
    public Task<int> SubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalLocalDecisionAsync(actorId, relayId, path: "relays", remove: false, removeQuery: "unsubscribe", credentials, ct);

    /// <inheritdoc/>
    public Task<int> UnsubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default)
        => LocalLocalDecisionAsync(actorId, relayId, path: "relays", remove: true, removeQuery: "unsubscribe", credentials: null, ct);

    /// <inheritdoc/>
    public Task<int> UnsubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default)
        => LocalLocalDecisionAsync(actorId, relayId, path: "relays", remove: true, removeQuery: "unsubscribe", credentials, ct);

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

    private async Task<int> LocalModerateAsync(
        Iri actorId,
        Iri targetId,
        bool remove,
        ProxyCredentials? credentials,
        CancellationToken ct)
        => await LocalLocalDecisionAsync(
            actorId, targetId, path: "mutes", remove, removeQuery: "unmute", credentials, ct)
            .ConfigureAwait(false);

    private async Task<int> LocalLocalDecisionAsync(
        Iri actorId,
        Iri targetId,
        string path,
        bool remove,
        string removeQuery,
        ProxyCredentials? credentials,
        CancellationToken ct)
    {
        // A local decision (a mute, F-07, or a relay subscription, F-06) is Iris-specific (no
        // ActivityStreams type for either) and is a local decision, so it is not a signed inbox
        // delivery: it is a Basic-authenticated POST to the acting actor's own instance. The local-auth
        // handler is either the client's default (the configured LocalCredentials) or a one built for the
        // request (explicit credentials). A missing handler/credentials is a programming error (the
        // caller must configure LocalCredentials or pass credentials explicitly).
        //
        // When the client's default local-auth handler is used it is SHARED across calls (the transport
        // is the factory's, and a test may route it through a deferred handler that is created once), so
        // the HttpClient must NOT dispose it. When a handler is built for the request (explicit
        // credentials over a fresh transport) it is request-scoped and IS disposed.
        var configured = _localAuth;
        LocalAuthHandler handler;
        bool ownsHandler;
        if (credentials is not null && configured is null)
        {
            // Explicit credentials with no configured default: build a request-scoped handler over a
            // fresh transport (owned and disposed with the request).
            handler = new LocalAuthHandler(credentials, new HttpClientHandler());
            ownsHandler = true;
        }
        else if (credentials is not null)
        {
            // Explicit credentials with a configured default: wrap the shared transport (not disposed —
            // it is the factory's / a deferred test handler, reused across calls).
            handler = new LocalAuthHandler(credentials, configured!);
            ownsHandler = false;
        }
        else if (configured is not null)
        {
            handler = configured;
            ownsHandler = false;
        }
        else
        {
            throw new InvalidOperationException(
                "Local moderation requires LocalCredentials (set ActivityPubClientOptions.LocalCredentials) or explicit credentials.");
        }

        // The target is an absolute IRI; the catch-all route on the server preserves it. A removal is
        // signalled by ?{removeQuery}=true (the same route records the edge otherwise). The request has
        // no body (the target is in the path), so it is sent unsigned through the local-auth handler (not
        // the signed pipeline, which would throw — a local decision is not a federated activity).
        var removeQueryString = remove ? $"?{removeQuery}=true" : string.Empty;
        var requestUri = new Uri($"{actorId.Value.TrimEnd('/')}/{path}/{targetId.Value.TrimStart('/')}{removeQueryString}");
        using var localHttp = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        using var response = await localHttp.SendAsync(request, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    /// <inheritdoc/>
    public Task<int> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
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

        // Delivered to the author's own inbox (the "local post" path): the author's instance records
        // the note and federates it to followers.
        return DeliverAsync(actorId.InboxOf(), create, ct);
    }

    /// <inheritdoc/>
    public Task<int> PostReplyAsync(
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

        return DeliverAsync(actorId.InboxOf(), create, ct);
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

        // The q value is URL-encoded (it may contain spaces / non-ASCII); limit/offset are numeric.
        var encodedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var searchIri = new Iri($"{instanceBase.Value}/search?q={encodedQuery}&limit={limit}&offset={offset}");

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
            obj = await GetObjectFromNetworkAsync(pageIri, ct).ConfigureAwait(false);
        }
        else
        {
            var (value, _) = await _collectionPageCache.GetAsync(
                pageIri,
                bypassCache,
                async iri => await GetObjectFromNetworkAsync(iri, ct).ConfigureAwait(false),
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
