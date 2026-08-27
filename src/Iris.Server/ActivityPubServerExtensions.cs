using System.Net.Http;
using System.Security.Cryptography;
using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Iris.Server;

/// <summary>
/// Extension methods that add ActivityPub server capability to an ASP.NET Core application.
/// </summary>
/// <remarks>
/// <see cref="AddActivityPubServer(IServiceCollection)"/> registers the persistence provider,
/// options, key infrastructure, and the credential validator. <see cref="MapActivityPubEndpoints(IEndpointRouteBuilder)"/>
/// maps the versioned ActivityPub endpoints (actor document, WebFinger, NodeInfo) under the
/// <c>/ap/v1</c> route prefix (Resolved Decision #10).
/// </remarks>
public static class ActivityPubServerExtensions
{
    /// <summary>
    /// Adds the ActivityPub server services to the service collection.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddActivityPubServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddActivityPubServer(services, _ => { });
    }

    /// <summary>
    /// Adds the ActivityPub server services to the service collection, applying the given options.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <param name="configure">A callback to configure <see cref="ActivityPubServerOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddActivityPubServer(
        this IServiceCollection services,
        Action<ActivityPubServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // The credential validator for the owner-only actor document extension. The default is a
        // safe no-op (never includes the privateKey extension); a host app replaces this with
        // BasicAuthCredentialValidator (or another implementation) to enable the authenticated path.
        services.TryAddSingleton<IActorCredentialValidator, DefaultActorCredentialValidator>();

        // The signing key provider for the local actor (Phase 4 delivery signs with the actor's key).
        services.TryAddSingleton<IKeyProvider, InMemoryKeyProvider>();

        // The server-side object caches (remote actors, remote keys, collection pages, WebFinger).
        // The TTLs come from ActivityPubServerOptions.CachePolicies (ServerCachePolicies); a null
        // policy falls back to the CachePolicy default for that object type. These are the building
        // blocks for the server's outbound federation paths (Phase 4); they are registered now so
        // the seam is in place and unit-testable.
        // The remote-actor and remote-key caches are registered standalone (not just inside
        // ServerCaches) so the outbound paths can resolve them directly by type: the actor-document
        // fetcher (IrisActorDocumentFetcher) reads/writes the actor-doc cache, and the inbound key
        // resolver (RemoteInboundKeyResolver) reads/writes the key cache.
        services.TryAddSingleton<RemoteActorCache>(sp =>
        {
            var policies = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.CachePolicies;
            return new RemoteActorCache(policies?.RemoteActor);
        });

        services.TryAddSingleton<RemoteKeyCache>(sp =>
        {
            var policies = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.CachePolicies;
            return new RemoteKeyCache(policies?.RemoteKey);
        });

        // The WebFinger cache is also registered standalone so the outbound account-resolution path
        // (WebFingerAccountResolver) can resolve it directly by type; ServerCaches reuses the same
        // instance below.
        services.TryAddSingleton<WebFingerCache>(sp =>
        {
            var policies = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.CachePolicies;
            return new WebFingerCache(policies?.WebFinger);
        });

        // The collection-page cache is also registered standalone so the outbound remote-collection
        // fetch path (IrisRemoteCollectionFetcher) can resolve it directly by type; ServerCaches reuses
        // the same instance below.
        services.TryAddSingleton<CollectionPageCache>(sp =>
        {
            var policies = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.CachePolicies;
            return new CollectionPageCache(policies?.CollectionPage);
        });

        services.TryAddSingleton<ServerCaches>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
            var policies = options.CachePolicies;
            return new ServerCaches(
                RemoteActors: sp.GetRequiredService<RemoteActorCache>(),
                RemoteKeys: sp.GetRequiredService<RemoteKeyCache>(),
                CollectionPages: sp.GetRequiredService<CollectionPageCache>(),
                WebFinger: sp.GetRequiredService<WebFingerCache>());
        });

        // The server → client response cache: rendered local actor documents, backing the actor
        // document endpoint's Cache-Control headers and ?refresh=true bypass (public docs only; the
        // authenticated owner-only document is never cached).
        services.TryAddSingleton<LocalActorDocumentCache>(_ => new LocalActorDocumentCache());

        // The server → client response cache for paged local collections (outbox/followers/following),
        // backing those endpoints' Cache-Control headers and ?refresh=true bypass.
        services.TryAddSingleton<LocalCollectionPageCache>(_ => new LocalCollectionPageCache());

        // Inbound signature validation (Phase 4). The server verifies the HTTP signature on inbound
        // requests by resolving the remote signing key (fetched from the remote actor's document) and
        // checking it cryptographically. A host app (or test) may replace IActorDocumentFetcher /
        // IInboundKeyResolver / ISignatureValidator to customize key resolution or validation policy.
        services.TryAddSingleton<IActivityPubClientFactory, ActivityPubClientFactory>();
        services.TryAddSingleton<ISignatureVerifier, HttpSignatureVerifier>();
        services.TryAddSingleton<IInboundKeyResolver, RemoteInboundKeyResolver>();
        services.TryAddSingleton<ISignatureValidator, HttpSignatureValidator>();
        services.TryAddSingleton<IActorDocumentFetcher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;

            // Without a configured instance actor the default fetcher cannot sign outbound fetches,
            // so it degrades to a no-op (remote keys cannot be resolved → remote signatures fail
            // validation). A host that sets ActivityPubServerOptions.InstanceActorId gets full
            // inbound key resolution. A test that needs in-process routing (the federation test)
            // replaces this registration with one wired to the other TestServer.
            if (options.InstanceActorId is null)
            {
                return new NoopActorDocumentFetcher();
            }

            var factory = sp.GetRequiredService<IActivityPubClientFactory>();
            var clientOptions = new ActivityPubClientOptions
            {
                ActorId = options.InstanceActorId.Value,
                // Inbound key-resolution fetches do not need retries; keep the pipeline minimal.
                EnableRetry = false,
            };

            // A real transport handler: the default fetch goes to the remote instance's public URL.
            // The fetch reads through the remote-actor cache (Phase 3), so a remote actor's document
            // is fetched once and reused across key resolutions and deliveries.
            var actorCache = sp.GetRequiredService<RemoteActorCache>();
            return new IrisActorDocumentFetcher(factory.Create(clientOptions, new HttpClientHandler()), actorCache);
        });

        // Outbound remote-collection fetch (Phase 4): fetches a single page of a remote actor's
        // collection (e.g. a remote actor's outbox/followers), reading through the Phase 3
        // CollectionPageCache so a page is fetched once and reused within the TTL. The outbound
        // transport is a real HttpClientHandler (goes to the remote instance's public URL); a host or
        // test replaces IRemoteCollectionFetcher with one wired to the other TestServer.
        services.TryAddSingleton<IRemoteCollectionFetcher>(sp =>
        {
            var factory = sp.GetRequiredService<IActivityPubClientFactory>();
            var options = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;

            // Without a configured instance actor the fetcher cannot sign outbound fetches, so it
            // cannot resolve remote pages. A host that sets ActivityPubServerOptions.InstanceActorId
            // gets full remote-collection fetching.
            if (options.InstanceActorId is null)
            {
                throw new InvalidOperationException(
                    "IRemoteCollectionFetcher requires ActivityPubServerOptions.InstanceActorId to be set (outbound fetches must be signed).");
            }

            var clientOptions = new ActivityPubClientOptions
            {
                ActorId = options.InstanceActorId.Value,
                // Outbound collection fetches do not need retries; keep the pipeline minimal.
                EnableRetry = false,
            };

            var collectionPages = sp.GetRequiredService<CollectionPageCache>();
            return new IrisRemoteCollectionFetcher(factory.Create(clientOptions, new HttpClientHandler()), collectionPages);
        });

        // Inbox processing (Phase 4): the processor stores each validated activity and dispatches it
        // to the registered activity handlers. The default set interprets the follow lifecycle:
        // Follow (records the local follow edge + schedules the Accept response), Accept (finalizes a
        // local follower's provisional follow when the followed side accepts), and Reject (undoes it
        // when the followed side rejects). Announce (records the announce in the recipient's outbox
        // and propagates it to the recipient's local followers' inboxes, so a boost is visible to a
        // local follower's client). A host may add more IActivityHandler registrations
        // (e.g. Create) to extend the pipeline; the processor picks them up automatically.
        services.TryAddSingleton<ILocalActorResolver, DefaultLocalActorResolver>();
        // The activity handlers are an OPEN list: each is a distinct implementation registered under
        // the same service type (IActivityHandler), so AddSingleton (not TryAddSingleton) is required —
        // TryAddSingleton would treat the second and later registrations as duplicates of the first
        // (the same ServiceType) and skip them, leaving only the FollowActivityHandler. A host may add
        // more IActivityHandler registrations (e.g. Create) to extend the pipeline.
        services.AddSingleton<IActivityHandler, FollowActivityHandler>();
        services.AddSingleton<IActivityHandler, AcceptActivityHandler>();
        services.AddSingleton<IActivityHandler, RejectActivityHandler>();
        services.AddSingleton<IActivityHandler, AnnounceActivityHandler>();
        services.AddSingleton<IActivityHandler, CommunityInboxActivityHandler>();
        services.TryAddSingleton<IInboxProcessor, InboxProcessor>();

        // Community feed (Phase 5): computes a community's unified feed (the union of its local
        // members' outbox activities, newest first) for the /c/{name}/feed endpoint and the client's
        // GetCommunityFeedAsync. A host may replace this to add followed-community content or ranking.
        services.TryAddSingleton<ICommunityFeedService, CommunityFeedService>();

        // Outbound delivery (Phase 4): the delivery queue (in-memory Channel<T>), the delivery service
        // (handlers call it to schedule a delivery — it enqueues and returns), and the background
        // DeliveryWorker (pumps jobs off the queue and POSTs them, signed as InstanceActorId).
        // The outbound transport is a Func<HttpMessageHandler> seam: the default is a real
        // HttpClientHandler (goes to the recipient's public URL); a host or test overrides it to route
        // deliveries (e.g. to a TestServer in-process, or to an IHttpClientFactory-backed handler for
        // proxying/timeouts). DeliveryWorker is registered as a hosted service so it starts with the host.
        services.TryAddSingleton<IDeliveryQueue, InMemoryDeliveryQueue>();
        services.TryAddSingleton<IDeliveryService, DeliveryService>();
        services.TryAddSingleton<Func<HttpMessageHandler>>(_ => () => new HttpClientHandler());
        services.AddHostedService<DeliveryWorker>();

        // Outbound account resolution (Phase 4): resolves a remote account (e.g. @bob@b.test) to its
        // actor IRI via WebFinger, reading through the Phase 3 WebFingerCache. The WebFingerClient is
        // backed by a plain (unsigned, no content-negotiation) HTTP pipeline — WebFinger (RFC 8410)
        // is not ActivityPub, so it must not carry the activity+json Accept header or an HTTP
        // signature. A host (or test) overrides the Func<HttpMessageHandler> seam above to route the
        // request (e.g. in-process to a TestServer).
        services.TryAddSingleton<WebFingerClient>(sp =>
        {
            var handlerFactory = sp.GetRequiredService<Func<HttpMessageHandler>>();
            return new WebFingerClient(new HttpClient(handlerFactory(), disposeHandler: false));
        });

        // The resolver contract the account resolver depends on; the WebFingerClient is the default
        // implementation. Registered so a host (or test) may swap in a different resolver.
        services.TryAddSingleton<IWebFingerResolver>(sp => sp.GetRequiredService<WebFingerClient>());
        services.TryAddSingleton<IAccountResolver, WebFingerAccountResolver>();

        // NOTE: IPersistenceProvider is a seam — it is registered by the persistence package
        // (e.g. Iris.Server.InMemory's AddInMemoryPersistence) or by a host app. AddActivityPubServer
        // does NOT register a concrete persistence provider, keeping Iris.Server free of a dependency
        // on any specific persistence implementation.

        return services;
    }

    /// <summary>
    /// Adds the <see cref="SignatureValidationMiddleware"/> to the pipeline. Call this before
    /// <c>UseRouting</c>/<c>UseEndpoints</c> so inbound ActivityPub requests are signature-validated
    /// before they reach the endpoints.
    /// </summary>
    /// <param name="app">The application builder. Must not be null.</param>
    /// <returns>The application builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="app"/> is null.</exception>
    public static IApplicationBuilder UseSignatureValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SignatureValidationMiddleware>();
    }

    /// <summary>
    /// Maps the versioned ActivityPub server endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder. Must not be null.</param>
    /// <returns>The endpoint route builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="endpoints"/> is null.</exception>
    public static IEndpointRouteBuilder MapActivityPubEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(ActivityPubServerConstants.RoutePrefix);

        // Every response carries the meta version header (Resolved Decision #10).
        group.AddEndpointFilter(
            async (context, next) =>
            {
                context.HttpContext.Response.Headers[ActivityPubServerConstants.VersionHeaderName] =
                    ActivityPubServerConstants.ApiVersion;
                return await next(context).ConfigureAwait(false);
            });

        // Actor document: GET /ap/v1/u/{handle}. Public by default; includes the owner-only
        // privateKey + keyAlgorithm extensions when the request is authenticated (Basic auth).
        group.MapGet("/u/{handle}", ActorDocumentHandler);

        // WebFinger: GET /ap/v1/.well-known/webfinger?resource=acct:{handle}@{host}.
        group.MapGet("/.well-known/webfinger", WebFingerHandler);

        // WebFinger at the RFC 8410 standard root path (/.well-known/webfinger). RFC 8410 defines the
        // well-known URI at the host root (not under a versioned prefix), so a remote client resolving
        // an account via WebFinger — the standard discovery mechanism — must be able to reach it here.
        // This is the path the client's WebFingerClient queries; without it, an Iris instance could not
        // resolve another Iris instance's accounts. The versioned route above is retained for symmetry.
        endpoints.MapGet("/.well-known/webfinger", WebFingerHandler);

        // NodeInfo: GET /ap/v1/nodeinfo/2.0 (RFC 8555 instance metadata).
        group.MapGet("/nodeinfo/2.0", NodeInfoHandler);

        // NodeInfo discovery root: GET /ap/v1/.well-known/nodeinfo (links to /nodeinfo/2.0).
        group.MapGet("/.well-known/nodeinfo", NodeInfoWellKnownHandler);

        // Inbox: POST /ap/v1/u/{handle}/inbox — receives federation activities (Follow, Accept,
        // Create, ...). Requires a valid HTTP signature (validated by SignatureValidationMiddleware);
        // unsigned or invalidly-signed requests are rejected with 401.
        group.MapPost("/u/{handle}/inbox", InboxHandler);

        // Paged collections: GET /ap/v1/u/{handle}/{collection} where {collection} is one of outbox
        // (the actor's posted activities, newest first), followers (actors following the local actor),
        // or following (actors the local actor follows). Each serves an OrderedCollection (page 1,
        // with `first`) or an OrderedCollectionPage (page N>1), paged via ?page=N and ?limit=N, and
        // served through the local collection-page response cache. The {collection} route value is
        // bound as `collectionName` (it is not a query parameter).
        group.MapGet(
                "/u/{handle}/{collection:regex(outbox|followers|following)}",
                (string handle, string collection, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CollectionEndpointHandler(handle, collection, context, persistence, optionsAccessor, collectionCache, ct))
            .WithName("collection-endpoint");

        // Community document: GET /ap/v1/c/{name} — the community (the library's Group actor) document.
        // A community is addressed by its handle (not an actor IRI), so the route uses {name}.
        group.MapGet("/c/{name}", CommunityDocumentHandler);

        // Community members: GET /ap/v1/c/{name}/members — the community's member actor IRIs, served as
        // a paged collection (page 1 is an OrderedCollection with `first`; page N>1 an OrderedCollectionPage).
        group.MapGet("/c/{name}/members", CommunityMembersHandler);

        // Community feed: GET /ap/v1/c/{name}/feed — the community's unified feed (the union of its
        // local members' outbox activities, newest first), served as a paged collection.
        group.MapGet("/c/{name}/feed", CommunityFeedHandler);

        // Community search: GET /ap/v1/c/{name}/search — a specialized collection that searches the
        // community's content (the feed surface) case-insensitively via ?q, paged via ?limit/?offset
        // (the shared limit/offset pagination shape, Resolved Decision #6).
        group.MapGet("/c/{name}/search", CommunitySearchHandler);

        // Community inbox: POST /ap/v1/c/{name}/inbox — receives federation activities addressed to the
        // community (e.g. a Follow from a remote actor, or a Create/Announce from a followed community).
        // Requires a valid HTTP signature (validated by SignatureValidationMiddleware); unsigned or
        // invalidly-signed requests are rejected with 401.
        group.MapPost("/c/{name}/inbox", CommunityInboxHandler);

        return endpoints;
    }

    // --- Endpoint handlers -----------------------------------------------------

    private static async Task<IResult> ActorDocumentHandler(
        HttpContext context,
        string handle,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        IActorCredentialValidator credentialValidator,
        LocalActorDocumentCache actorDocumentCache,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // Determine whether the request is authenticated for this actor (owner-only extension).
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);

        // Owner-only (authenticated) document: private data. Never cached; always no-store.
        if (authenticatedHandle is not null)
        {
            if (!await persistence.Actors.TryGetActorAsync(actorIri, out var ownerActor, ct).ConfigureAwait(false) ||
                ownerActor is null)
            {
                return Results.NotFound();
            }

            var ownerDoc = BuildActorDocument(ownerActor, actorIri, authenticatedHandle, persistence, options);
            var noStore = Results.Text(ActivityJson.Serialize(ownerDoc), ActivityJson.ActivityJsonContentType);
            context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
                ActivityPubServerConstants.NoStoreCacheControl;
            return noStore;
        }

        // Public document: served through the local actor document cache (server → client layer).
        // ?refresh=true bypasses the read (re-fetch from persistence) but still writes back.
        var forceRefresh = HasRefreshBypass(context);
        var (rendered, _, _) = await actorDocumentCache
            .GetAsync(
                actorIri,
                forceRefresh,
                async key =>
                {
                    if (await persistence.Actors.TryGetActorAsync(key, out var actor, ct).ConfigureAwait(false) &&
                        actor is not null)
                    {
                        var doc = BuildActorDocument(actor, key, null, persistence, options);
                        return ActivityJson.Serialize(doc);
                    }

                    return null;
                },
                ct)
            .ConfigureAwait(false);

        if (rendered is null)
        {
            return Results.NotFound();
        }

        // Cache-Control: only an explicit ?refresh=true bypass emits no-cache (the value was just
        // re-fetched; intermediates must not serve a stale copy). A fresh hit, a stale-while-revalidate
        // hit, and a first fetch (a miss we now populate) are all cacheable: max-age=60,
        // stale-while-revalidate=300.
        var cacheControl = forceRefresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.ActorCacheControl;
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = cacheControl;
        return Results.Text(rendered, ActivityJson.ActivityJsonContentType);
    }

    private static async Task<IResult> InboxHandler(
        HttpContext context,
        string handle,
        IPersistenceProvider persistence,
        IInboxProcessor inboxProcessor,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        // Signature validation policy: an inbox POST must be signed and valid. The middleware
        // buffered the body and stored the outcome; an unsigned or invalid signature → 401.
        var outcome = SignatureValidationMiddleware.GetResult(context);
        if (!outcome.IsValid)
        {
            return Results.Unauthorized();
        }

        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // The inbox belongs to a local actor; an unknown handle → 404.
        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // The body was buffered by the middleware; re-read it from the start.
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest();
        }

        // Rule 1: deserialize into the range interface, then cast.
        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);
        if (payload is not Activity { Id: not null } activity)
        {
            return Results.BadRequest();
        }

        // Hand the validated activity to the inbox processor: it stores the activity and dispatches
        // it to the registered activity handler for its type (e.g. Follow records the follow edge).
        // A handler failure is a server-side error → 500 (the activity may already be stored; the
        // remote can retry, and the store is idempotent on the activity IRI).
        try
        {
            await inboxProcessor
                .ProcessAsync(new InboxDelivery(actorIri, activity), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Returns true when the request carries the <c>?refresh=true</c> bypass (case-insensitive,
    /// any truthy value).
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns><see langword="true"/> when the refresh bypass is requested.</returns>
    private static bool HasRefreshBypass(HttpContext context)
        => context.Request.Query.TryGetValue(
            ActivityPubServerConstants.RefreshQueryParameterName,
            out var values) && values.Count > 0 && values[0] is not null and not "false";

    private static Actor BuildActorDocument(
        Actor actor,
        Iri actorIri,
        string? authenticatedHandle,
        IPersistenceProvider persistence,
        ActivityPubServerOptions options)
    {
        // Deep-copy via serialize/deserialize so we never mutate the stored actor.
        var doc = ActivityJson.Deserialize<Actor>(ActivityJson.Serialize(actor))!;

        // Ensure the document carries the standard collection endpoints (inbox/outbox/followers/following).
        doc.Id ??= actorIri.Value;
        doc.Inbox ??= new Link { Href = new Uri(actorIri.InboxOf().Value) };
        doc.Outbox ??= new Link { Href = new Uri(actorIri.OutboxOf().Value) };
        doc.Followers ??= new Link { Href = new Uri(actorIri.FollowersOf().Value) };
        doc.Following ??= new Link { Href = new Uri(actorIri.FollowingOf().Value) };

        // If authenticated as the owner, include the privateKey + keyAlgorithm extensions.
        if (authenticatedHandle is not null)
        {
            var ext = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            var keyIdIri = ResolveKeyIri(doc, actorIri);
            if (persistence.Keys.TryGetKey(keyIdIri, out var keyPair) && keyPair is not null)
            {
                ext[ActivityPubServerConstants.PrivateKeyExtensionName] =
                    System.Text.Json.JsonSerializer.SerializeToElement(keyPair.ExportPrivateKeyPem());
                ext[ActivityPubServerConstants.KeyAlgorithmExtensionName] =
                    System.Text.Json.JsonSerializer.SerializeToElement(KeyAlgorithmLabel(keyPair.Algorithm));
            }
        }

        return doc;
    }

    /// <summary>
    /// Builds the absolute actor IRI for a local handle, normalizing the base URL (strips a trailing
    /// slash so the path segment is appended cleanly, avoiding a double slash).
    /// </summary>
    /// <param name="baseUrl">The base URL of the instance (may have a trailing slash).</param>
    /// <param name="handle">The local actor handle.</param>
    /// <returns>The absolute actor IRI.</returns>
    private static Iri BuildActorIri(string baseUrl, string handle)
    {
        var normalized = baseUrl.TrimEnd('/');
        return new Iri($"{normalized}{ActivityPubServerConstants.RoutePrefix}/u/{handle}");
    }

    /// <summary>
    /// Builds the absolute community IRI for a local community name, normalizing the base URL (strips a
    /// trailing slash so the path segment is appended cleanly, avoiding a double slash).
    /// </summary>
    /// <param name="baseUrl">The base URL of the instance (may have a trailing slash).</param>
    /// <param name="name">The local community name (the <c>{name}</c> route segment).</param>
    /// <returns>The absolute community IRI.</returns>
    private static Iri BuildCommunityIri(string baseUrl, string name)
    {
        var normalized = baseUrl.TrimEnd('/');
        return new Iri($"{normalized}{ActivityPubServerConstants.RoutePrefix}/c/{name}");
    }

    private static Iri ResolveKeyIri(Actor actor, Iri actorIri)
    {
        // The key IRI is the actor's publicKey.id (ActivityPub convention). The library carries
        // publicKey in ExtensionData (it's not a typed property). Fall back to the actor IRI with
        // a #key-1 fragment when the document doesn't carry an explicit key id.
        if (actor.ExtensionData is { } ext && ext.TryGetValue("publicKey", out var pk))
        {
            if (pk.ValueKind == System.Text.Json.JsonValueKind.Object && pk.TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var id = idEl.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return new Iri(id);
                }
            }
        }

        return new Iri(actorIri.Value + "#key-1");
    }

    private static string KeyAlgorithmLabel(KeyAlgorithm algorithm) => algorithm switch
    {
        KeyAlgorithm.Rsa => ActivityPubServerConstants.KeyAlgorithmRsa,
        KeyAlgorithm.EcP256 => ActivityPubServerConstants.KeyAlgorithmEcP256,
        _ => throw new NotSupportedException($"Algorithm {algorithm} is not supported."),
    };

    private static async Task<IResult> WebFingerHandler(
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var resource = context.Request.Query["resource"].ToString();
        if (string.IsNullOrWhiteSpace(resource) || !resource.StartsWith("acct:", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }

        var acct = resource["acct:".Length..];
        var at = acct.IndexOf('@');
        if (at < 0)
        {
            return Results.NotFound();
        }

        var handle = acct[..at];
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // The instance host (for the acct: subject) is derived from the base URL, not the request
        // host (which may differ, e.g. in tests or behind a proxy).
        var instanceHost = new Uri(baseUrl).Host;
        // WebFinger response: { subject, links: [{ rel: self, type: activity+json, href: actorIri }] }.
        var webFinger = new
        {
            subject = $"acct:{handle}@{instanceHost}",
            links = new[]
            {
                new
                {
                    rel = "self",
                    type = ActivityJson.ActivityJsonContentType,
                    href = actorIri.Value,
                },
            },
        };

        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(webFinger),
            "application/json");
    }

    private static IResult NodeInfoHandler(IOptions<ActivityPubServerOptions> optionsAccessor)
    {
        var options = optionsAccessor.Value;
        var nodeInfo = new
        {
            version = "2.0",
            software = new { name = "iris", version = ActivityPubServerConstants.ApiVersion },
            protocols = new[] { "activitypub" },
            usage = new { users = new { total = 0 } },
            openRegistrations = false,
            metadata = new
            {
                name = options.InstanceName ?? "Iris",
                description = options.InstanceName ?? "An Iris ActivityPub instance",
            },
        };

        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(nodeInfo),
            "application/json");
    }

    private static IResult NodeInfoWellKnownHandler(IOptions<ActivityPubServerOptions> optionsAccessor)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? throw new InvalidOperationException("BaseUri is not configured; cannot build the NodeInfo discovery link.");
        var link = new
        {
            links = new[]
            {
                new
                {
                    rel = "http://nodeinfo.dpl.dev/ns/1.0/nodeinfo",
                    version = "2.0",
                    href = $"{baseUrl}{ActivityPubServerConstants.RoutePrefix}/nodeinfo/2.0",
                },
            },
        };

        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(link),
            "application/json");
    }

    // --- Paged collection endpoints (outbox / followers / following) -----------

    /// <summary>
    /// Serves a local actor's <c>outbox</c>, <c>followers</c>, or <c>following</c> collection as a paged
    /// <see cref="OrderedCollection"/> (page 1, carrying <c>first</c>) or <see cref="OrderedCollectionPage"/>
    /// (page N &gt; 1). The request's <c>?page</c> (default 1) and <c>?limit</c> (default
    /// <see cref="ActivityPubServerConstants.DefaultCollectionPageSize"/>, capped at
    /// <see cref="ActivityPubServerConstants.MaxCollectionPageSize"/>) select the page; <c>?refresh=true</c>
    /// bypasses the local collection-page response cache for the read. The response is served through the
    /// <see cref="LocalCollectionPageCache"/> and carries the collection <c>Cache-Control</c> header.
    /// </summary>
    private static async Task<IResult> CollectionEndpointHandler(
        string handle,
        string collectionName,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false)
            || actor is null)
        {
            return Results.NotFound();
        }

        // Resolve the collection items (newest-first outbox; insertion-ordered followers/following).
        IReadOnlyList<IObjectOrLink> items = collectionName switch
        {
            "outbox" => await persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false),
            "followers" => ActorIrisToLinks(await persistence.Follows.GetFollowersAsync(actorIri, ct).ConfigureAwait(false)),
            "following" => ActorIrisToLinks(await persistence.Follows.GetFollowingAsync(actorIri, ct).ConfigureAwait(false)),
            _ => [],
        };

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());
        var refresh = context.Request.Query["refresh"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        var collectionIri = new Iri($"{actorIri}/{collectionName}");
        var pageIri = page == 1 ? collectionIri : new Iri($"{collectionIri}/?page={page}");

        // Read (or render on a miss) through the local collection-page response cache.
        var (document, _, _) = await collectionCache.GetAsync(
            pageIri,
            refresh,
            _ => Task.FromResult<string?>(BuildCollectionPageDocument(
                collectionIri,
                page,
                limit,
                items)),
            ct).ConfigureAwait(false);

        // Cache-Control: only an explicit ?refresh=true bypass emits no-cache (the value was just
        // re-rendered; intermediates must not serve a stale copy). A fresh hit, a stale-while-revalidate
        // hit, and a first render (a miss we now populate) are all cacheable.
        var cacheControl = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = cacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the community (the library's <c>Group</c> actor) document for <c>GET /ap/v1/c/{name}</c>.
    /// The community is addressed by its handle (not an actor IRI), so the route uses <c>{name}</c>.
    /// </summary>
    private static async Task<IResult> CommunityDocumentHandler(
        HttpContext context,
        string name,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out var community, ct).ConfigureAwait(false)
            || community is null)
        {
            return Results.NotFound();
        }

        // Deep-copy via serialize/deserialize so we never mutate the stored community, and ensure the
        // document carries the standard collection endpoints (inbox/outbox/followers/following) + members.
        var doc = ActivityJson.Deserialize<Group>(ActivityJson.Serialize(community))!;
        doc.Id ??= communityIri.Value;
        doc.Inbox ??= new Link { Href = new Uri(communityIri.InboxOf().Value) };
        doc.Outbox ??= new Link { Href = new Uri(communityIri.OutboxOf().Value) };
        doc.Followers ??= new Link { Href = new Uri(communityIri.FollowersOf().Value) };
        doc.Following ??= new Link { Href = new Uri(communityIri.FollowingOf().Value) };
        var ext = doc.ExtensionData ?? new Dictionary<string, System.Text.Json.JsonElement>();
        var changed = false;
        if (!ext.ContainsKey("members"))
        {
            ext["members"] = System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/members");
            changed = true;
        }

        if (!ext.ContainsKey("feed"))
        {
            ext["feed"] = System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/feed");
            changed = true;
        }

        if (!ext.ContainsKey("search"))
        {
            ext["search"] = System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/search");
            changed = true;
        }

        // The iris:capabilities extension (Resolved Decision #11) declares the community's available
        // specialized collections (feed/members/search) for client discovery. The full term is
        // {NamespaceIri}capabilities (configurable per-deployment, Resolved Decision #9; the canonical
        // default when unset, Resolved Decision #1).
        var capabilitiesTerm =
            (options.NamespaceIri?.Value ?? ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri) +
            ActivityPubServerConstants.CapabilitiesTerm;
        if (!ext.ContainsKey(capabilitiesTerm))
        {
            ext[capabilitiesTerm] = System.Text.Json.JsonSerializer.SerializeToElement(new[]
            {
                ActivityPubServerConstants.CapabilityFeed,
                ActivityPubServerConstants.CapabilityMembers,
                ActivityPubServerConstants.CapabilitySearch,
            });
            changed = true;
        }

        if (changed)
        {
            doc.ExtensionData = ext;
        }

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.ActorCacheControl;
        return Results.Text(ActivityJson.Serialize(doc), ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the community's member actor IRIs as a paged collection for <c>GET /ap/v1/c/{name}/members</c>.
    /// Page 1 is an <c>OrderedCollection</c> (with <c>first</c>); page N &gt; 1 is an
    /// <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>), paged via <c>?page</c>/<c>?limit</c>.
    /// </summary>
    private static async Task<IResult> CommunityMembersHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var memberIris = await persistence.Communities.GetMembersAsync(communityIri, ct).ConfigureAwait(false);
        var items = ActorIrisToLinks(memberIris.ToList());

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());

        var collectionIri = new Iri($"{communityIri.Value}/members");
        var pageIri = page == 1 ? collectionIri : new Iri($"{collectionIri.Value}/?page={page}");
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the community's unified feed as a paged collection for <c>GET /ap/v1/c/{name}/feed</c>.
    /// The feed is the union of the community's local members' outbox activities (newest first),
    /// computed by the <see cref="ICommunityFeedService"/>. Page 1 is an <c>OrderedCollection</c> (with
    /// <c>first</c>); page N &gt; 1 is an <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>),
    /// paged via <c>?page</c>/<c>?limit</c>.
    /// </summary>
    private static async Task<IResult> CommunityFeedHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        ICommunityFeedService feedService,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var items = await feedService.GetFeedAsync(communityIri, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());

        var collectionIri = new Iri($"{communityIri.Value}/feed");
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the community's content search as a specialized collection for
    /// <c>GET /ap/v1/c/{name}/search</c>. The search matches the community's content (the feed surface —
    /// the union of the local members' outbox activities) case-insensitively via the <c>?q</c> query,
    /// and pages the matching items via the shared <c>?limit</c>/<c>?offset</c> shape (Resolved Decision
    /// #6). The page 1 document is an <c>OrderedCollection</c> (with <c>first</c>); a page N &gt; 1 is an
    /// <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>) carrying this page's slice
    /// in the <c>items</c> array and the full match count in <c>totalItems</c>.
    /// </summary>
    /// <remarks>
    /// An unknown community 404s. An empty/absent <c>?q</c> matches all items (the feed, unfiltered), so
    /// the endpoint also serves as a plain paged listing of the community's content. A <c>?limit</c> is
    /// bounded (default <see cref="ActivityPubServerConstants.DefaultCollectionPageSize"/>, capped at
    /// <see cref="ActivityPubServerConstants.MaxCollectionPageSize"/>); a <c>?offset</c> is a 0-based
    /// position (default 0, negative clamped to 0). The response carries the collection
    /// <c>Cache-Control</c>.
    /// </remarks>
    private static async Task<IResult> CommunitySearchHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        ICommunityFeedService feedService,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var query = context.Request.Query["q"].ToString();
        var items = await feedService.SearchCommunityAsync(communityIri, query, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var offset = ParseOffset(context.Request.Query[ActivityPubServerConstants.OffsetQueryParameterName].ToString());

        var collectionIri = new Iri($"{communityIri.Value}/search");
        var namespaceBase = options.NamespaceIri?.Value ?? ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri;
        var document = BuildSearchPageDocument(collectionIri, offset, limit, items, query, namespaceBase);

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Receives federation activities addressed to a community for <c>POST /ap/v1/c/{name}/inbox</c>.
    /// Mirrors the actor inbox: requires a valid signature (401 otherwise), 404 for an unknown community,
    /// deserializes the body into an <see cref="Activity"/> and hands it to the inbox processor.
    /// </summary>
    private static async Task<IResult> CommunityInboxHandler(
        HttpContext context,
        string name,
        IPersistenceProvider persistence,
        IInboxProcessor inboxProcessor,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var outcome = SignatureValidationMiddleware.GetResult(context);
        if (!outcome.IsValid)
        {
            return Results.Unauthorized();
        }

        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        // The inbox belongs to a local community; an unknown name → 404.
        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest();
        }

        // Rule 1: deserialize into the range interface, then cast.
        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);
        if (payload is not Activity { Id: not null } activity)
        {
            return Results.BadRequest();
        }

        try
        {
            await inboxProcessor
                .ProcessAsync(new InboxDelivery(communityIri, activity), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Renders the JSON-LD document for a single page of a local collection. Page 1 is an
    /// <c>OrderedCollection</c> (with <c>first</c> self-referencing it); page N &gt; 1 is an
    /// <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>). The <c>items</c> array holds
    /// this page's slice; the <c>totalItems</c> property carries the full collection size.
    /// </summary>
    /// <param name="collectionIri">The collection's IRI (<c>{actor}/{name}</c>).</param>
    /// <param name="page">The 1-based page number to render.</param>
    /// <param name="limit">The page size (items per page).</param>
    /// <param name="items">All of the collection's items (newest-first for the outbox), unslliced.</param>
    /// <returns>The serialized JSON-LD document for the requested page.</returns>
    private static string BuildCollectionPageDocument(
        Iri collectionIri,
        int page,
        int limit,
        IReadOnlyList<IObjectOrLink> items)
    {
        var total = items.Count;
        var pageCount = total == 0 ? 1 : (int)Math.Ceiling(total / (double)limit);
        if (page > pageCount)
        {
            page = pageCount;
        }

        var start = (page - 1) * limit + 1;
        var endExclusive = Math.Min(page * limit, total);
        var slice = new List<IObjectOrLink>();
        for (var i = start; i <= endExclusive; i++)
        {
            slice.Add(items[i - 1]);
        }

        if (page == 1)
        {
            // Page 1 is the collection document itself: it carries its own first page of items and a
            // self-referencing `first` link. The `next` pointer lives on the first page, so a reader
            // walks page 2 onward via the page's `next` (this document's own items are page 1).
            var collection = new OrderedCollection
            {
                Id = collectionIri.Value,
                Items = [.. slice],
                First = new Link { Href = new Uri(collectionIri.Value) },
                TotalItems = (uint)total,
            };

            return ActivityJson.Serialize(collection);
        }

        var pageDoc = new OrderedCollectionPage
        {
            Id = $"{collectionIri.Value}/?page={page}",
            PartOf = new Link { Href = new Uri(collectionIri.Value) },
            Items = [.. slice],
            StartIndex = (uint)start,
            TotalItems = (uint)total,
        };

        pageDoc.Prev = new Link { Href = new Uri($"{collectionIri.Value}/?page={page - 1}") };
        if (page < pageCount)
        {
            pageDoc.Next = new Link { Href = new Uri($"{collectionIri.Value}/?page={page + 1}") };
        }

        return ActivityJson.Serialize(pageDoc);
    }

    /// <summary>
    /// Coerces actor IRIs (from <c>IFollowStore</c>) into ActivityStreams <c>Link</c> objects so they can
    /// be embedded as collection items.
    /// </summary>
    /// <param name="actorIris">The actor IRIs.</param>
    /// <returns>A list of <see cref="Link"/> objects (one per IRI).</returns>
    private static IReadOnlyList<IObjectOrLink> ActorIrisToLinks(IReadOnlyList<Iri> actorIris)
    {
        var links = new IObjectOrLink[actorIris.Count];
        for (var i = 0; i < actorIris.Count; i++)
        {
            links[i] = new Link { Href = new Uri(actorIris[i].Value) };
        }

        return links;
    }

    /// <summary>
    /// Parses a <c>?limit</c> query value into a bounded page size (default
    /// <see cref="ActivityPubServerConstants.DefaultCollectionPageSize"/>, capped at
    /// <see cref="ActivityPubServerConstants.MaxCollectionPageSize"/>).
    /// </summary>
    /// <param name="raw">The raw query string (may be empty or non-numeric).</param>
    /// <returns>The clamped page size.</returns>
    private static int ParsePageSize(string raw)
    {
        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            return ActivityPubServerConstants.DefaultCollectionPageSize;
        }

        return Math.Min(value, ActivityPubServerConstants.MaxCollectionPageSize);
    }

    /// <summary>
    /// Parses a <c>?page</c> query value into a 1-based page number (default 1).
    /// </summary>
    /// <param name="raw">The raw query string (may be empty, non-numeric, or &lt; 1).</param>
    /// <returns>The 1-based page number.</returns>
    private static int ParsePageNumber(string raw)
    {
        if (!int.TryParse(raw, out var value) || value < 1)
        {
            return 1;
        }

        return value;
    }

    /// <summary>
    /// Parses a <c>?offset</c> query value into a 0-based offset (default 0; negative clamped to 0).
    /// </summary>
    /// <param name="raw">The raw query string (may be empty, non-numeric, or negative).</param>
    /// <returns>The clamped 0-based offset.</returns>
    private static int ParseOffset(string raw)
    {
        if (!int.TryParse(raw, out var value) || value < 0)
        {
            return 0;
        }

        return value;
    }

    /// <summary>
    /// Renders the JSON-LD document for one page of the community search (the
    /// <c>GET /c/{name}/search</c> specialized collection). The page is selected by a 0-based
    /// <paramref name="offset"/> and a <paramref name="limit"/> (the shared <c>limit</c>/<c>offset</c>
    /// pagination shape, Resolved Decision #6). Page 1 (offset 0) is an <c>OrderedCollection</c> (with a
    /// self-referencing <c>first</c>); a page beyond the first is an <c>OrderedCollectionPage</c> (with
    /// <c>partOf</c>/<c>prev</c>/<c>next</c>). The <c>items</c> array holds this page's slice;
    /// <c>totalItems</c> carries the full match count. When the search had a non-empty query, the page
    /// carries an <c>iris:searchQuery</c> extension (in the configurable namespace) recording it.
    /// </summary>
    /// <param name="collectionIri">The search collection's IRI (<c>{community}/search</c>).</param>
    /// <param name="offset">The 0-based offset of the first item on this page.</param>
    /// <param name="limit">The page size (items per page).</param>
    /// <param name="items">All matching items (in feed order), unsliced.</param>
    /// <param name="query">The search query (an empty/whitespace query records no extension).</param>
    /// <param name="namespaceBase">The <c>iris:</c> namespace base IRI (the configurable
    /// <see cref="ActivityPubServerOptions.NamespaceIri"/>, or the canonical default when unset) used to
    /// form the <c>iris:searchQuery</c> extension term.</param>
    /// <returns>The serialized JSON-LD document for the requested page.</returns>
    private static string BuildSearchPageDocument(
        Iri collectionIri,
        int offset,
        int limit,
        IReadOnlyList<IObjectOrLink> items,
        string? query,
        string namespaceBase)
    {
        var total = items.Count;
        var start = offset;
        var endExclusive = Math.Min(offset + limit, total);
        var slice = new List<IObjectOrLink>();
        for (var i = start; i < endExclusive; i++)
        {
            slice.Add(items[i]);
        }

        // Whether a query was supplied (records the iris:searchQuery extension when non-empty).
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var searchQueryTerm = $"{namespaceBase}searchQuery";
        var nextOffset = start + limit;
        var prevOffset = Math.Max(0, start - limit);

        if (start == 0)
        {
            // The first page (offset 0) is the collection document itself: it carries its own items and a
            // self-referencing `first` link. An `OrderedCollection` has no `next` property, so a
            // next-page link is recorded as the standard AS `next` extension (matching the page-2+
            // `next` so a reader can walk from page 1 onward).
            var collection = new OrderedCollection
            {
                Id = collectionIri.Value,
                Items = [.. slice],
                First = new Link { Href = new Uri(collectionIri.Value) },
                TotalItems = (uint)total,
            };

            if (nextOffset < total)
            {
                AddExtension(collection, "next", $"{collectionIri.Value}/?offset={nextOffset}&limit={limit}");
            }

            if (hasQuery)
            {
                AddSearchQueryExtension(collection, searchQueryTerm, query!);
            }

            return ActivityJson.Serialize(collection);
        }

        var pageDoc = new OrderedCollectionPage
        {
            Id = $"{collectionIri.Value}/?offset={offset}&limit={limit}",
            PartOf = new Link { Href = new Uri(collectionIri.Value) },
            Items = [.. slice],
            StartIndex = (uint)start,
            TotalItems = (uint)total,
        };

        pageDoc.Prev = new Link { Href = new Uri($"{collectionIri.Value}/?offset={prevOffset}&limit={limit}") };
        if (nextOffset < total)
        {
            pageDoc.Next = new Link { Href = new Uri($"{collectionIri.Value}/?offset={nextOffset}&limit={limit}") };
        }

        if (hasQuery)
        {
            AddSearchQueryExtension(pageDoc, searchQueryTerm, query!);
        }

        return ActivityJson.Serialize(pageDoc);
    }

    /// <summary>
    /// Records the <c>iris:searchQuery</c> extension (the search query) on a collection page document.
    /// </summary>
    private static void AddSearchQueryExtension(
        KristofferStrube.ActivityStreams.Object document,
        string searchQueryTerm,
        string query)
    {
        AddExtension(document, searchQueryTerm, query.Trim());
    }

    /// <summary>
    /// Records a scalar or link value under a term in a document's extension data (creating the
    /// dictionary when absent).
    /// </summary>
    private static void AddExtension(
        KristofferStrube.ActivityStreams.Object document,
        string term,
        object? value)
    {
        var ext = document.ExtensionData ?? new Dictionary<string, System.Text.Json.JsonElement>();
        ext[term] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        document.ExtensionData = ext;
    }
}

/// <summary>
/// The default <see cref="IActorCredentialValidator"/> — a no-op that always returns null (no
/// owner-only extension). A host app replaces this with <see cref="BasicAuthCredentialValidator"/>
/// (or another implementation) to enable the authenticated actor document path.
/// </summary>
/// <remarks>
/// This is a safe default: without a registered credential validator, the actor document never
/// includes the <c>privateKey</c> extension, so the private key is never leaked.
/// </remarks>
public sealed class DefaultActorCredentialValidator : IActorCredentialValidator
{
    /// <inheritdoc/>
    public Task<string?> TryValidateAsync(Iri actorIri, string? authorizationHeader, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
