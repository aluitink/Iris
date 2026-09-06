using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Identity;
using Iris.Server.Media;
using Iris.Server.Observability;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;
using KristofferStrube.ActivityStreams.JsonLD;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CollectionPageCache = Iris.Server.Caching.CollectionPageCache;
using WebFingerCache = Iris.Server.Caching.WebFingerCache;

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

        // Mute is an Iris-specific activity (there is no ActivityStreams Mute type), so the ActivityStreams
        // library does not know it: its ObjectConverter would serialize a MuteActivity as a generic object
        // (dropping the @context and type) and deserialize an inbound "type": "Mute" to a plain Object.
        // Register the Iris MuteActivity under its wire type so the library serializes it WITH its
        // @context/type (so the 202 body, the A → B federated delivery, and the re-read on an Undo all
        // round-trip) and deserializes an inbound "type": "Mute" back into a MuteActivity (24.2). The
        // registry is a shared mutable dictionary (idempotent guard).
        if (!ObjectTypes.Types.ContainsKey(MuteActivity.MuteType))
        {
            ObjectTypes.Types[MuteActivity.MuteType] = typeof(MuteActivity);
        }

        // The credential validator for the owner-only actor document extension. The default is a
        // safe no-op (never includes the privateKey extension); a host app replaces this with
        // BasicAuthCredentialValidator (or another implementation) to enable the authenticated path.
        services.TryAddSingleton<IActorCredentialValidator, DefaultActorCredentialValidator>();

        // The OAuth2 token store for the /ap/v1/oauth2/token + /ap/v1/oauth2/revoke endpoints
        // (Phase 15.2a). The default is in-memory; a host app replaces this with a database-backed
        // or Redis-backed store for production.
        services.TryAddSingleton<IOAuthTokenStore, InMemoryOAuthTokenStore>();

        // The signing key provider for the local actor (Phase 4 delivery signs with the actor's key).
        services.TryAddSingleton<IKeyProvider, InMemoryKeyProvider>();

        // The server-side id authority (decision 055): mints the collision-resistant, unguessable id
        // for every object/activity this instance creates (the outbox write path and the inbound
        // response paths). The authoring client sends the activity shape without an id; the server mints
        // it and returns it, so the id is never chosen by an untrusted client.
        services.TryAddSingleton<IdMinter>();

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
        // F-21: the validator receives the outbound key cache and actor-document cache so a
        // verification failure (a rotated remote key keeping the same key IRI) invalidates the stale
        // key AND the stale actor document (the re-resolve re-derives the key from the re-fetched
        // document) before re-resolving once.
        services.TryAddSingleton<ISignatureValidator>(sp => new HttpSignatureValidator(
            sp.GetRequiredService<IInboundKeyResolver>(),
            sp.GetRequiredService<ISignatureVerifier>(),
            sp.GetService<RemoteKeyCache>(),
            sp.GetService<RemoteActorCache>()));
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

        // Outbound object fetch (24.1): the server→server delivery target for a Like / Announce (and an
        // Undo of one) of a *remote* object is that object's author (attributedTo). A local object's owner
        // is read from the object store; a remote object's owner is resolved by fetching the object's
        // document over the wire and reading its attributedTo — without this the delivery would fall back
        // to the object IRI, whose "/inbox" does not exist (the remote instance never receives the
        // activity or its undo, so its edge is never recorded or removed). Registered only when an instance
        // actor is configured (the only case where the server can sign an outbound object fetch); when
        // absent, IActivityPubClient is left unregistered so the outbox handler's nullable
        // IActivityPubClient? parameter resolves to null (GetService).
        var configuredOptions = new ActivityPubServerOptions();
        configure(configuredOptions);
        if (configuredOptions.InstanceActorId is { } configuredInstanceActor)
        {
            services.TryAddSingleton<IActivityPubClient>(sp =>
                sp.GetRequiredService<IActivityPubClientFactory>().Create(
                    new ActivityPubClientOptions
                    {
                        ActorId = configuredInstanceActor,
                        EnableRetry = false,
                    },
                    new HttpClientHandler()));
        }

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

        // Media proxy (Phase 20.4 (d)): the unsigned outbound media fetch (DefaultMediaFetcher wraps a
        // pre-built HttpClient, per the coding style — no HttpClient ownership in library code). The
        // HttpClient is IHttpClientFactory-backed (a shared, handler-managing factory) with a generous
        // timeout: a media fetch is a plain GET of a remote attachment url, so it needs no signing,
        // retry, or activity-streams pipeline — just a bounded, cancellable read of the bytes.
        services.AddHttpClient(
            MediaFetcherClientName,
            client => client.Timeout = MediaFetchTimeout);
        services.TryAddSingleton<IMediaFetcher>(sp =>
            new DefaultMediaFetcher(sp.GetRequiredService<IHttpClientFactory>().CreateClient(MediaFetcherClientName)));
        // Eager-warm (Phase 20.4 (d), ON by default): pre-fetches a stored object's cross-origin
        // attachments so the media proxy serves them instantly. Best-effort and non-fatal.
        services.TryAddSingleton<IMediaWarmer, DefaultMediaWarmer>();

        // Inbox processing (Phase 4): the processor stores each validated activity and dispatches it
        // to the registered activity handlers. The default set interprets the follow lifecycle:
        // Follow (records the local follow edge + schedules the Accept response), Accept (finalizes a
        // local follower's provisional follow when the followed side accepts), and Reject (undoes it
        // when the followed side rejects). Announce (records the announce in the recipient's outbox
        // and propagates it to the recipient's local followers' inboxes, so a boost is visible to a
        // local follower's client). A host may add more IActivityHandler registrations
        // to extend the pipeline.
        services.TryAddSingleton<ILocalActorResolver, DefaultLocalActorResolver>();
        // The activity handlers are an OPEN list: each is a distinct implementation registered under
        // the same service type (IActivityHandler), so AddSingleton (not TryAddSingleton) is required —
        // TryAddSingleton would treat the second and later registrations as duplicates of the first
        // (the same ServiceType) and skip them, leaving only the FollowActivityHandler. A host may add
        // more IActivityHandler registrations to extend the pipeline.
        services.AddSingleton<IActivityHandler, FollowActivityHandler>();
        services.AddSingleton<IActivityHandler, AcceptActivityHandler>();
        services.AddSingleton<IActivityHandler, RejectActivityHandler>();
        services.AddSingleton<IActivityHandler, AnnounceActivityHandler>();
        services.AddSingleton<IActivityHandler, CreateActivityHandler>();
        services.AddSingleton<IActivityHandler, UpdateActivityHandler>();
        services.AddSingleton<IActivityHandler, DeleteActivityHandler>();
        services.AddSingleton<IActivityHandler, UndoActivityHandler>();
        services.AddSingleton<IActivityHandler, LikeActivityHandler>();
        services.AddSingleton<IActivityHandler, BlockActivityHandler>();
        services.AddSingleton<IActivityHandler, FlagActivityHandler>();
        // Mute (24.2): Mute is not an ActivityStreams type (the library has no Mute class), so an inbound
        // "type": "Mute" deserializes to a generic Object. The inbox endpoint wraps it into the
        // Iris-specific MuteActivity, and this exact-type handler records the muter → muted edge (the
        // inverse of the outbox-publish mute arm). It does not contend with any other handler (MuteActivity
        // has a unique type, distance 0).
        services.AddSingleton<IActivityHandler, MuteActivityHandler>();
        // Collection-modification primitives (F-09): a server that manages a community's membership via
        // Add/Remove (rather than a Follow or Offer/Invite/Join/Leave) updates the local community's
        // member set. Each derives from ActivityHandlerBase{T} so the InboxProcessor dispatches by an
        // exact type match (distance 0) — they do not contend with the MembershipActivityHandler
        // (registered for the base Activity type) for the same activity.
        services.AddSingleton<IActivityHandler, AddActivityHandler>();
        services.AddSingleton<IActivityHandler, RemoveActivityHandler>();
        // Intransitive activities (F-17): Read/View/Listen/Travel/Arrive are acknowledgments of
        // receipt — they change no persistent state (no member set, like edge, or block edge to
        // update). The handler accepts them (so they are stored by the InboxProcessor and not
        // rejected) and interprets them as a no-op. Registered for the base Activity type (the five
        // types share no single concrete base an ActivityHandlerBase{T} could be parameterized over)
        // and BEFORE the MembershipActivityHandler (also registered for Activity): the InboxProcessor
        // breaks the base-Activity tie by registration order, so this handler wins the intransitive
        // family. It is registered via a factory (not a direct AddSingleton<IActivityHandler,
        // IntransitiveActivityHandler>) because it needs the MembershipActivityHandler injected: a
        // non-intransitive base-Activity activity (Offer/Invite/Join/Leave) is forwarded to it, so the
        // membership family is not swallowed by this handler's catch-all.
        services.AddSingleton<IActivityHandler>(sp =>
            new IntransitiveActivityHandler(sp.GetRequiredService<MembershipActivityHandler>()));
        // Membership primitives (F-16): a server that manages a community's membership via Offer/Invite/
        // Join/Leave (rather than a Follow or Add/Remove) updates the local community's member set.
        // Registered for the base Activity type (a single ActivityHandlerBase{T} cannot cover the four
        // membership types); the InboxProcessor resolves each activity to the most specific registered
        // handler, so an Add/Remove reaches its exact-type handler and an Offer/Invite/Join/Leave
        // reaches this catch-all (directly, or forwarded by the IntransitiveActivityHandler registered
        // before it). The concrete type is also registered so the IntransitiveActivityHandler factory
        // can resolve the same instance it forwards to (a single MembershipActivityHandler instance is
        // shared by both the IActivityHandler registration and the factory).
        services.AddSingleton<MembershipActivityHandler>();
        services.AddSingleton<IActivityHandler>(sp => sp.GetRequiredService<MembershipActivityHandler>());
        services.AddSingleton<IActivityHandler, CommunityInboxActivityHandler>();
        // Move (F-08): re-points the local follow edges when an actor migrates to a new IRI. It needs the
        // local community IRIs and the outbound caches (to invalidate the moved actor's stale key/doc), so
        // it is registered via a factory that resolves them from the provider (not a direct
        // AddSingleton<IActivityHandler, MoveActivityHandler> — the handler has a non-default ctor).
        services.AddSingleton<IActivityHandler>(sp =>
        {
            var persistence = sp.GetRequiredService<IPersistenceProvider>();
            var localCommunities = persistence.Communities
                .GetAllCommunityIrisAsync()
                .GetAwaiter()
                .GetResult();
            var remoteKeys = sp.GetService<RemoteKeyCache>();
            var remoteActors = sp.GetService<RemoteActorCache>();
            return new MoveActivityHandler(persistence, localCommunities, remoteKeys, remoteActors);
        });
        services.TryAddSingleton<IInboxProcessor, InboxProcessor>();

        // Object Update/Delete propagation (the federated half of F-02/F-03): schedules an object's
        // Update/Delete to the remote actors that hold a copy (the author's remote followers, the
        // remote attributedTo, and the remote parent's owner for a deleted reply) so their copies are
        // refreshed / tombstoned. The UpdateActivityHandler and DeleteActivityHandler depend on it.
        services.TryAddSingleton<IDeletePropagationService, DeletePropagationService>();

        // Community feed (Phase 5): computes a community's unified feed (the union of its local
        // members' outbox activities, newest first) for the /c/{name}/feed endpoint and the client's
        // GetCommunityFeedAsync. A host may replace this to add followed-community content or ranking.
        // The community store is passed in (19.5.4, read via the persistence provider's Communities
        // property) so the feed applies the community's own moderation edges (a blocked/muted member's
        // content is excluded from the feed). The local-actor resolver, actor-document fetcher, and
        // outbound client enable remote-member outbox fetching (a remote member's content appears in
        // the community feed, fetched over the wire and capped by FeedOptions.PagesPerActor). Without
        // a configured instance actor, remote members contribute nothing (local members still work).
        services.TryAddSingleton<ICommunityFeedService>(sp =>
        {
            var persistence = sp.GetRequiredService<IPersistenceProvider>();
            var localActors = sp.GetRequiredService<ILocalActorResolver>();
            var actorDocs = sp.GetRequiredService<IActorDocumentFetcher>();
            var options = sp.GetRequiredService<IOptions<FeedOptions>>().Value;

            // The outbound client for remote outbox fetches: reuse the same client the
            // IActorDocumentFetcher uses (signed as the instance actor). Without a configured
            // instance actor, the fetcher is a NoopActorDocumentFetcher and the client is null
            // (remote members contribute nothing; local members still work).
            IActivityPubClient? client = null;
            var serverOptions = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
            if (serverOptions.InstanceActorId is not null)
            {
                var factory = sp.GetRequiredService<IActivityPubClientFactory>();
                client = factory.Create(
                    new ActivityPubClientOptions { ActorId = serverOptions.InstanceActorId.Value, EnableRetry = false },
                    new HttpClientHandler());
            }

            return new CommunityFeedService(persistence, persistence.Communities, localActors, actorDocs, client, options);
        });

        // Global search (F-13): searches the instance's local actors (the directory) and stored content
        // objects for the /ap/v1/search endpoint and the client's SearchAsync. A host may replace this to
        // add ranking, full-text indexing, or cross-instance (relay/WebFinger) search.
        services.TryAddSingleton<IGlobalSearchService, GlobalSearchService>();

        // Followed feed (F-14): computes an actor's home timeline (the union of the actor's local and
        // remote follows' outbox items, newest first) for the /u/{handle}/feed endpoint and the client's
        // GetFollowFeedAsync. FeedOptions bounds how many outbox pages are walked per remote follow
        // (PagesPerActor) and the total merged item count (MaxItems); a host may rebind FeedOptions to
        // tune both. The service needs the outbound ActivityPub client (to walk a remote follow's
        // outbox over the wire) — the same instance the IRemoteCollectionFetcher uses (a real
        // HttpClientHandler transport, signed as the instance actor).
        services.TryAddSingleton<FeedOptions>(_ => new FeedOptions());
        services.TryAddSingleton<IFollowFeedService>(sp =>
        {
            var factory = sp.GetRequiredService<IActivityPubClientFactory>();
            var options = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;

            // Without a configured instance actor the service cannot sign outbound outbox fetches, so
            // remote follows contribute nothing (local follows still work). A host that sets
            // ActivityPubServerOptions.InstanceActorId gets full remote-outbox walking.
            if (options.InstanceActorId is null)
            {
                throw new InvalidOperationException(
                    "IFollowFeedService requires ActivityPubServerOptions.InstanceActorId to be set (remote outbox fetches must be signed).");
            }

            var clientOptions = new ActivityPubClientOptions
            {
                ActorId = options.InstanceActorId.Value,
                // Outbound outbox fetches do not need retries; keep the pipeline minimal.
                EnableRetry = false,
            };

            return new FeedService(
                sp.GetRequiredService<IPersistenceProvider>(),
                sp.GetRequiredService<ILocalActorResolver>(),
                sp.GetRequiredService<IActorDocumentFetcher>(),
                factory.Create(clientOptions, new HttpClientHandler()),
                sp.GetRequiredService<IOptions<FeedOptions>>(),
                // F-07 (apply the block edge): a follow the actor has blocked is excluded from its feed.
                sp.GetRequiredService<IPersistenceProvider>().Moderation);
        });

        // Outbound delivery (Phase 4): the delivery queue (in-memory Channel<T>), the delivery service
        // (handlers call it to schedule a delivery — it enqueues and returns), and the background
        // DeliveryWorker (pumps jobs off the queue and POSTs them, signed as InstanceActorId).
        // The outbound transport is a Func<HttpMessageHandler> seam: the default is a real
        // HttpClientHandler (goes to the recipient's public URL); a host or test overrides it to route
        // deliveries (e.g. to a TestServer in-process, or to an IHttpClientFactory-backed handler for
        // proxying/timeouts). DeliveryWorker is registered as a hosted service so it starts with the host.
        services.TryAddSingleton<IDeliveryQueue, InMemoryDeliveryQueue>();
        // Phase 17.2: outbound-delivery metrics. A single shared IrisDeliveryMetrics (a Meter + its
        // instruments) is handed to the DeliveryService and DeliveryWorker, which record at the same
        // points they log. No OpenTelemetry dependency — a host that wants to export the metrics adds
        // the OTel SDK and AddMeter(IrisDeliveryMetrics.MeterName) (plus an exporter).
        services.TryAddSingleton<Iris.Server.Observability.IrisDeliveryMetrics>();
        services.TryAddSingleton<IDeliveryService>(sp =>
            new DeliveryService(
                sp.GetRequiredService<IDeliveryQueue>(),
                // F-01: the delivery service resolves a remote recipient's advertised
                // endpoints.sharedInbox from its document. The fetcher is the same registration the
                // inbound signature path uses (reads through the remote-actor cache); when it is a
                // NoopActorDocumentFetcher (no instance actor configured) the delivery service simply
                // falls back to the per-actor inbox.
                sp.GetRequiredService<IActorDocumentFetcher>(),
                // F-07 (apply the block edge): suppress an actor-targeted delivery when the recipient
                // has blocked the signing actor (a blocker does not want content from a blocked actor).
                sp.GetRequiredService<IPersistenceProvider>().Moderation,
                sp.GetRequiredService<ILogger<DeliveryService>>(),
                sp.GetRequiredService<Iris.Server.Observability.IrisDeliveryMetrics>()));
        services.TryAddSingleton<Func<HttpMessageHandler>>(_ => () => new HttpClientHandler());

        // F-22 delivery retry / dead-letter: the retry policy (MaxAttempts=5, BaseDelay=1s, MaxDelay=60s;
        // a host may rebind DeliveryRetryOptions to tune the retry budget) and the dead-letter store
        // (in-memory, bounded; a host may swap in a persistent IDeliveryDeadLetterStore). The worker
        // retries a failed delivery with exponential backoff and dead-letters it when the budget is
        // exhausted, giving at-least-once delivery (a re-delivered activity is deduped by its Id, C-07).
        services.TryAddSingleton<DeliveryRetryOptions>(_ => new DeliveryRetryOptions());
        // Phase 16.1: outbound-delivery concurrency. A host may rebind DeliveryWorkerOptions to deliver a
        // burst in parallel (MaxConcurrentDeliveries > 1); the default is 1 (serial, the pre-Phase-16
        // behavior).
        services.TryAddSingleton<DeliveryWorkerOptions>(_ => new DeliveryWorkerOptions());
        services.TryAddSingleton<IDeliveryDeadLetterStore, InMemoryDeliveryDeadLetterStore>();
        // Phase 16.3: per-peer outbound-delivery rate limit. A host may rebind DeliveryRateLimitOptions
        // (PerPeerMaxRequestsPerMinute > 0) to bound how fast the worker sends to a single peer; the
        // default is 0 (disabled — the worker delivers as fast as the concurrency cap allows).
        services.TryAddSingleton<DeliveryRateLimitOptions>(_ => new DeliveryRateLimitOptions());
        // Phase 17.3: per-peer outbound-delivery circuit breaker. A host may rebind
        // DeliveryCircuitBreakerOptions (FailureThreshold > 0) to stop the worker from hammering a
        // downed peer: once a peer accumulates FailureThreshold consecutive failures, deliveries to
        // that peer are skipped (dead-lettered immediately, no network call) until the peer recovers.
        // The default is 0 (disabled — the worker delivers with per-job retry only).
        services.TryAddSingleton<DeliveryCircuitBreakerOptions>(_ => new DeliveryCircuitBreakerOptions());
        // Phase 17.4: per-peer inbound-delivery rate limit. A host may rebind
        // InboundRateLimitOptions (PerPeerMaxRequestsPerMinute > 0) to bound how many signed inbox
        // POSTs the server accepts from a single peer (keyed by the host of the signer's keyId) per
        // sliding minute; a peer that exceeds the limit receives 429 Too Many Requests (fail-fast).
        // The default is 0 (disabled — the server accepts inbox POSTs as fast as the pipeline allows).
        services.TryAddSingleton<InboundRateLimitOptions>(_ => new InboundRateLimitOptions());
        services.TryAddSingleton<IInboundRateLimiter>(sp =>
            CreateInboundRateLimiter(sp.GetRequiredService<IOptions<InboundRateLimitOptions>>().Value));
        // Phase 17.1: graceful shutdown. Registered BEFORE the DeliveryWorker so its StopAsync (which
        // completes the queue) runs before the worker's BackgroundService.StopAsync (which cancels the
        // worker's stopping token and awaits ExecuteAsync). Completing the queue first lets the worker's
        // dequeue loop observe a complete-and-empty queue and exit cleanly, draining in-flight deliveries,
        // instead of blocking on an open channel.
        services.AddHostedService<DeliveryQueueShutdownService>();
        // The worker is constructed explicitly (not AddHostedService<DeliveryWorker>()) so the F-22 retry
        // policy, dead-letter store, concurrency cap, and rate limiter are injected deterministically (the
        // multiple constructor overloads would otherwise rely on DI's most-constructible overload
        // selection). The rate limiter is a no-op when its options disable it (PerPeerMaxRequestsPerMinute
        // == 0).
        services.AddHostedService(sp => new DeliveryWorker(
            sp.GetRequiredService<IDeliveryQueue>(),
            sp.GetRequiredService<IActivityPubClientFactory>(),
            sp.GetRequiredService<Func<HttpMessageHandler>>(),
            sp.GetRequiredService<IOptions<ActivityPubServerOptions>>(),
            sp.GetRequiredService<ILogger<DeliveryWorker>>(),
            sp.GetRequiredService<IOptions<DeliveryRetryOptions>>().Value,
            sp.GetRequiredService<IDeliveryDeadLetterStore>(),
            sp.GetRequiredService<IOptions<DeliveryWorkerOptions>>().Value.MaxConcurrentDeliveries,
            CreateDeliveryRateLimiter(sp.GetRequiredService<IOptions<DeliveryRateLimitOptions>>().Value),
            sp.GetRequiredService<Iris.Server.Observability.IrisDeliveryMetrics>(),
            CreateDeliveryCircuitBreaker(sp.GetRequiredService<IOptions<DeliveryCircuitBreakerOptions>>().Value)));

        // Phase 17.1: observability. The instance's GET /ap/v1/health endpoint resolves every registered
        // IHealthCheck (IEnumerable<IHealthCheck>) and reports the aggregate status, so a host that wants
        // the standard ASP.NET health-check middleware (UseHealthChecks("/health")) can additionally call
        // AddHealthChecks() — but the Iris endpoint works without it. The checks are registered as
        // singletons (multiple IHealthCheck registrations are allowed), not via AddHealthChecks().AddCheck,
        // so the custom endpoint can resolve them directly. A host may rebind DeliveryQueueHealthOptions
        // (WarningPending/CriticalPending > 0) to alert on a growing delivery backlog; the defaults (0)
        // disable both thresholds.
        services.TryAddSingleton<DeliveryQueueHealthOptions>(_ => new DeliveryQueueHealthOptions());
        services.AddSingleton<IHealthCheck, InstanceHealthCheck>();
        services.AddSingleton<IHealthCheck, DeliveryQueueHealthCheck>();
        // 30.2: persistence reachability (a real read against the actor store — the seam a file/DB-backed
        // IPersistenceProvider backs) and delivery-worker liveness (the worker is registered untyped, so
        // this check locates it among the IHostedServices and reads IsRunning). Both resolve through
        // IEnumerable<IHealthCheck> at the GET /ap/v1/health endpoint.
        services.AddSingleton<IHealthCheck, PersistenceHealthCheck>();
        services.AddSingleton<IHealthCheck, DeliveryWorkerHealthCheck>();

        // 30.2: readiness gate. Ready once the instance actor's signing key is registered + resolvable
        // (a freshly-started instance is not ready until its key material is loaded). The GET /ap/v1/ready
        // probe reports IReadinessGate.IsReadyAsync; a host that loads keys asynchronously may bind its own
        // IReadinessGate (TryAdd — an extra/override registration wins).
        services.TryAddSingleton<IReadinessGate, DefaultReadinessGate>();

        // Proxy fallback (Phase 6): the target policy for the POST /ap/v1/proxy/{target} endpoint —
        // the composition of the target allowlist (which hosts an actor may proxy to) and the per-actor
        // rate limit (how often). Both come from ActivityPubServerOptions.ProxySettings (defaults:
        // empty allowlist = all hosts; DefaultProxyMaxRequestsPerMinute). A host may replace
        // IProxyTargetPolicy with its own (e.g. a distributed rate limiter).
        services.TryAddSingleton<IProxyTargetPolicy>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.ProxySettings;
            return new CompositeProxyTargetPolicy(
                [
                    new AllowlistProxyTargetPolicy(settings?.AllowedHosts),
                    new RateLimitingProxyPolicy(settings?.MaxRequestsPerMinute ?? ActivityPubServerConstants.DefaultProxyMaxRequestsPerMinute),
                ]);
        });

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

        // IPersistenceProvider is a seam — a concrete provider is registered by the persistence package
        // (e.g. Iris.Server.InMemory's AddInMemoryPersistence) or by a host app. AddActivityPubServer does
        // NOT register a concrete provider, keeping Iris.Server free of a dependency on any specific
        // persistence implementation. The TryAddSingleton FACTORY below (resolving the provider from the
        // IServiceProvider) only runs when no concrete registration exists, so it never shadows one — a
        // test/host that later binds AddSingleton<IPersistenceProvider>(a concrete instance) wins, and this
        // factory then returns that same instance. It exists so the 30.2 PersistenceHealthCheck can resolve
        // the provider from DI (it reads IPersistenceProvider.Actors, not a concrete IActorStore).
        services.TryAddSingleton<IPersistenceProvider>(sp => sp.GetRequiredService<IPersistenceProvider>());

        return services;
    }

    /// <summary>
    /// Adds the ActivityPub server services to the service collection, binding <see cref="ActivityPubServerOptions"/>
    /// from the "Iris" configuration section and all delivery/observability options from their conventional
    /// sections.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <param name="configuration">The application configuration. Must not be null.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddActivityPubServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var irisSection = configuration.GetSection("Iris");
        services.AddActivityPubServer(o =>
        {
            if (irisSection["BaseUri"] is { } baseUri) o.BaseUri = new Iri(baseUri);
            if (irisSection["InstanceActorId"] is { } actorId) o.InstanceActorId = new Iri(actorId);
            if (irisSection["SharedInboxIri"] is { } inbox) o.SharedInboxIri = new Iri(inbox);
            if (irisSection["InstanceName"] is { } name) o.InstanceName = name;
            if (irisSection["NamespaceIri"] is { } ns) o.NamespaceIri = new Iri(ns);

            var proxySection = irisSection.GetSection("ProxySettings");
            if (proxySection.Exists())
            {
                var proxy = proxySection.Get<ProxySettings>() ?? new ProxySettings();
                o.ProxySettings = proxy;
            }

            var mediaSection = irisSection.GetSection("Media");
            if (mediaSection.Exists())
            {
                var media = mediaSection.Get<MediaOptions>() ?? new MediaOptions();
                o.Media = media;
            }
        });

        var deliverySection = configuration.GetSection("Iris:Delivery");
        if (deliverySection.Exists())
        {
            services.Configure<DeliveryRetryOptions>(deliverySection.GetSection("Retry"));
            services.Configure<DeliveryWorkerOptions>(deliverySection.GetSection("Worker"));
            services.Configure<DeliveryRateLimitOptions>(deliverySection.GetSection("RateLimit"));
            services.Configure<DeliveryCircuitBreakerOptions>(deliverySection.GetSection("CircuitBreaker"));
        }

        var inboundSection = configuration.GetSection("Iris:Inbound");
        if (inboundSection.Exists())
        {
            services.Configure<InboundRateLimitOptions>(inboundSection.GetSection("RateLimit"));
        }

        var feedSection = configuration.GetSection("Iris:Feed");
        if (feedSection.Exists())
        {
            services.Configure<FeedOptions>(feedSection);
        }

        var healthSection = configuration.GetSection("Iris:Health");
        if (healthSection.Exists())
        {
            services.Configure<DeliveryQueueHealthOptions>(healthSection.GetSection("DeliveryQueue"));
        }

        return services;
    }
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

        // iris: extension namespace document: GET /ns (Phase 31.8). The deployment's iris: extension
        // namespace base is {BaseUri}/ns# (declared as @vocab in every public actor/community document);
        // this endpoint hosts the JSON-LD context at the namespace base ({BaseUri}/ns) so the advertised
        // namespace is resolvable rather than a dangling IRI. Mapped on the ROOT endpoint (NOT the
        // versioned group) because the namespace base is {BaseUri}/ns# — at the host root, not under the
        // /ap/v1 versioned prefix. The # is the JSON-LD fragment separator inside the base and is never
        // sent over HTTP, so the document is served at the literal {BaseUri}/ns. Public and long-cacheable
        // (the vocabulary is immutable per deployment base URI).
        endpoints.MapGet($"/{ActivityPubServerConstants.NamespaceRouteSegment}", NamespaceDocumentHandler)
            .WithName("namespace-document-endpoint");

        // Health: GET /ap/v1/health — the observability endpoint (Phase 17.1). Runs every registered
        // IHealthCheck and reports the aggregate status. 200 when every check is healthy (or degraded),
        // 503 when any check is unhealthy. The body is a JSON object { "status": "...", "checks": { name:
        // { "status": "...", "description": "..." } } }. No authentication: a load balancer / orchestrator
        // health probe must reach it without an ActivityPub signature.
        group.MapGet($"/{ActivityPubServerConstants.HealthRouteSegment}", HealthHandler);

        // Readiness: GET /ap/v1/ready — the readiness probe (Phase 30.2). Reports whether the instance has
        // finished loading its key material and is ready to receive traffic (IReadinessGate). 200
        // { "ready": true } when ready; 503 { "ready": false } otherwise. No authentication: a load
        // balancer / orchestrator readiness probe must reach it without an ActivityPub signature. Distinct
        // from GET /ap/v1/health (liveness): an instance can be up but not yet ready.
        group.MapGet($"/{ActivityPubServerConstants.ReadyRouteSegment}", ReadyHandler);

        // Media serve (Phase 20.4 (a)): GET /ap/v1/media/{id} — serves a stored note attachment (an image
        // or document) by its same-origin media IRI. Public (the browser's <img>/<a> loads it), and
        // long-cacheable (the media is immutable per id; the id is a minted, unguessable GUID). The
        // uploader got this IRI from the upload write (POST /local/v1/u/{handle}/media) and set it as the
        // attachment's url on the note it authored.
        group.MapGet($"/{Iris.Client.MediaConstants.ServeSegment}/{{id}}", MediaServeHandler)
            .WithName("media-serve-endpoint");

        // Media proxy (Phase 20.4 (d)): GET /ap/v1/media/proxy?url={originator-url} — a same-origin,
        // long-cacheable GET that makes an external attachment loadable in the browser. The client's
        // render boundary rewrites every cross-origin attachment url to this IRI; the server fetches
        // the remote url once (IMediaFetcher), stores it (IMediaStore.PutBySourceUrlAsync, keyed by the
        // URL + a server-internal content-hash dedupe), and serves the bytes from the same origin. On a
        // fetch failure it returns 502 (the client's <img onerror> falls back to a link-out to the raw
        // URL). Public (the browser's <img> loads it), like the media-serve read.
        group.MapGet($"/{Iris.Client.MediaConstants.ServeSegment}/{Iris.Client.MediaConstants.ProxySegment}", MediaProxyHandler)
            .WithName("media-proxy-endpoint");

        // Inbox: POST /ap/v1/u/{handle}/inbox — receives federation activities (Follow, Accept,
        // Create, ...) that a REMOTE peer delivers TO this actor. Requires a valid HTTP signature
        // (validated by SignatureValidationMiddleware); unsigned or invalidly-signed requests are
        // rejected with 401.
        group.MapPost("/u/{handle}/inbox", InboxHandler);

        // Outbox publish: POST /ap/v1/u/{handle}/outbox — the WRITE SURFACE for the activities the local
        // actor AUTHORS (a Follow, a Create/note, a Like, a Block, a Flag, an Undo, ...). Per the delivery
        // model, a client never addresses a recipient's inbox for an activity it authors; it publishes the
        // activity to the acting actor's own outbox. The server records the activity in that actor's outbox
        // (so the actor's feed / outbox collection surfaces it) + the activity store, and is the only thing
        // that delivers the activity to a recipient's inbox (the server resolves the recipient — the
        // activity's object for a Follow/Block/Flag, the author's remote followers for a Create, the
        // object's owner for a Like — and server-delivers it, signed as the acting local actor). Requires a
        // valid signature from the acting local actor.
        group.MapPost("/u/{handle}/outbox", OutboxPublishHandler);

        // Inbox: GET /ap/v1/u/{handle}/inbox — the activities DELIVERED TO the actor (what they received),
        // as opposed to the outbox (what they authored). Decision 056: the inbox is a first-class,
        // per-actor collection and, unlike the public collections, it is PRIVATE — it is served only to
        // the owner (Basic auth via IActorCredentialValidator, the same seam that gates the owner-only
        // privateKey extension) and is never cached (no-store). An unauthenticated / non-owner request
        // gets 403; an unknown actor gets 404. Paged via ?page=N / ?limit=N.
        group.MapGet("/u/{handle}/inbox", InboxEndpointHandler)
            .WithName("inbox-endpoint");

        // Paged collections: GET /ap/v1/u/{handle}/{collection} where {collection} is one of outbox
        // (the actor's posted activities, newest first), followers (actors following the local actor),
        // following (actors the local actor follows), liked (objects the local actor has liked, F-04),
        // blocks (actors the local actor has blocked, F-07 moderation), flags (actors the local actor
        // has flagged, F-07 moderation), or mutes (actors the local actor has muted, F-07 moderation).
        // Each serves an
        // OrderedCollection (page 1, with `first`) or an OrderedCollectionPage (page N>1), paged via
        // ?page=N and ?limit=N, and served through the local collection-page response cache. The
        // {collection} route value is bound as `collectionName` (it is not a query parameter).
        group.MapGet(
                "/u/{handle}/{collection:regex(outbox|followers|following|liked|blocks|flags|mutes|relays)}",
                (string handle, string collection, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CollectionEndpointHandler(handle, collection, context, persistence, optionsAccessor, collectionCache, ct))
            .WithName("collection-endpoint");

        // Followed feed: GET /ap/v1/u/{handle}/feed — the actor's home timeline (F-14): the union of the
        // actor's local and remote follows' outbox items, newest first, de-duplicated, capped by
        // FeedOptions. Served as a paged collection (page 1 is an OrderedCollection with `first`; page
        // N>1 an OrderedCollectionPage), paged via ?page/?limit. Unlike the local outbox/followers/
        // following/liked collections, this is NOT served through the LocalCollectionPageCache: the feed
        // merges remote follows' outboxes over the wire on every request, so caching the rendered page
        // would hide new remote content (a remote follow posting is not reflected until the cache TTL
        // lapses). The response still carries the collection Cache-Control so intermediates may cache
        // briefly.
        group.MapGet(
                "/u/{handle}/feed",
                (string handle, HttpContext context,
                    IPersistenceProvider persistence, IFollowFeedService feedService,
                    IOptions<ActivityPubServerOptions> optionsAccessor, CancellationToken ct)
                    => FollowFeedHandler(handle, context, persistence, feedService, optionsAccessor, ct))
            .WithName("follow-feed-endpoint");

        // Community document: GET /ap/v1/c/{name} — the community (the library's Group actor) document.
        // A community is addressed by its handle (not an actor IRI), so the route uses {name}.
        group.MapGet("/c/{name}", CommunityDocumentHandler);

        // Community members: GET /ap/v1/c/{name}/members — the community's member actor IRIs, served as
        // a paged collection (page 1 is an OrderedCollection with `first`; page N>1 an OrderedCollectionPage),
        // through the local collection-page response cache (so ?refresh=true bypasses it).
        group.MapGet(
                "/c/{name}/members",
                (string name, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CommunityMembersHandler(name, context, persistence, optionsAccessor, collectionCache, ct));

        // Community feed: GET /ap/v1/c/{name}/feed — the community's unified feed (the union of its
        // local members' outbox activities, newest first), served as a paged collection through the
        // local collection-page response cache (so ?refresh=true bypasses it and emits a no-cache
        // Cache-Control — the 19.5.5 cache-bypass for the community feed).
        group.MapGet(
                "/c/{name}/feed",
                (string name, HttpContext context,
                    IPersistenceProvider persistence, ICommunityFeedService feedService,
                    IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CommunityFeedHandler(name, context, persistence, feedService, optionsAccessor, collectionCache, ct));

        // Community outbox: GET /ap/v1/c/{name}/outbox — the activities the local community (a Group
        // actor) AUTHORS and publishes to its own outbox (currently a Follow and the Undo of a Follow —
        // the only activity kinds the community outbox publish endpoint accepts). This is the READ
        // counterpart of POST /ap/v1/c/{name}/outbox (CommunityOutboxPublishHandler), which stores each
        // published activity in the community's outbox (Activities.GetOutboxAsync, keyed by the community
        // IRI) and the activity store. The community document advertises this outbox IRI, so serving it
        // keeps the document honest (a remote client resolving the community's outbox link finds the
        // community's authored activities). Mirrors the actor outbox collection endpoint
        // (GET /u/{handle}/outbox) for a Group: served as a paged collection (page 1 is an
        // OrderedCollection with `first`; page N>1 an OrderedCollectionPage), paged via ?page/?limit,
        // and served through the local collection-page response cache (so ?refresh=true bypasses it).
        // An unknown community 404s.
        group.MapGet(
                "/c/{name}/outbox",
                (string name, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CommunityOutboxHandler(name, context, persistence, optionsAccessor, collectionCache, ct))
            .WithName("community-outbox-endpoint");

        // Community search: GET /ap/v1/c/{name}/search — a specialized collection that searches the
        // community's content (the feed surface) case-insensitively via ?q, paged via ?limit/?offset
        // (the shared limit/offset pagination shape, Resolved Decision #6).
        group.MapGet("/c/{name}/search", CommunitySearchHandler);

        // Community collections: GET /ap/v1/c/{name}/{collection} where {collection} is one of following
        // (the actors/communities the community follows) or followers (the actors/communities that follow
        // the community). Mirrors the actor collection endpoint (/u/{handle}/{collection}) for a Group:
        // a community is followed (and follows) the same way a person is, so it carries the same
        // following/followers collections. `following` is backed by the community's follows set
        // (ICommunityStore.GetFollowsAsync — the community follows the follower, Resolved Decision #36).
        // `followers` is backed by the community's followers set (ICommunityStore.GetFollowersAsync —
        // F-24: the FollowActivityHandler records a follower in this set when an actor follows a local
        // community, so the collection lists the actors/communities that follow it). Paged via
        // ?page/?limit (the shared page/limit shape).
        group.MapGet(
                "/c/{name}/{collection:regex(following|followers)}",
                (string name, string collection, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CommunityCollectionHandler(name, collection, context, persistence, optionsAccessor, collectionCache, ct))
            .WithName("community-collection-endpoint");

        // Community moderation collections (19.5.4): GET /ap/v1/c/{name}/{blocks|flags|mutes} — the
        // actors the community has blocked/flagged/muted, served as a paged collection (mirrors the
        // person moderation collections GET /u/{handle}/{blocks|flags|mutes} for a Group). A community
        // moderates the actors whose content it surfaces in its unified feed: the edges live in the
        // community's own moderation sets (ICommunityStore's blocks/flags/mutes, scoped to the
        // community), not the person IModerationStore. Paged via ?page/?limit (the shared page/limit
        // shape); an unknown community 404s.
        group.MapGet(
                "/c/{name}/{collection:regex(blocks|flags|mutes)}",
                (string name, string collection, HttpContext context,
                    IPersistenceProvider persistence, IOptions<ActivityPubServerOptions> optionsAccessor,
                    LocalCollectionPageCache collectionCache, CancellationToken ct)
                    => CommunityModerationCollectionHandler(name, collection, context, persistence, optionsAccessor, collectionCache, ct))
            .WithName("community-moderation-collection-endpoint");

        // NOTE (19.0b.2b AP-native rework): the community mute WRITE no longer has a /ap/v1/c/{name}/mutes
        // route. A mute is Iris-specific (no ActivityStreams type) and a local moderation decision, so it
        // is not part of the AP route tree: it is a Basic-authenticated POST on the dedicated local tree
        // (POST /local/v1/c/{name}/mutes/{target}, CommunityMuteHandler, mapped in the local-moderation
        // group below). The community mute READ (GET /c/{name}/mutes, an OrderedCollection) stays on the
        // AP tree — it is an ordinary collection read. (A community block/flag is not a local POST: those
        // are the federated Block/Flag activities, recorded on the community when either party is local.)

        // Community inbox: POST /ap/v1/c/{name}/inbox — receives federation activities addressed to the
        // community (e.g. a Follow from a remote actor, or a Create/Announce from a followed community).
        // Requires a valid HTTP signature (validated by SignatureValidationMiddleware); unsigned or
        // invalidly-signed requests are rejected with 401.
        group.MapPost("/c/{name}/inbox", CommunityInboxHandler);

        // Community outbox publish: POST /ap/v1/c/{name}/outbox — the WRITE SURFACE for the activities the
        // local community (a Group actor) AUTHORS: a Follow (the community follows a remote actor/community,
        // gap G-3) or an Undo of such a Follow (an un-follow). Mirrors the actor outbox publish endpoint
        // (POST /u/{handle}/outbox) for a Group: the client publishes the community-authored activity to the
        // community's own outbox, the server records the activity in the community's outbox (so the
        // community's `following` collection surfaces the edge) + the activity store, records the community's
        // follows set edge (the inverse of the inbound FollowActivityHandler's community branch), and is the
        // only thing that delivers the activity to the target's inbox (the server-delivery hop, signed as the
        // community — the community is a Group actor and signs just like a Person). Only Follow and Undo are
        // accepted (a community does not post content through its own outbox — that flows through the
        // members' outboxes / the community inbox). Requires a valid HTTP signature from the community.
        group.MapPost("/c/{name}/outbox", CommunityOutboxPublishHandler);

        // Global search: GET /ap/v1/search — instance-wide search / directory (F-13): searches the
        // instance's local actors (the directory) and stored content objects case-insensitively via ?q
        // (an empty query lists everything), paged via ?limit/?offset (the shared limit/offset
        // pagination shape, Resolved Decision #6). Actors come first, then content objects, each sorted
        // by IRI (deterministic). Like the community search, this is computed fresh per request (not
        // served through the local collection-page cache).
        group.MapGet("/search", GlobalSearchHandler);

        // Object document: GET /ap/v1/{**path} — serves a content object by its IRI (F-02/F-03/F-10).
        // {**path} is the object IRI's path relative to the route prefix (e.g. the Note at
        // https://a.test/ap/v1/u/alice/notes/1 is GET /ap/v1/u/alice/notes/1). The absolute IRI is
        // reconstructed from the base URL + the catch-all path. A stored object is served as itself; a
        // deleted object is served as its AS2.0 Tombstone ({"type":"Tombstone",…}); an unknown IRI 404s.
        // The catch-all is the LAST route segment (ASP0017 forbids a segment after {**path}), so the
        // object IRI IS the endpoint IRI (no /o/ prefix) — a client fetching GET {objectIri} reaches
        // this route. More specific routes (/u/{handle}, /u/{handle}/{collection}, /c/{name}, …) match
        // first by routing priority, so the catch-all only serves content objects.
        //
        // Object replies (F-12): when the catch-all path ends in a /replies segment (e.g.
        // /u/alice/notes/n1/replies), the route instead serves the parent object's replies — the
        // objects that set their inReplyTo to the parent's IRI (the parent IRI is the catch-all path
        // minus the trailing /replies). Served as a paged collection (items are the reply IRIs as
        // links), paged via ?page/?limit. A catch-all cannot be followed by another segment, so the
        // replies surface is handled inside ObjectDocumentHandler by stripping the trailing /replies.
        group.MapGet("/{**path}", ObjectDocumentHandler).WithName("object-document-endpoint");

        // Proxy fallback: POST /ap/v1/proxy/{target} — an authenticated actor's browser cannot reach a
        // cross-origin remote instance (CORS / no signed outbound from the browser), so it posts the
        // request to its own instance's proxy, which signs it with the actor's key (the same per-actor
        // signing the delivery worker uses) and forwards it to the target, returning the remote
        // response. Basic auth identifies the actor (IActorCredentialValidator); the target must pass
        // the IProxyTargetPolicy (allowlist + rate limit). {target} is a catch-all of the absolute
        // target IRI (slash-containing); the path is reconstructed with Uri.EscapeDataString so the
        // signature's (request-target) component matches the forwarded request.
        group.MapPost("/proxy/{**target}", ProxyHandler).WithName("proxy-endpoint");

        // NOTE (19.0b.2b AP-native rework): the person mute + relay WRITE routes no longer live on the
        // /ap/v1 tree. A mute (F-07) and a relay subscription (F-06) are Iris-specific local moderation
        // decisions (no ActivityStreams type), so they are not part of the AP route tree: they are
        // Basic-authenticated POSTs on the dedicated local tree, mapped in the local-moderation group
        // below (POST /local/v1/u/{handle}/mutes/{target}, LocalMuteHandler; POST
        // /local/v1/u/{handle}/relays/{target}, LocalRelayHandler). The mute/relay READS
        // (GET /u/{handle}/mutes, GET /u/{handle}/relays) stay on the AP tree — ordinary collection reads.

        // NOTE (Phase 19.0b AP-native rework): the operator's follow Accept/Reject no longer has a
        // dedicated /follows/{**followId} endpoint. It is an ordinary ActivityStreams activity that the
        // client authors and publishes to the followed actor's own outbox (see OutboxPublishHandler's
        // Accept/Reject branches); the outbox records it and server-delivers it to the follower. The
        // legacy Basic-auth follow-decision endpoints were removed — the outbox is the sole write path.

        // OAuth2 token exchange: POST /ap/v1/oauth2/token — exchanges an authorization code for a
        // Bearer token. The client sends grant_type=authorization_code + code; the server redeems the
        // code (one-time), issues a random Bearer token, stores it in the IOAuthTokenStore, and returns
        // { access_token, token_type: "bearer" }. Phase 15.2a (the CI-testable core of the OAuth2 flow).
        group.MapPost("/oauth2/token", OAuthTokenHandler).WithName("oauth-token-endpoint");

        // OAuth2 token revocation: POST /ap/v1/oauth2/revoke — revokes a Bearer token. The client sends
        // token; the server removes it from the IOAuthTokenStore and returns 200 (RFC 7009: always 200,
        // even for unknown tokens, to avoid leaking token validity).
        group.MapPost("/oauth2/revoke", OAuthRevokeHandler).WithName("oauth-revoke-endpoint");

        // OAuth2 authorization: GET /ap/v1/oauth2/authorize — the browser-redirect half of the
        // authorization-code flow (RFC 6749 §4.1). The browser is redirected here by the client app
        // with ?client_id (the actor handle), ?redirect_uri, and ?state (opaque, echoed back). The
        // handler auto-approves (the v1 model — no interactive consent screen), issues a one-time
        // authorization code, and 302-redirects to redirect_uri?code=...&state=....
        group.MapGet("/oauth2/authorize", OAuthAuthorizeHandler).WithName("oauth-authorize-endpoint");

        // Local moderation (19.0b.2b AP-native rework): the mute (F-07) and relay-subscription (F-06)
        // WRITE routes live on a dedicated, non-AP tree ({LocalRoutePrefix}), NOT the /ap/v1 AP tree. A
        // mute and a relay subscription are Iris-specific local decisions (no ActivityStreams type), so
        // they are not AP activities and are not part of the AP protocol surface: each is a
        // Basic-authenticated POST to the acting actor's (or community's) own instance, which
        // authenticates the requester by Basic auth (IActorCredentialValidator) and records/removes the
        // edge. A removal is signalled by ?unmute=true / ?unsubscribe=true. The corresponding READS
        // (GET /ap/v1/u/{handle}/mutes, /relays, /c/{name}/mutes) remain on the AP tree — they are
        // ordinary ActivityStreams collection reads. This group is separate from the /ap/v1 group so it
        // does not carry the Iris AP version header (it is not an AP endpoint).
        var localGroup = endpoints.MapGroup(Iris.Client.LocalModerationConstants.LocalRoutePrefix);

        // Local mute (person): POST /local/v1/u/{handle}/mutes/{target} — a local actor records a mute
        // (F-07); the same route with ?unmute=true removes it. {target} is a catch-all of the absolute
        // IRI of the actor being muted.
        localGroup.MapPost("/u/{handle}/mutes/{**target}", LocalMuteHandler).WithName("local-mute-endpoint");

        // Local relay subscription (person): POST /local/v1/u/{handle}/relays/{target} — a local actor
        // subscribes to a relay (F-06); the same route with ?unsubscribe=true removes it. {target} is a
        // catch-all of the absolute IRI of the relay being subscribed to.
        localGroup.MapPost("/u/{handle}/relays/{**target}", LocalRelayHandler).WithName("local-relay-endpoint");

        // Local mute (community): POST /local/v1/c/{name}/mutes/{target} — a community's operator records
        // a community-scoped mute (the community hides a member's content from its unified feed without
        // severing the membership); the same route with ?unmute=true removes it. The community's IRI is
        // the credential seam (IActorCredentialValidator). {target} is a catch-all of the absolute IRI
        // of the actor being muted.
        localGroup.MapPost("/c/{name}/mutes/{**target}", CommunityMuteHandler).WithName("community-mute-endpoint");

        // Media upload (Phase 20.4 (a)): POST /local/v1/u/{handle}/media — an owner-only,
        // Basic-authenticated multipart POST of a note's attachment (an image or document). The server
        // stores the bytes and returns (201) the same-origin media IRI the uploader sets as the
        // attachment's url. Not an ActivityStreams activity (a local, non-federated write), so it is on
        // the non-AP /local/v1 tree, not /ap/v1.
        localGroup
            .MapPost($"/u/{{handle}}/{Iris.Client.MediaConstants.UploadSegment}", LocalMediaUploadHandler)
            .WithName("local-media-upload-endpoint");

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
        var bypassCache = HasRefreshBypass(context);
        var (rendered, _, _) = await actorDocumentCache
            .GetAsync(
                actorIri,
                bypassCache,
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
        var cacheControl = bypassCache
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.ActorCacheControl;
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = cacheControl;
        return Results.Text(rendered, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// The proxy-fallback endpoint (<c>POST /ap/v1/proxy/{target}</c>, Phase 6). An authenticated
    /// actor's browser cannot reach a cross-origin remote instance directly (CORS, and the browser
    /// cannot produce an ActivityPub HTTP signature), so it posts the request it wants to make to its
    /// own instance's proxy. The endpoint: (1) identifies the actor from the request's Basic auth
    /// (<see cref="IActorCredentialValidator"/>), (2) checks the target against the
    /// <see cref="IProxyTargetPolicy"/> (allowlist + rate limit), and (3) signs and forwards the
    /// request to the target with the actor's own key (the per-actor <c>X-Iris-Actor</c> override,
    /// Resolved Decision #29), relaying the remote response's status and body back.
    /// <para>
    /// The proxied request is a <c>GET</c> to the target: the proxy signs it with the actor's key
    /// (the remote instance validates the signature by the actor's document) and copies the client's
    /// <c>Accept</c> header. The client's <c>Authorization</c> is <em>not</em> forwarded — the remote
    /// authenticates by the HTTP signature, not Basic auth. The target is the <c>{target}</c> catch-all
    /// route value (an absolute IRI); the path is passed through as-is so the forwarded request's
    /// <c>(request-target)</c> component (the escaped path the <see cref="Iris.Client.Pipeline.SigningHandler"/>
    /// signs) is exactly the target's path.
    /// </para>
    /// </summary>
    private static async Task<IResult> ProxyHandler(
        HttpContext context,
        IActorCredentialValidator credentialValidator,
        IProxyTargetPolicy proxyPolicy,
        IActivityPubClientFactory clientFactory,
        Func<HttpMessageHandler> transportFactory,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        // Buffer the request body so it is re-readable for the relay below (the SignatureValidation
        // middleware or another component may have already consumed the stream). EnableBuffering makes
        // the stream seekable and re-readable from position 0.
        context.Request.EnableBuffering();

        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";

        // 1. Identify the actor from Basic auth. The validator returns the authenticated handle (the
        // local username); the actor IRI is {base}/ap/v1/u/{handle}.
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(BuildActorIri(baseUrl, "proxy"), authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.Unauthorized();
        }

        var actorIri = BuildActorIri(baseUrl, authenticatedHandle);

        // 2. Resolve the target IRI from the catch-all route value ({target} = the absolute target IRI).
        // The catch-all route parameter is named "target" (the route template is /proxy/{**target}),
        // so the route value key is "target", not the segment name "proxy".
        const string targetRouteKey = "target";
        if (context.Request.RouteValues[targetRouteKey] is not string targetValue
            || string.IsNullOrWhiteSpace(targetValue))
        {
            return Results.NotFound();
        }

        // The catch-all route value {**target} captures only the PATH of the target IRI — the route
        // matches the path, and the target's query string (e.g. ?page=2 for a paginated collection) is
        // carried in the proxy request's OWN query string (context.Request.QueryString), which the route
        // value excludes. Reconstruct the full target IRI by appending it, or the proxy would always
        // relay page 1 and a paginated read would loop on the same next link forever (the client's
        // GetCollectionAsync walks next indefinitely). The fragment is never sent by a client (browsers
        // strip it), so only the query string needs re-attaching.
        var fullTarget = context.Request.QueryString.HasValue
            ? targetValue + context.Request.QueryString.Value
            : targetValue;

        if (!Iri.TryParse(fullTarget, out var target))
        {
            return Results.BadRequest();
        }

        // 3. Check the target against the policy (allowlist + rate limit). A rate-limit rejection is
        // 429; an allowlist rejection is 403.
        if (!await proxyPolicy.TryAuthorizeAsync(actorIri, target, out var reason, ct).ConfigureAwait(false))
        {
            var status = reason is not null && reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? (HttpStatusCode)429
                : HttpStatusCode.Forbidden;
            return Results.Json(new { error = reason }, statusCode: (int)status);
        }

        // 4. Build the forwarded request. The proxy transport is always a POST to
        // /ap/v1/proxy/{target} (the target IRI rides in the path), so the client signals the REAL
        // method of the request it wants made via the X-Iris-Proxy-Method header (defaulting to GET
        // for legacy bodyless reads). The proxy relays that method, the body (the activity for a
        // Create), and the Accept header (ActivityPub content negotiation), and signs as the
        // authenticated actor (the X-Iris-Actor override — the SigningHandler resolves the actor's
        // key from the IKeyProvider). The client's Authorization is deliberately not copied (the
        // remote authenticates by signature). Relaying the method + body is what makes a proxied
        // write (a browser Create POST to an outbox) actually create — without it the bodyless
        // forward is a no-op GET-equivalent that only lists the outbox.
        var method = HttpMethod.Parse(
            context.Request.Headers["X-Iris-Proxy-Method"].FirstOrDefault() ?? "GET");
        using var request = new HttpRequestMessage(method, target.Value);
        if (context.Request.Headers.Accept is { Count: > 0 } accept)
        {
            foreach (var value in accept)
            {
                request.Headers.TryAddWithoutValidation("Accept", value);
            }
        }

        // Relay the request body (the ActivityPub activity for a Create) for a write (POST/PUT).
        // The body was buffered at the top of the handler (EnableBuffering); reset the stream to
        // position 0 and read it into a buffer for the relay. The content type defaults to the
        // ActivityPub JSON-LD media type (the client always sends it).
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            context.Request.Body.Position = 0;
            using var bodyReader = new MemoryStream();
            await context.Request.Body.CopyToAsync(bodyReader, ct).ConfigureAwait(false);
            var bodyBytes = bodyReader.ToArray();
            request.Content = new ByteArrayContent(bodyBytes);
            // TryAddWithoutValidation (not the ContentType setter): the inbound content type may carry
            // a charset parameter (e.g. "application/activity+json; charset=utf-8"), which the
            // MediaTypeHeaderValue constructor rejects. The relayed content type is opaque to the
            // target (it re-serializes the activity), so validation is unnecessary.
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type", context.Request.ContentType ?? ActivityJson.ActivityJsonContentType);
        }

        request.Headers.TryAddWithoutValidation("X-Iris-Actor", actorIri.Value);

        // 5. Sign + forward, relaying the remote response (status + body + content type). The transport
        // is the Func<HttpMessageHandler> seam (default: a real HttpClientHandler; a test routes it to a
        // TestServer in-process).
        using var client = clientFactory.Create(
            new ActivityPubClientOptions
            {
                ActorId = actorIri,
                EnableRetry = false,
            },
            transportFactory());

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Relay the remote response's status and body (content type defaults to ActivityPub JSON-LD).
        var statusCode = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? ActivityJson.ActivityJsonContentType;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = mediaType;
        return Results.Content(body, mediaType);
    }

    /// <summary>
    /// Records (or removes) a local mute (F-07). The requesting actor is identified by Basic auth
    /// (IActorCredentialValidator); the muted actor is the {target} catch-all route value (an absolute
    /// IRI); <c>?unmute=true</c> removes the mute instead of recording it.
    /// </summary>
    /// <remarks>
    /// A mute is Iris-specific (there is no ActivityStreams <c>Mute</c> type) and is a local moderation
    /// decision: a local actor hides a follow's content from its feed without severing the follow. It is
    /// therefore recorded from an authenticated local request — it is not interpreted from a federated
    /// activity (which the inbox endpoint would reject, the ActivityStreams library deserializing an
    /// unknown <c>type</c> to a generic <c>Object</c> rather than an <c>Activity</c>). The handler:
    /// (1) authenticates the actor, (2) resolves the target IRI, and (3) records or removes the mute
    /// edge in the moderation store (an un-mute is signalled by <c>?unmute=true</c>). The response is
    /// <c>204</c> on success.
    /// </remarks>
    private static async Task<IResult> LocalMuteHandler(
        HttpContext context,
        string handle,
        IActorCredentialValidator credentialValidator,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // 1. Authenticate the requesting actor (Basic auth) for this actor's IRI.
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.Unauthorized();
        }

        // 2. Resolve the target IRI from the catch-all route value ({target} = the absolute target IRI).
        const string targetRouteKey = "target";
        if (context.Request.RouteValues[targetRouteKey] is not string targetValue
            || string.IsNullOrWhiteSpace(targetValue))
        {
            return Results.NotFound();
        }

        if (!Iri.TryParse(targetValue, out var target))
        {
            return Results.BadRequest();
        }

        // 3. Record or remove the mute edge (?unmute=true removes). The mute is idempotent (re-muting is
        // a no-op); an un-mute of a non-existent mute is also a no-op (both return 204 — the mute's
        // steady state is authoritative).
        var remove = context.Request.Query.TryGetValue("unmute", out var unmuteValues)
            && unmuteValues.Count > 0
            && string.Equals(unmuteValues[0], "true", StringComparison.OrdinalIgnoreCase);
        if (remove)
        {
            await persistence.Moderation.RemoveMuteAsync(actorIri, target, ct).ConfigureAwait(false);
        }
        else
        {
            await persistence.Moderation.RecordMuteAsync(actorIri, target, ct).ConfigureAwait(false);
        }

        // 19.6.2 moderation-collection enumeration correctness: the actor's mutes collection is served
        // through the local collection-page response cache (a 60s TTL). A mute or un-mute that is not
        // paired with an invalidation leaves the cached page-1 stale: the owner's card would not reflect
        // the mute it just recorded (or removed) until the TTL lapses or a ?refresh=true bypass is
        // issued. Drop the mutes page-1 entry so the next non-?refresh read re-renders.
        InvalidateLocalCollectionPage(collectionCache, actorIri, "mutes");

        return Results.NoContent();
    }

    /// <summary>
    /// Records (or removes) a local relay subscription (F-06). The requesting actor is identified by
    /// Basic auth (IActorCredentialValidator); the relay is the {target} catch-all route value (an
    /// absolute IRI); <c>?unsubscribe=true</c> removes the subscription instead of recording it.
    /// </summary>
    /// <remarks>
    /// A relay subscription is an Iris-specific local decision: a local actor configures the relays
    /// (fan-out servers, ActivityPub §5.1.3) it wants its content fanned out through. It is therefore
    /// recorded from an authenticated local request — it is not interpreted from a federated activity
    /// (a relay is a remote server the actor points at, not an activity the actor receives). The handler:
    /// (1) authenticates the actor, (2) resolves the relay IRI, and (3) records or removes the relay
    /// edge in the relay store (an un-subscribe is signalled by <c>?unsubscribe=true</c>). The response
    /// is <c>204</c> on success.
    /// </remarks>
    private static async Task<IResult> LocalRelayHandler(
        HttpContext context,
        string handle,
        IActorCredentialValidator credentialValidator,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // 1. Authenticate the requesting actor (Basic auth) for this actor's IRI.
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.Unauthorized();
        }

        // 2. Resolve the relay IRI from the catch-all route value ({target} = the absolute relay IRI).
        const string targetRouteKey = "target";
        if (context.Request.RouteValues[targetRouteKey] is not string targetValue
            || string.IsNullOrWhiteSpace(targetValue))
        {
            return Results.NotFound();
        }

        if (!Iri.TryParse(targetValue, out var relay))
        {
            return Results.BadRequest();
        }

        // 3. Record or remove the relay edge (?unsubscribe=true removes). A subscription is idempotent
        // (re-subscribing is a no-op); an un-subscribe of a non-existent subscription is also a no-op
        // (both return 204 — the subscription's steady state is authoritative).
        var remove = context.Request.Query.TryGetValue("unsubscribe", out var unsubscribeValues)
            && unsubscribeValues.Count > 0
            && string.Equals(unsubscribeValues[0], "true", StringComparison.OrdinalIgnoreCase);
        if (remove)
        {
            await persistence.Relays.RemoveRelayAsync(actorIri, relay, ct).ConfigureAwait(false);
        }
        else
        {
            await persistence.Relays.RecordRelayAsync(actorIri, relay, ct).ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Handles a media upload (Phase 20.4 (a)): <c>POST /local/v1/u/{handle}/media</c>. The requesting
    /// actor (identified by Basic auth via <see cref="IActorCredentialValidator"/>) POSTs a note's
    /// attachment (an image or document) as a multipart file. The server stores the bytes in the media
    /// store and returns <c>201 Created</c> with a JSON body carrying the same-origin media IRI (which
    /// the uploader sets as the attachment's <c>url</c>), the content-type, and the file name.
    /// </summary>
    /// <remarks>
    /// A media upload is not an ActivityStreams activity (it is a local, non-federated write), so it is
    /// on the non-AP <c>/local/v1</c> tree and is Basic-authenticated (not signed). An unknown actor is
    /// <c>404</c>; an unauthenticated / non-owner request is <c>401</c>; a missing or non-multipart body
    /// is <c>400</c>; an oversized upload is <c>413</c>.
    /// </remarks>
    private static async Task<IResult> LocalMediaUploadHandler(
        HttpContext context,
        string handle,
        IActorCredentialValidator credentialValidator,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // 1. The actor must exist (404 when unknown) and the requester must be the owner (Basic auth).
        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.Unauthorized();
        }

        // 2. Read the uploaded file from the multipart form. The file is the single required part (named
        // "file"); its content-type + file name are carried by the part.
        IFormFile? file;
        try
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            file = form.Files.Count > 0 ? form.Files[0] : null;
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest();
        }

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest();
        }

        // 3. Enforce a size cap (a media attachment must not be unbounded).
        if (file.Length > MaxMediaUploadBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // 4. Store the bytes + metadata; the store mints the same-origin media IRI.
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? string.Empty : file.FileName;
        var mediaIri = await persistence.Media.PutAsync(bytes, contentType, fileName, new Iri(baseUrl), ct).ConfigureAwait(false);

        // 5. Return 201 with the media IRI + content-type + file name (the uploader sets the IRI as the
        // attachment's url and the content-type/file name as the Image's mediaType/name).
        return Results.Created(
            mediaIri.Value,
            new { id = mediaIri.Value, type = contentType, name = fileName });
    }

    /// <summary>
    /// Serves a stored note attachment (Phase 20.4 (a)): <c>GET /ap/v1/media/{id}</c>. Public (the
    /// browser's <c>&lt;img&gt;</c>/<c>&lt;a&gt;</c> loads it) and long-cacheable (the media is immutable
    /// per id; the id is a minted, unguessable GUID). Returns the stored bytes with the recorded
    /// <c>Content-Type</c> and a long <c>Cache-Control</c>; <c>404</c> when the media is unknown.
    /// </summary>
    private static async Task<IResult> MediaServeHandler(
        string id,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var mediaIri = new Iri($"{baseUrl.TrimEnd('/')}/ap/v1/media/{id}");

        if (!await persistence.Media.TryGetAsync(
                mediaIri, out var bytes, out var contentType, out var fileName, ct)
            .ConfigureAwait(false)
            || bytes is null
            || contentType is null)
        {
            return Results.NotFound();
        }

        // The media is immutable per id (a minted, unguessable GUID); cache it aggressively (the browser
        // should not re-fetch a stable attachment). A long max-age with no revalidation. The content-type
        // is the recorded media type (the <img>/<a> renders/downloads it accordingly).
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = "max-age=31536000, immutable";
        return Results.File(bytes, contentType);
    }

    /// <summary>
    /// Serves an external attachment from the same origin (Phase 20.4 (d)):
    /// <c>GET /ap/v1/media/proxy?url={originator-url}</c>. Public (the browser's <c>&lt;img&gt;</c>
    /// loads it) and long-cacheable. Fetches the remote <c>url</c> once (via <see cref="IMediaFetcher"/>),
    /// stores it (via <see cref="Stores.IMediaStore.PutBySourceUrlAsync"/>, keyed by the URL + a
    /// server-internal content-hash dedupe), and serves the bytes with the remote
    /// <c>Content-Type</c>. A cache hit (the URL was already stored) serves straight from the store with
    /// no outbound fetch. On a fetch failure (a dead or unreachable URL) it returns
    /// <c>502 Bad Gateway</c> so the client's <c>&lt;img onerror&gt;</c> falls back to a link-out to the
    /// raw URL (a degraded case is a link, never a broken image).
    /// </summary>
    private static async Task<IResult> MediaProxyHandler(
        HttpContext context,
        IPersistenceProvider persistence,
        IMediaFetcher mediaFetcher,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        // 1. The external attachment URL is the ?url= query parameter (the client's render boundary
        // always has the originator's attachment url — it is the stable key, not a content hash). It
        // must be an absolute HTTP(S) URL (a relative or non-HTTP(S) value is rejected with 400 — the
        // proxy only proxies absolute remote media URLs).
        if (!context.Request.Query.TryGetValue(Iris.Client.MediaConstants.ProxyQueryParam, out var urlValue)
            || urlValue.Count == 0
            || string.IsNullOrWhiteSpace(urlValue[0])
            || !Uri.TryCreate(urlValue[0], UriKind.Absolute, out var sourceUri)
            || (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest();
        }

        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var sourceUrl = new Iri(sourceUri);

        // 2. Cache hit: the URL was already stored (by a prior proxy hit or the eager-warm hook) →
        // serve straight from the store with no outbound fetch.
        if (await persistence.Media
                .TryGetMediaIriBySourceUrlAsync(sourceUrl, out var existingIri, ct)
                .ConfigureAwait(false)
            && existingIri is { } existing
            && await persistence.Media
                    .TryGetAsync(existing, out var cachedBytes, out var cachedType, out _, ct)
                    .ConfigureAwait(false)
                && cachedBytes is not null
                && cachedType is not null)
        {
            context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = "max-age=31536000, immutable";
            return Results.File(cachedBytes, cachedType);
        }

        // 3. Miss: fetch the remote URL once (unsigned outbound GET). A failure (4xx/5xx, network
        // error, timeout) is an expected condition → 502 (the client falls back to a link-out).
        var fetched = await mediaFetcher.FetchAsync(sourceUrl, ct).ConfigureAwait(false);
        if (fetched is null)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        // 4. Enforce the same size cap as an upload (a media attachment must not be unbounded).
        if (fetched.Content.Length > MaxMediaUploadBytes)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        // 5. Store the fetched bytes (keyed by the source URL + a server-internal content-hash dedupe)
        // and serve them from the same origin, long-cacheable.
        var mediaIri = await persistence.Media
            .PutBySourceUrlAsync(sourceUrl, fetched.Content, fetched.ContentType, new Iri(baseUrl), ct)
            .ConfigureAwait(false);
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = "max-age=31536000, immutable";
        return Results.File(fetched.Content, fetched.ContentType);
    }

    /// <summary>
    /// The maximum size (in bytes) of a single media upload (Phase 20.4 (a)).
    /// </summary>
    private const long MaxMediaUploadBytes = 10L * 1024 * 1024; // 10 MiB

    /// <summary>
    /// The named-client name for the media-proxy outbound fetch (Phase 20.4 (d)).
    /// </summary>
    private const string MediaFetcherClientName = "IrisMediaFetcher";

    /// <summary>
    /// The timeout for a single media-proxy outbound fetch (Phase 20.4 (d)). A media attachment is a
    /// bounded asset; a generous timeout bounds the wait while tolerating slow remote media hosts.
    /// </summary>
    private static readonly TimeSpan MediaFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shared core for the actor and community inbox POST endpoints: signature check, recipient
    /// existence check, inbound rate-limit check (Phase 17.4), body read + deserialize + cast, and
    /// inbox-processor dispatch.
    /// </summary>
    private static async Task<IResult> HandleInboxPostAsync(
        HttpContext context,
        Iri recipientIri,
        bool exists,
        IInboxProcessor inboxProcessor,
        IInboundRateLimiter rateLimiter,
        CancellationToken ct)
    {
        var outcome = SignatureValidationMiddleware.GetResult(context);
        if (!outcome.IsValid)
        {
            return Results.Unauthorized();
        }

        if (!exists)
        {
            return Results.NotFound();
        }

        // Phase 17.4: per-peer inbound rate limit. The peer is keyed by the host of the signer's
        // keyId (the sender host). A peer that exceeds its per-minute budget receives 429 Too Many
        // Requests (fail-fast; the request is not queued or retried). A disabled limiter (0 = disabled)
        // permits all requests.
        var senderHost = outcome.KeyId.Uri.IsAbsoluteUri
            ? outcome.KeyId.Uri.Host.ToLowerInvariant()
            : outcome.KeyId.Value;
        if (!rateLimiter.TryAcquire(senderHost, ct))
        {
            // Phase 18.3: send an HTTP-date Retry-After (RFC 9110 §10.2.1) so the client's
            // RetryHandler can back off precisely (the date is when the peer's window resets).
            var retryAfter = rateLimiter.GetRetryAfter(senderHost);
            if (retryAfter > DateTimeOffset.UtcNow)
            {
                context.Response.Headers.Append(
                    "Retry-After",
                    retryAfter.ToUniversalTime().ToString("R"));
            }
            else
            {
                // Fallback: the window already expired (race) — send a 1-second delta.
                context.Response.Headers.Append("Retry-After", "1");
            }
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest();
        }

        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);

        // Mute is not an ActivityStreams type (the library has no Mute class), so an inbound
        // "type": "Mute" deserializes to a plain Object (not an Activity): its actor/object references
        // land in the Object's ExtensionData. Wrap it into the Iris-specific MuteActivity (the thin
        // Activity subclass that pins Type to ["Mute"]) so the InboxProcessor stores it and the
        // MuteActivityHandler records the muter → muted edge (24.2).
        // A standalone inbound Tombstone (an IObject, not an Activity): a peer signals that an object was
        // deleted on its instance (F-10). Store the tombstone under the object IRI (so a GET serves it,
        // not stale content or a 404) and clean up any local copy (outbox Create + object → Create index
        // + reply edge) — the inbound half of the tombstone contract.
        if (payload is KristofferStrube.ActivityStreams.Tombstone { Id: not null } inboundTombstone)
        {
            var persistence = context.RequestServices.GetRequiredService<IPersistenceProvider>();
            await TombstoneInbound.ApplyAsync(persistence, inboundTombstone, ct).ConfigureAwait(false);
            return Results.Accepted();
        }

        Activity? activity;
        if (payload is Activity { Id: not null } typedActivity)
        {
            activity = typedActivity;
        }
        else if (payload is KristofferStrube.ActivityStreams.Object { Id: not null } genericMute
            && IsMuteType(genericMute))
        {
            activity = WrapAsMuteActivity(genericMute);
        }
        else
        {
            return Results.BadRequest();
        }

        try
        {
            await inboxProcessor
                .ProcessAsync(new InboxDelivery(recipientIri, activity), ct)
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
    /// Determines whether a deserialized generic <see cref="KristofferStrube.ActivityStreams.Object"/>
    /// (the result of deserializing an unknown activity <c>type</c>, e.g. the Iris-specific
    /// <c>Mute</c>) is a <c>Mute</c> activity by inspecting its <c>type</c> property (which the
    /// ActivityStreams converter stores verbatim, since the
    /// library has no <c>Mute</c> type).
    /// </summary>
    private static bool IsMuteType(KristofferStrube.ActivityStreams.Object @object)
    {
        return @object.Type is { } types
            && types.Contains(MuteActivity.MuteType, StringComparer.Ordinal);
    }

    /// <summary>
    /// Wraps a deserialized generic <c>Object</c> carrying a <c>Mute</c> <c>type</c> into the
    /// Iris-specific <see cref="MuteActivity"/> (the thin <see cref="Activity"/> subclass that pins
    /// <c>Type</c> to <c>["Mute"]</c>). Because the library has no <c>Mute</c> class, the deserializer
    /// produced a plain <c>Object</c> whose <c>actor</c> (the muter) and <c>object</c> (the muted actor)
    /// references live in its <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/>; this
    /// reads them and builds the <see cref="MuteActivity"/> so the <see cref="InboxProcessor"/> stores the
    /// activity and the <see cref="MuteActivityHandler"/> records the muter → muted edge (24.2).
    /// </summary>
    private static MuteActivity WrapAsMuteActivity(KristofferStrube.ActivityStreams.Object @object)
    {
        ILink? muter = ReadFirstIriLink(@object.ExtensionData, "actor");
        ILink? muted = ReadFirstIriLink(@object.ExtensionData, "object");
        return new MuteActivity
        {
            Id = @object.Id,
            Actor = muter is { } m ? [m] : null,
            Object = muted is { } o ? [o] : null,
        };
    }

    /// <summary>
    /// Reads the first IRI-bearing link from a deserialized <c>Object</c>'s <c>actor</c>/<c>object</c>
    /// extension data (a single <c>Link</c>, or a scalar IRI string) — the references the ActivityStreams
    /// converter parks in <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/> for an
    /// activity type it does not model (here, the Iris-specific <c>Mute</c>).
    /// </summary>
    private static ILink? ReadFirstIriLink(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData,
        string key)
    {
        if (extensionData is not { } data
            || !data.TryGetValue(key, out var element))
        {
            return null;
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var iri = element.GetString();
            return string.IsNullOrWhiteSpace(iri) ? null : new Link { Href = new Uri(iri) };
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var link = ActivityJson.Deserialize<ILink>(element.GetRawText());
            if (link is { } && link.Href is { } href)
            {
                return new Link { Href = href };
            }
        }

        return null;
    }

    private static async Task<IResult> InboxHandler(
        HttpContext context,
        string handle,
        IPersistenceProvider persistence,
        IInboxProcessor inboxProcessor,
        IInboundRateLimiter rateLimiter,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        var exists = await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false);
        return await HandleInboxPostAsync(context, actorIri, exists, inboxProcessor, rateLimiter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Outbox publish: handles <c>POST /ap/v1/u/{handle}/outbox</c> — the write surface for the
    /// activities the local actor <em>authors</em>. Per the delivery model, a client never addresses a
    /// recipient's inbox for an activity it authors; it publishes the activity to the acting actor's own
    /// outbox. This handler signature-validates the acting local actor, records the activity in that
    /// actor's outbox (so the actor's feed / outbox collection surfaces it) and the activity store (for
    /// <see cref="Undo"/> resolution and for the remote side to look it up), records the local edge the
    /// activity implies (a follow/block/flag/like, or its undo), and — the server's job, not the
    /// client's — resolves the recipient and server-delivers the activity to the recipient's inbox.
    /// </summary>
    /// <remarks>
    /// The recipient is derived from the activity: the activity's <c>object</c> for a
    /// <see cref="Follow"/>/<see cref="Block"/>/<see cref="Flag"/> (the actor being followed/blocked/
    /// flagged), the original activity's <c>object</c> for an <see cref="Undo"/> of one, the object's
    /// <c>attributedTo</c> for a <see cref="Like"/> (the object's owner), and the author's
    /// <em>remote</em> followers for a <see cref="Create"/> (the post's federation target). A
    /// local-only recipient (a local actor) needs no cross-instance hop — the local edge is already
    /// recorded — so the server delivers only to remote recipients (the mirror of
    /// <see cref="CreateActivityHandler"/>'s remote-follower loop).
    /// </remarks>
    private static async Task<IResult> OutboxPublishHandler(
        HttpContext context,
        string handle,
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        IEnumerable<IActivityHandler> handlers,
        IdMinter idMinter,
        LocalCollectionPageCache collectionCache,
        LocalActorDocumentCache actorDocumentCache,
        IActivityPubClient? objectFetch,
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
        var actorIri = BuildActorIri(baseUrl, handle);

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
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

        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);

        // Mute is not an ActivityStreams type (the library has no Mute class), so a posted "type": "Mute"
        // deserializes to a plain Object (not an Activity): its actor/object references land in the
        // Object's ExtensionData. Wrap it into the Iris-specific MuteActivity (the thin Activity subclass
        // that pins Type to ["Mute"]) so the publish proceeds — the outbox-publish mute arm records the
        // local edge and the server delivers it to the muted actor's inbox (24.2). Mirrors the inbox
        // endpoint's wrap.
        Activity? activity;
        if (payload is Activity typedActivity)
        {
            activity = typedActivity;
        }
        else if (payload is KristofferStrube.ActivityStreams.Object genericMute && IsMuteType(genericMute))
        {
            activity = WrapAsMuteActivity(genericMute);
        }
        else
        {
            return Results.BadRequest();
        }

        // The acting actor must be the activity's actor (the client publishes an activity it authors to
        // its own outbox; the server enforces that the signer owns the activity).
        var actingActorIri = activity.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!actingActorIri.HasValue || actingActorIri.Value != actorIri)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Docker-only-routable IRI normalization: the authoring client dials the instance on a
        // host-published base (e.g. http://localhost:8081) and carries that base in the activity's
        // object references (a Follow's target), but the instance's local actors are stored under the
        // advertised base (e.g. https://iris-dev1.luit.ink). When the object is a local actor reached
        // via the dial base, rewrite it to the advertised base so the local-actor check (and the
        // recorded edge) use the canonical IRI — otherwise the instance treats its own actor as remote
        // and attempts a cross-instance delivery that cannot route.
        await NormalizeLocalActorObjectIriAsync(activity, baseUrl, context.Request.Host.Value ?? string.Empty, persistence, ct).ConfigureAwait(false);

        // Decision 055: the server is the sole authority for the id of an object/activity it creates.
        // The client sends the activity shape (type, actor, object content/references) WITHOUT an id;
        // the server mints a collision-resistant, unguessable ULID in a fixed per-type namespace and
        // assigns it to the activity — and to any embedded object (a Note/Group inside a Create), whose
        // id the client no longer sends either. The minted id is returned to the authoring client in the
        // 202 body so it can reference the object later (an Undo, a delete, an Accept of this follow).
        MintActivityIds(idMinter, actorIri, activity);

        // 19.6.5 audience metadata: rewrite the on-the-wire audience (to/cc) of an outbound Create/Announce
        // to enumerate the actual distribution list (the remote, non-blocked follower set — and, for a
        // reply, the reply target). This runs BEFORE the outbox/activity-store record so the stored form and
        // the federated (on-the-wire) form are the same canonical activity; no-op for other activity types.
        await RewriteOutboundAudienceAsync(activity, actorIri, persistence, localActors, ct).ConfigureAwait(false);

        try
        {
            // 1. Record the activity in the actor's outbox (surfaces it in the actor's feed / outbox
            //    collection) and the activity store (for Undo resolution + the remote side to look it up).
            await persistence.Activities.AddToOutboxAsync(actorIri, activity, ct).ConfigureAwait(false);
            await persistence.Activities.PutActivityAsync(activity, ct).ConfigureAwait(false);

            // 19.6.2 outbox-enumeration correctness: the outbox collection page is served through the local
            // collection-page response cache (a 60s TTL). A new outbox write prepends to the collection
            // (newest-first), so without invalidation the next non-?refresh read within the TTL returns the
            // stale page — the actor's outbox card would not show the activity it just published. Drop the
            // cached page-1 entry (the key a newest-first insert always affects; the new item lands at the
            // head of the collection) so the next read re-renders with the new activity.
            InvalidateLocalOutboxPage(collectionCache, actorIri);

            // 2. Record the local edge the activity implies + resolve the recipient(s) for the
            //    server→server delivery hop (the client never enumerates recipients — that is the
            //    server's job).
            if (activity is Create create)
            {
                // A Create fans out to every remote, non-blocked follower (G-1 residual: the full
                // fan-out, mirroring CreateActivityHandler's loop, not just the first follower). When the
                // embedded object is a community (a Group whose IRI is this instance's /ap/v1/c/{name}),
                // the community is also stored in the community store (19.5.1 creation write path).
                var recipients = await RecordCreateLocalAsync(persistence, localActors, actorIri, create, baseUrl, ct)
                    .ConfigureAwait(false);
                foreach (var recipient in recipients)
                {
                    await delivery.DeliverToActorAsync(recipient, activity, actorIri, ct).ConfigureAwait(false);
                }

                // F-06 relay fan-out: deliver the Create to each of the actor's subscribed relays (the
                // star-subscribed fan-out servers) so they can re-fan the content to the wider federation.
                await DeliverToRelaysAsync(persistence, delivery, actorIri, activity, ct).ConfigureAwait(false);
            }
            else if (activity is Announce announce)
            {
                // An Announce (boost/repost) fans out to every remote, non-blocked follower, mirroring
                // the Create branch (F-15: outbound Announce federation). Unlike a Create, an Announce
                // carries no embedded object — it is a reference to an existing object IRI — so no
                // object-store write is needed.
                //
                // Record the local announce edge (announcer → announced-object, both directions) BEFORE
                // the fan-out: this is the durable boost record and the per-object boost counter
                // (decision 056 (d), the object's <c>shares</c> collection / <c>totalItems</c>). The
                // inbound-federation path records the same edge in AnnounceActivityHandler; the local
                // outbox-write path (this branch) records it here so a local boost is counted exactly
                // like a local like is (RecordLikeLocalAsync). Reversible via Undo(Announce)
                // (RecordUndoLocalAsync → RemoveAnnounceLocalAsync).
                await RecordAnnounceLocalAsync(persistence, actorIri, announce, objectFetch, ct).ConfigureAwait(false);

                var recipients = await GetRemoteNonBlockedFollowersAsync(persistence, localActors, actorIri, ct)
                    .ConfigureAwait(false);
                foreach (var recipient in recipients)
                {
                    await delivery.DeliverToActorAsync(recipient, activity, actorIri, ct).ConfigureAwait(false);
                }

                // F-06 relay fan-out: deliver the Announce to each of the actor's subscribed relays.
                await DeliverToRelaysAsync(persistence, delivery, actorIri, activity, ct).ConfigureAwait(false);
            }
            else if (activity is Delete delete)
            {
                // A delete (a local actor deleting their own content) routes to the DeleteActivityHandler —
                // the same handler that handles an inbound Delete — so the tombstone, reply-edge cleanup,
                // and the federated propagation to remote followers all go through the one code path. The
                // Delete was already recorded in the outbox + activity store (steps 1); the handler applies
                // the local object-store change and the propagation. A non-author (or an object not stored
                // here) is a no-op (the handler's owner guard), so the 202 (recorded) is still correct.
                if (handlers.OfType<DeleteActivityHandler>().FirstOrDefault() is { } deleteHandler)
                {
                    await deleteHandler.HandleAsync(new InboxDelivery(actorIri, delete), delete, ct).ConfigureAwait(false);
                }
            }
            else if (activity is Update update)
            {
                // An update (a local actor editing their own content) routes to the UpdateActivityHandler —
                // the same handler that handles an inbound Update — so the local object-store refresh and
                // the federated propagation to remote followers (the federated half of F-02) all go through
                // the one code path. The Update was already recorded in the outbox + activity store (steps
                // 1); the handler applies the local object-store change and the propagation. A non-author
                // (or an object not stored here) is a no-op (the handler's owner guard), so the 202
                // (recorded) is still correct.
                if (handlers.OfType<UpdateActivityHandler>().FirstOrDefault() is { } updateHandler)
                {
                    await updateHandler.HandleAsync(new InboxDelivery(actorIri, update), update, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // AP-native person settings change (22.6.1): an Add (enable) or Remove (disable) of the
                // actor's own document carrying the manuallyApprovesFollowers extension updates the stored
                // actor's ExtensionData (mirroring RecordCommunityAddAsync / RecordCommunityRemoveAsync for
                // the community's manuallyApprovesMembers gate). A local-only operation — no delivery.
                if (activity is Add personAdd)
                {
                    await RecordPersonAddAsync(persistence, actorIri, personAdd, ct).ConfigureAwait(false);
                    // The actor's public document now renders differently (iris:settings / capabilities).
                    // Drop the cached render so the next public read re-renders (mirrors the outbox-page
                    // invalidation above — without it the 60s-TTL document cache serves a stale copy).
                    actorDocumentCache.Invalidate(actorIri);
                    return Results.Text(ActivityJson.Serialize(activity), ActivityJson.ActivityJsonContentType, statusCode: 202);
                }

                if (activity is Remove personRemove)
                {
                    await RecordPersonRemoveAsync(persistence, actorIri, personRemove, ct).ConfigureAwait(false);
                    actorDocumentCache.Invalidate(actorIri);
                    return Results.Text(ActivityJson.Serialize(activity), ActivityJson.ActivityJsonContentType, statusCode: 202);
                }

                Iri? recipientIri = activity switch
                {
                    Follow follow => await RecordFollowLocalAsync(persistence, localActors, actorIri, follow, ct).ConfigureAwait(false),
                    Block block => await RecordBlockLocalAsync(persistence, localActors, actorIri, block, ct).ConfigureAwait(false),
                    Flag flag => await RecordFlagLocalAsync(persistence, localActors, actorIri, flag, ct).ConfigureAwait(false),
                    MuteActivity mute => await RecordMuteLocalAsync(persistence, actorIri, mute, ct).ConfigureAwait(false),
                    Like like => await RecordLikeLocalAsync(persistence, actorIri, like, objectFetch, ct).ConfigureAwait(false),
                    Undo undo => await RecordUndoLocalAsync(persistence, localActors, actorIri, undo, objectFetch, ct).ConfigureAwait(false),
                    Accept accept => await RecordFollowDecisionLocalAsync(persistence, actorIri, accept, accept: true, ct).ConfigureAwait(false),
                    Reject reject => await RecordFollowDecisionLocalAsync(persistence, actorIri, reject, accept: false, ct).ConfigureAwait(false),
                    _ => null,
                };

                // 19.6.2 moderation-collection enumeration correctness: the actor's moderation
                // collections (blocks / flags / mutes) are served through the same local collection-page
                // response cache (a 60s TTL). A moderation write that is not paired with an invalidation
                // leaves the cached page-1 stale: the owner's card would not reflect the edge it just
                // recorded (a Block / a Flag) or removed (an Undo of one) until the TTL lapses or a
                // ?refresh=true bypass is issued. Drop the affected collection's page-1 entry so the next
                // non-?refresh read re-renders. An Undo only affects a moderation collection when it undoes
                // a Block or a Flag (an Undo of a Follow / Like / Announce touches no moderation
                // collection), so resolve the undone sub-activity and invalidate accordingly.
                switch (activity)
                {
                    case Block:
                        InvalidateLocalCollectionPage(collectionCache, actorIri, "blocks");
                        break;
                    case Flag:
                        InvalidateLocalCollectionPage(collectionCache, actorIri, "flags");
                        break;
                    case MuteActivity:
                        InvalidateLocalCollectionPage(collectionCache, actorIri, "mutes");
                        break;
                    case Undo undone:
                        var undoneIri = undone.Object?.FirstOrDefault().ResolveObjectIri();
                        if (undoneIri.HasValue
                            && await persistence.Activities.TryGetActivityAsync(undoneIri.Value, out var undoneActivity, ct).ConfigureAwait(false))
                        {
                            if (undoneActivity is Block)
                            {
                                InvalidateLocalCollectionPage(collectionCache, actorIri, "blocks");
                            }
                            else if (undoneActivity is Flag)
                            {
                                InvalidateLocalCollectionPage(collectionCache, actorIri, "flags");
                            }
                            else if (undoneActivity is MuteActivity)
                            {
                                InvalidateLocalCollectionPage(collectionCache, actorIri, "mutes");
                            }
                        }

                        break;
                }

                // 3. The server (not the client) delivers the activity to the recipient's inbox. A local
                //    recipient needs no cross-instance hop (the local edge is already recorded); only a
                //    remote recipient is delivered to, signed as the acting local actor.
                if (recipientIri is { } recipient
                    && !await localActors.IsLocalActorAsync(recipient, ct).ConfigureAwait(false))
                {
                    await delivery.DeliverToActorAsync(recipient, activity, actorIri, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Iris.Server.OutboxPublishHandler")
                .LogError(ex, "Outbox publish for actor {Handle} ({Type}) failed.", handle, activity.GetType().Name);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        // Decision 055: return the created activity (with its server-minted id) in the 202 body so the
        // authoring client can learn the id and reference the object later (an Undo, a delete, an Accept
        // of this follow). The body is the activity serialized as ActivityStreams JSON (a raw text body —
        // NOT Results.Accepted(string), which would JSON-serialize the string into a quoted JSON string).
        return Results.Text(ActivityJson.Serialize(activity), ActivityJson.ActivityJsonContentType, statusCode: 202);
    }

    /// <summary>
    /// The WRITE SURFACE for the activities a local community (a <see cref="Group"/> actor) AUTHORS: a
    /// <see cref="Follow"/> (the community follows a remote actor/community — gap G-3) or an <see
    /// cref="Undo"/> of such a follow (an un-follow). Mirrors <see cref="OutboxPublishHandler"/> for a
    /// Group: the client publishes the community-authored activity to the community's own outbox, the
    /// server records the activity in the community's outbox (so the community's <c>following</c>
    /// collection surfaces the edge) + the activity store, records the community's follows-set edge (the
    /// inverse of <see cref="FollowActivityHandler"/>'s community branch), and is the only thing that
    /// delivers the activity to the target's inbox (the server→server hop, signed as the community — a
    /// Group signs just like a Person). Requires a valid HTTP signature from the community (validated by
    /// <c>SignatureValidationMiddleware</c>); unsigned or invalidly-signed requests are rejected with 401.
    /// </summary>
    /// <remarks>
    /// <strong>Follow vs. Undo.</strong> A <c>Follow</c> records the community's follows-set edge
    /// (<see cref="ICommunityStore.AddFollowAsync"/>) and server-delivers the follow to the target's inbox
    /// (signed as the community). An <c>Undo</c> of a follow resolves the original follow from the activity
    /// store (the community stored it when it authored it), removes the community's follows-set edge (the
    /// inverse of the follow), and server-delivers the <c>Undo</c> to the target's inbox (so the target
    /// removes the edge it recorded on receipt). A <c>Follow</c> of a <em>local</em> target records the
    /// local edge but performs no cross-instance hop (the local edge is already recorded; the local target
    /// is not re-delivered to, matching <c>OutboxPublishHandler</c>'s local-recipient rule).
    /// </remarks>
    /// <param name="context">The HTTP context (the request body is the community-authored activity).</param>
    /// <param name="name">The community's handle (the <c>{name}</c> route value).</param>
    /// <param name="persistence">The persistence provider (activity store + community store).</param>
    /// <param name="delivery">The delivery service (schedules the server→server delivery hop).</param>
    /// <param name="idMinter">The id minter (decision 055: mints the community-authored activity's id).</param>
    /// <param name="optionsAccessor">The server options (the base URL, for the community IRI).</param>
    /// <param name="collectionCache">The local collection-page response cache (invalidated on the outbox
    /// write so the community's outbox card reflects the new activity immediately).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// <see cref="StatusCodes.Status202Accepted"/> when the activity was recorded + (for a remote target)
    /// delivery scheduled; <see cref="StatusCodes.Status401Unauthorized"/> when the request is unsigned or
    /// invalidly signed (the middleware result); <see cref="StatusCodes.Status404NotFound"/> when the
    /// community is unknown; <see cref="StatusCodes.Status403Forbidden"/> when the activity's actor is not
    /// this community; <see cref="StatusCodes.Status400BadRequest"/> for a non-Follow/Undo or a follow/undo
    /// whose target is not resolvable; <see cref="StatusCodes.Status500InternalServerError"/> on a store
    /// failure.
    /// </returns>
    private static async Task<IResult> CommunityOutboxPublishHandler(
        HttpContext context,
        string name,
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        IdMinter idMinter,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
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

        if (!await persistence.Communities
                .TryGetCommunityAsync(communityIri, out _, ct)
                .ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // The request body may not be seekable in TestHost (the middleware has already drained it), so
        // read it into a buffer before deserializing — mirroring the operator reject endpoint's read.
        var json = await ReadAsBufferedStringAsync(context.Request.Body, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest();
        }

        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);

        // The community is the only author of its outbox: the activity's actor must be this community
        // (mirrors OutboxPublishHandler's acting-actor check). A Follow and the Undo of a follow both
        // carry the community as their actor.
        var actingIri = payload is Activity activity
            ? activity.Actor?.FirstOrDefault().ResolveObjectIri()
            : null;
        if (actingIri is not { } actorIri || actorIri != communityIri)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Decision 055: the server is the sole authority for the object id. The community (like an actor)
        // authors id-less activities through its own outbox; the server mints the activity's id (and any
        // embedded Create object's id) before recording it, so the activity store can key it.
        if (payload is not Activity activityToMint)
        {
            return Results.BadRequest();
        }
        MintActivityIds(idMinter, communityIri, activityToMint);

        try
        {
            // Membership self-management (Add/Remove) is a local-only operation: the community edits its
            // own members (the actor == community gate above already passed), so no cross-instance delivery
            // is needed. It is handled separately from the follow/decision cases, which DO carry a delivery
            // recipient.
            if (payload is Add add)
            {
                await RecordCommunityAddAsync(persistence, communityIri, add, ct).ConfigureAwait(false);
                return await FinishCommunityOutboxPublishAsync(persistence, communityIri, payload, null, delivery, collectionCache, ct)
                    .ConfigureAwait(false);
            }

            if (payload is Remove remove)
            {
                await RecordCommunityRemoveAsync(persistence, communityIri, remove, ct).ConfigureAwait(false);
                return await FinishCommunityOutboxPublishAsync(persistence, communityIri, payload, null, delivery, collectionCache, ct)
                    .ConfigureAwait(false);
            }

            Iri? recipientIri = payload switch
            {
                Follow follow => await RecordCommunityFollowAsync(persistence, communityIri, follow, ct)
                    .ConfigureAwait(false),
                Undo undo => await RecordCommunityUnfollowAsync(persistence, communityIri, undo, ct)
                    .ConfigureAwait(false),
                Accept accept => await RecordCommunityDecisionAsync(persistence, communityIri, accept, accept: true, ct)
                    .ConfigureAwait(false),
                Reject reject => await RecordCommunityDecisionAsync(persistence, communityIri, reject, accept: false, ct)
                    .ConfigureAwait(false),
                _ => null,
            };
            if (recipientIri is null)
            {
                return Results.BadRequest();
            }

            return await FinishCommunityOutboxPublishAsync(persistence, communityIri, payload, recipientIri, delivery, collectionCache, ct)
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
    }

    /// <summary>
    /// Records a published community-outbox activity in the community's outbox + activity store and
    /// (when a non-null, non-local <paramref name="recipientIri"/> is supplied) delivers it to the
    /// recipient's inbox, signed as the community. Returns the 202 Accepted. A <see langword="null"/>
    /// recipient (a local-only membership Add/Remove) records without delivering.
    /// </summary>
    private static async Task<IResult> FinishCommunityOutboxPublishAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        IObjectOrLink? payload,
        Iri? recipientIri,
        IDeliveryService delivery,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        // Record the activity in the community's outbox (so the `following`/membership collections
        // surface the edge) and the activity store (for Undo resolution + the remote side to look it up).
        ArgumentNullException.ThrowIfNull(payload);
        await persistence.Activities.AddToOutboxAsync(communityIri, payload, ct).ConfigureAwait(false);
        await persistence.Activities.PutActivityAsync((IObject)payload, ct).ConfigureAwait(false);

        // 19.6.2 outbox-enumeration correctness: same as OutboxPublishHandler — drop the community's
        // cached outbox page-1 so the next non-?refresh read reflects the activity just published.
        InvalidateLocalOutboxPage(collectionCache, communityIri);

        // The server (not the client) delivers the activity to the target's inbox. A local target
        // needs no cross-instance hop (the local edge is already recorded); only a remote target is
        // delivered to, signed as the community.
        if (recipientIri is { } recipient
            && !await IsLocalCommunityAsync(persistence, recipient, ct).ConfigureAwait(false)
            && !await IsLocalActorAsync(persistence, recipient, ct).ConfigureAwait(false))
        {
            await delivery.DeliverToActorAsync(recipient, (Activity)payload, communityIri, ct).ConfigureAwait(false);
        }

        // Decision 055: return the created object (with its minted id) in the 2xx body so the client can
        // learn the id (for a future Undo of this activity, e.g. un-adding a member).
        return Results.Text(ActivityJson.Serialize((Activity)payload), ActivityJson.ActivityJsonContentType, statusCode: 202);
    }

    /// <summary>
    /// Records a person <see cref="Add"/> (AP-native settings change, 22.6.1): when the <c>object</c> is
    /// the actor's own document carrying the <c>manuallyApprovesFollowers</c> extension, sets that flag on
    /// the stored actor's <c>ExtensionData</c>. Other objects are ignored (a person's followers are owned
    /// by the follow lifecycle, not by <c>Add</c>). The actor == actor gate is applied by the caller.
    /// </summary>
    private static async Task RecordPersonAddAsync(
        IPersistenceProvider persistence,
        Iri actorIri,
        Add add,
        CancellationToken ct)
    {
        var objectIri = add.Object?.FirstOrDefault().ResolveObjectIri();
        if (objectIri is { } obj && obj == actorIri &&
            add.Object?.FirstOrDefault() is IObject { ExtensionData: { } ext } &&
            ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var value))
        {
            await SetManuallyApprovesFollowersAsync(persistence, actorIri, value.ValueKind == System.Text.Json.JsonValueKind.True, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a person <see cref="Remove"/> (AP-native settings change, 22.6.1): when the <c>object</c>
    /// is the actor's own document carrying the <c>manuallyApprovesFollowers</c> extension, clears that
    /// flag on the stored actor's <c>ExtensionData</c>. Other objects are ignored. The actor == actor gate
    /// is applied by the caller.
    /// </summary>
    private static async Task RecordPersonRemoveAsync(
        IPersistenceProvider persistence,
        Iri actorIri,
        Remove remove,
        CancellationToken ct)
    {
        var objectIri = remove.Object?.FirstOrDefault().ResolveObjectIri();
        if (objectIri is { } obj && obj == actorIri &&
            remove.Object?.FirstOrDefault() is IObject { ExtensionData: { } ext } &&
            ext.ContainsKey(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName))
        {
            await SetManuallyApprovesFollowersAsync(persistence, actorIri, enabled: false, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets or clears the <c>manuallyApprovesFollowers</c> flag on the stored actor's
    /// <c>ExtensionData</c> (AP-native settings change, 22.6.1): the actor publishes an <c>Add</c>
    /// (enable) or <c>Remove</c> (disable) of its own document carrying the flag, and this method updates
    /// the stored actor so the <see cref="FollowActivityHandler"/> gate reflects the change on the next
    /// inbound <c>Follow</c>.
    /// </summary>
    private static async Task SetManuallyApprovesFollowersAsync(
        IPersistenceProvider persistence,
        Iri actorIri,
        bool enabled,
        CancellationToken ct)
    {
        if (!await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct).ConfigureAwait(false)
            || actor is null)
        {
            return;
        }

        actor.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        if (enabled)
        {
            actor.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] =
                System.Text.Json.JsonDocument.Parse("true").RootElement.Clone();
        }
        else
        {
            actor.ExtensionData.Remove(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName);
        }

        await persistence.Actors.PutActorAsync(actor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a community <see cref="Add"/> (membership self-management or settings change): adds the
    /// <c>object</c> (the member) to the community's member set via <see cref="ICommunityStore.AddMemberAsync"/>,
    /// or — when the <c>object</c> is the community's own document carrying the
    /// <c>manuallyApprovesMembers</c> extension — sets that flag on the stored community (AP-native
    /// settings change, change 217). The actor == community gate is applied by the caller, so this
    /// records unconditionally.
    /// </summary>
    private static async Task RecordCommunityAddAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Add add,
        CancellationToken ct)
    {
        var memberIri = add.Object?.FirstOrDefault().ResolveObjectIri();
        if (memberIri is { } member)
        {
            // When the object is the community's own document (a settings change, not a membership add),
            // update the stored community's ExtensionData (the manuallyApprovesMembers flag) instead of
            // adding a member.
            if (member == communityIri && add.Object?.FirstOrDefault() is IObject { ExtensionData: { } ext } obj
                && ext.TryGetValue(ActivityPubServerConstants.ManuallyApprovesMembersExtensionName, out var value))
            {
                await SetManuallyApprovesMembersAsync(persistence, communityIri, value.ValueKind == System.Text.Json.JsonValueKind.True, ct)
                    .ConfigureAwait(false);
                return;
            }

            await persistence.Communities.AddMemberAsync(communityIri, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a community <see cref="Remove"/> (membership self-management or settings change): removes
    /// the <c>object</c> (the member) from the community's member set via
    /// <see cref="ICommunityStore.RemoveMemberAsync"/>, or — when the <c>object</c> is the community's own
    /// document carrying the <c>manuallyApprovesMembers</c> extension — clears that flag on the stored
    /// community (AP-native settings change, change 217). The actor == community gate is applied by the
    /// caller, so this records unconditionally.
    /// </summary>
    private static async Task RecordCommunityRemoveAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Remove remove,
        CancellationToken ct)
    {
        var memberIri = remove.Object?.FirstOrDefault().ResolveObjectIri();
        if (memberIri is { } member)
        {
            // When the object is the community's own document (a settings change, not a membership remove),
            // clear the manuallyApprovesMembers flag on the stored community.
            if (member == communityIri && remove.Object?.FirstOrDefault() is IObject { ExtensionData: { } ext } obj
                && ext.ContainsKey(ActivityPubServerConstants.ManuallyApprovesMembersExtensionName))
            {
                await SetManuallyApprovesMembersAsync(persistence, communityIri, enabled: false, ct)
                    .ConfigureAwait(false);
                return;
            }

            await persistence.Communities.RemoveMemberAsync(communityIri, member, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets or clears the <c>manuallyApprovesMembers</c> flag on the stored community's
    /// <c>ExtensionData</c> (AP-native settings change, change 217): the community publishes an
    /// <c>Add</c> (enable) or <c>Remove</c> (disable) of its own document carrying the flag, and this
    /// method updates the stored community so the <see cref="MembershipActivityHandler"/> gate reflects
    /// the change on the next inbound <c>Join</c>.
    /// </summary>
    /// <param name="persistence">The persistence provider.</param>
    /// <param name="communityIri">The IRI of the community whose flag is being changed.</param>
    /// <param name="enabled"><see langword="true"/> to set the flag (gated); <see langword="false"/> to
    /// clear it (open).</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task SetManuallyApprovesMembersAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        bool enabled,
        CancellationToken ct)
    {
        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out var community, ct).ConfigureAwait(false)
            || community is null)
        {
            return;
        }

        community.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        if (enabled)
        {
            community.ExtensionData[ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] =
                System.Text.Json.JsonSerializer.SerializeToElement(true);
        }
        else
        {
            community.ExtensionData.Remove(ActivityPubServerConstants.ManuallyApprovesMembersExtensionName);
        }

        await persistence.Communities.PutCommunityAsync(community, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the local follows-set edge for a <see cref="Follow"/> published to a community's outbox
    /// and returns the follow's target (the recipient of the server→server delivery). A community follow
    /// is recorded in the community's follows set (<see cref="ICommunityStore.AddFollowAsync"/>) — the
    /// inverse of the inbound <see cref="FollowActivityHandler"/>'s community branch — so the community's
    /// <c>following</c> collection lists the target. Returns <see langword="null"/> when the target is
    /// not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordCommunityFollowAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Follow follow,
        CancellationToken ct)
    {
        var targetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!targetIri.HasValue)
        {
            return null;
        }

        await persistence.Communities
            .AddFollowAsync(communityIri, targetIri.Value, ct)
            .ConfigureAwait(false);

        return targetIri.Value;
    }

    /// <summary>
    /// Records the local follows-set edge removal for an <see cref="Undo"/> of a follow published to a
    /// community's outbox and returns the original follow's target (the recipient of the server→server
    /// delivery, so the target removes the edge it recorded). Resolves the undone follow from the activity
    /// store (the community stored it when it authored it); a missing follow (never stored) is a
    /// <see langword="null"/> (a bad request — there is no edge to remove and no target to deliver to).
    /// </summary>
    private static async Task<Iri?> RecordCommunityUnfollowAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Undo undo,
        CancellationToken ct)
    {
        var referencedIri = undo.Object?.FirstOrDefault().ResolveObjectIri();
        if (!referencedIri.HasValue
            || !await persistence.Activities
                .TryGetActivityAsync(referencedIri.Value, out var stored, ct)
                .ConfigureAwait(false)
            || stored is not Follow follow)
        {
            return null;
        }

        var targetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!targetIri.HasValue)
        {
            return null;
        }

        await persistence.Communities
            .RemoveFollowAsync(communityIri, targetIri.Value, ct)
            .ConfigureAwait(false);

        return targetIri.Value;
    }

    /// <summary>
    /// Reports whether <paramref name="actorIri"/> is a local community (in the
    /// <see cref="ICommunityStore"/>).
    /// </summary>
    private static Task<bool> IsLocalCommunityAsync(IPersistenceProvider persistence, Iri actorIri, CancellationToken ct)
        => persistence.Communities.TryGetCommunityAsync(actorIri, out _, ct);

    /// <summary>
    /// Reports whether <paramref name="actorIri"/> is a local person (in the
    /// <see cref="IActorStore"/>).
    /// </summary>
    private static Task<bool> IsLocalActorAsync(IPersistenceProvider persistence, Iri actorIri, CancellationToken ct)
        => persistence.Actors.TryGetActorAsync(actorIri, out _, ct);

    /// <summary>
    /// Records the local follow edge for a <see cref="Follow"/> published to the actor's outbox and
    /// returns the follow's target (the recipient of the server→server delivery). A follow of a local
    /// person records the <c>follower → target</c> edge (honoring <c>manuallyApprovesFollowers</c>, which
    /// still records the edge); a follow of a local community records the community's follows + followers
    /// sets (the inverse of <see cref="FollowActivityHandler"/>'s community branch, F-24). Returns
    /// <see langword="null"/> when the target is not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordFollowLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri followerIri,
        Follow follow,
        CancellationToken ct)
    {
        var targetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!targetIri.HasValue)
        {
            return null;
        }

        // The actor's home instance records the follow edge in its own follow store regardless of
        // whether the target is local — the actor's `following` collection lists even a remote target.
        // A follow of a local person additionally makes the target's `followers` collection list the
        // follower (the same edge, read inversely); a follow of a local community records the
        // community's follows + followers sets (the inverse of FollowActivityHandler's community
        // branch, F-24) instead of a person-follow edge (the stores are disjoint).
        await persistence.Follows
            .RecordFollowAsync(followerIri, targetIri.Value, ct)
            .ConfigureAwait(false);

        if (await persistence.Communities.TryGetCommunityAsync(targetIri.Value, out _, ct).ConfigureAwait(false))
        {
            await persistence.Communities.AddFollowAsync(targetIri.Value, followerIri, ct).ConfigureAwait(false);
            await persistence.Communities.AddFollowerAsync(targetIri.Value, followerIri, ct).ConfigureAwait(false);
        }

        return targetIri.Value;
    }

    /// <summary>
    /// Records the local decision for an <see cref="Accept"/>/<see cref="Reject"/> published to a
    /// community's outbox, dispatching to the join-decision or follow-decision path based on the type of
    /// the referenced activity. When the decision's <c>object</c> references a <see cref="Join"/> the
    /// join-decision path is taken (19.5.2); otherwise the follow-decision path is taken (existing).
    /// Returns the recipient IRI for server→server delivery (or <see langword="null"/> when the decision
    /// cannot be resolved).
    /// </summary>
    private static async Task<Iri?> RecordCommunityDecisionAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Activity decision,
        bool accept,
        CancellationToken ct)
    {
        var referencedIri = decision.Object?.FirstOrDefault().ResolveObjectIri();
        if (referencedIri is not { } objIri)
        {
            return null;
        }

        if (await persistence.Activities.TryGetActivityAsync(objIri, out var stored, ct).ConfigureAwait(false)
            && stored is Join)
        {
            return await RecordJoinDecisionLocalAsync(persistence, communityIri, decision, accept, ct).ConfigureAwait(false);
        }

        return await RecordFollowDecisionLocalAsync(persistence, communityIri, decision, accept, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the local join decision for an <see cref="Accept"/>/<see cref="Reject"/> published to a
    /// community's outbox whose <c>object</c> references a <see cref="Join"/> (19.5.2). For an
    /// <see cref="Accept"/> the requesting actor is added as a member and the pending join request is
    /// removed; for a <see cref="Reject"/> the pending join request is removed (no membership granted).
    /// Returns the requesting actor (the recipient of the server→server delivery). Returns
    /// <see langword="null"/> when the referenced join is unknown or the requesting actor is not
    /// resolvable.
    /// </summary>
    private static async Task<Iri?> RecordJoinDecisionLocalAsync(
        IPersistenceProvider persistence,
        Iri communityIri,
        Activity decision,
        bool accept,
        CancellationToken ct)
    {
        var joinIri = decision.Object?.FirstOrDefault().ResolveObjectIri();
        if (!joinIri.HasValue
            || !await persistence.Activities.TryGetActivityAsync(joinIri.Value, out var stored, ct)
                .ConfigureAwait(false)
            || stored is not Join { Id: not null } join)
        {
            return null;
        }

        // The join's object (the joining actor) must be resolvable.
        var joinerIri = join.Object?.FirstOrDefault().ResolveObjectIri();
        if (!joinerIri.HasValue)
        {
            return null;
        }

        if (accept)
        {
            await persistence.Communities
                .AddMemberAsync(communityIri, joinerIri.Value, ct)
                .ConfigureAwait(false);
        }

        // Both accept and reject remove the pending join request (idempotent — a no-op when already
        // removed, e.g. a duplicate decision or a request that was never recorded).
        await persistence.Communities
            .RemoveJoinRequestAsync(communityIri, joinerIri.Value, ct)
            .ConfigureAwait(false);

        return joinerIri.Value;
    }

    /// <summary>
    /// Records the local follow decision for an <see cref="Accept"/>/<see cref="Reject"/> published to the
    /// followed actor's outbox and returns the follower (the recipient of the server→server delivery, so
    /// the remote finalizes or removes its edge). The <c>object</c> of the decision references the
    /// original <see cref="Follow"/> (by IRI); the acting local actor is the follow's target (the followed
    /// side — the outbox owner, already validated by the handler). For an <see cref="Accept"/> the
    /// follower→actor edge is ensured (idempotent — a gated follow's provisional edge is confirmed; the
    /// edge lives in the person <see cref="IFollowStore"/> or, when the target is a local community, the
    /// community's follows/followers sets); for a <see cref="Reject"/> the provisional edge is removed.
    /// Returns <see langword="null"/> when the referenced follow is unknown, its target is not the acting
    /// actor, or the follower is not resolvable.
    /// </summary>
    /// <remarks>
    /// This is the outbox (AP-native) follow-decision path (the legacy operator follow-decision endpoints
    /// were removed in Phase 19.0b): the client authors the
    /// <c>Accept</c>/<c>Reject</c> (its own id is minted by the server on the outbox write path — decision
    /// 055) and publishes it to the followed actor's outbox;
    /// this helper applies the local edge effect and returns the follower so the caller server-delivers
    /// the activity to the follower's inbox (signed as the acting local actor).
    /// </remarks>
    private static async Task<Iri?> RecordFollowDecisionLocalAsync(
        IPersistenceProvider persistence,
        Iri actorIri,
        Activity decision,
        bool accept,
        CancellationToken ct)
    {
        var followIri = decision.Object?.FirstOrDefault().ResolveObjectIri();
        if (!followIri.HasValue
            || !await persistence.Activities.TryGetActivityAsync(followIri.Value, out var stored, ct)
                .ConfigureAwait(false)
            || stored is not Follow { Id: not null } follow)
        {
            return null;
        }

        // The decision's target (the original follow's target) must be the acting local actor — an
        // accept/reject is always the followed side's decision about a follow made OF that actor.
        var targetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!targetIri.HasValue || targetIri.Value != actorIri)
        {
            return null;
        }

        var followerIri = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!followerIri.HasValue)
        {
            return null;
        }

        if (accept)
        {
            // Accept: ensure the follower → actor edge (idempotent). A local community target records the
            // community's follows/followers sets (the inverse of the inbound FollowActivityHandler's
            // community branch); a person target records the person follow edge.
            if (await persistence.Communities.TryGetCommunityAsync(targetIri.Value, out _, ct).ConfigureAwait(false))
            {
                await persistence.Communities.AddFollowAsync(targetIri.Value, followerIri.Value, ct).ConfigureAwait(false);
                await persistence.Communities.AddFollowerAsync(targetIri.Value, followerIri.Value, ct).ConfigureAwait(false);
            }
            else
            {
                await persistence.Follows.RecordFollowAsync(followerIri.Value, targetIri.Value, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Reject: remove the provisional follower → actor edge (a no-op when already removed). The
            // edge is the inverse of a remote follow: the follower is the remote actor, the target (this
            // actor) is local, so the edge lives in this actor's follow store (or the community's
            // follows/followers sets when the target is a local community).
            await persistence.Follows.RemoveFollowAsync(followerIri.Value, targetIri.Value, ct).ConfigureAwait(false);
            if (await persistence.Communities.TryGetCommunityAsync(targetIri.Value, out _, ct).ConfigureAwait(false))
            {
                await persistence.Communities.RemoveFollowAsync(targetIri.Value, followerIri.Value, ct).ConfigureAwait(false);
                await persistence.Communities.RemoveFollowerAsync(targetIri.Value, followerIri.Value, ct).ConfigureAwait(false);
            }
        }

        return followerIri.Value;
    }

    /// <summary>
    /// Records the local block edge for a <see cref="Block"/> published to the actor's outbox and returns
    /// the blocked actor (the recipient of the server→server delivery). The blocker is the acting local
    /// actor, so the edge is always recorded (the local actor's <c>blocks</c> collection lists the
    /// blocked actor). Returns <see langword="null"/> when the blocked actor is not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordBlockLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri blockerIri,
        Block block,
        CancellationToken ct)
    {
        var blockedIri = block.Object?.FirstOrDefault().ResolveObjectIri();
        if (!blockedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RecordBlockAsync(blockerIri, blockedIri.Value, ct).ConfigureAwait(false);
        return blockedIri.Value;
    }

    /// <summary>
    /// Records the local flag edge for a <see cref="Flag"/> published to the actor's outbox and returns
    /// the flagged actor (the recipient of the server→server delivery). The flagger is the acting local
    /// actor, so the edge is always recorded (the local actor's <c>flags</c> collection lists the flagged
    /// actor). Returns <see langword="null"/> when the flagged actor is not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordFlagLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri flaggerIri,
        Flag flag,
        CancellationToken ct)
    {
        var flaggedIri = flag.Object?.FirstOrDefault().ResolveObjectIri();
        if (!flaggedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RecordFlagAsync(flaggerIri, flaggedIri.Value, ct).ConfigureAwait(false);
        return flaggedIri.Value;
    }

    /// <summary>
    /// Records the local mute edge for a <see cref="MuteActivity"/> published to the actor's outbox and
    /// returns the muted actor (the recipient of the server→server delivery). The muter is the acting
    /// local actor, so the edge is always recorded (the local actor's <c>mutes</c> collection lists the
    /// muted actor). Returns <see langword="null"/> when the muted actor is not resolvable. (24.2 — the
    /// inverse of the inbound <see cref="MuteActivityHandler"/>.)
    /// </summary>
    private static async Task<Iri?> RecordMuteLocalAsync(
        IPersistenceProvider persistence,
        Iri muterIri,
        MuteActivity mute,
        CancellationToken ct)
    {
        var mutedIri = mute.Object?.FirstOrDefault().ResolveObjectIri();
        if (!mutedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RecordMuteAsync(muterIri, mutedIri.Value, ct).ConfigureAwait(false);
        return mutedIri.Value;
    }

    /// <summary>
    /// Records the local like edge for a <see cref="Like"/> published to the actor's outbox and returns
    /// the object's owner (the recipient of the server→server delivery). The liker is the acting local
    /// actor, so the edge (liker → object) is always recorded in the liker's <c>liked</c> collection. The
    /// owner is the object's <c>attributedTo</c>: a local object's owner is read from the object store; a
    /// remote object's owner is resolved by fetching the object's document over the wire (24.1) so the
    /// delivery reaches the object's author's inbox (the object IRI's own <c>/inbox</c> does not exist).
    /// Returns <see langword="null"/> when the object is not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordLikeLocalAsync(
        IPersistenceProvider persistence,
        Iri likerIri,
        Like like,
        IActivityPubClient? objectFetch,
        CancellationToken ct)
    {
        var objectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        await persistence.Likes.RecordLikeAsync(likerIri, objectIri.Value, ct).ConfigureAwait(false);
        return await ResolveObjectOwnerForDeliveryAsync(persistence, objectFetch, objectIri.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the recipient of the server→server delivery for a Like / Announce (or an Undo of one) of
    /// the object at <paramref name="objectIri"/>: the object's owner (its <c>attributedTo</c>). A local
    /// object's owner is read from the object store; a remote object's owner is resolved by fetching the
    /// object's document over the wire (24.1) and reading its <c>attributedTo</c>. When the owner is not
    /// resolvable (no local copy and the remote fetch yields no owner) the object IRI itself is returned
    /// as a best-effort fallback (the prior behavior — the remote instance routes the delivery to the
    /// owner). Returns <paramref name="objectIri"/> unchanged when unresolvable.
    /// </summary>
    private static async Task<Iri> ResolveObjectOwnerForDeliveryAsync(
        IPersistenceProvider persistence,
        IActivityPubClient? objectFetch,
        Iri objectIri,
        CancellationToken ct)
    {
        // A local object is in the object store: read its attributedTo directly (no wire hop).
        if (await persistence.Objects.TryGetObjectAsync(objectIri, out var storedObject, ct).ConfigureAwait(false)
            && storedObject is { }
            && storedObject.AttributedTo is { } localAttributed
            && localAttributed.FirstOrDefault().ResolveObjectIri() is { } localOwner)
        {
            return localOwner;
        }

        // A remote object is not in the object store: fetch its document over the wire and read its
        // attributedTo, so the delivery targets the object's author's inbox (not the object IRI's
        // non-existent /inbox). A fetch failure (network, not-found, or a document with no attributedTo)
        // degrades to the object IRI fallback — the remote fetch is best-effort: it must never fail the
        // local publish (the activity is still recorded locally and delivered to the object IRI, the
        // prior behavior).
        if (objectFetch is { } fetch)
        {
            try
            {
                var remoteObject = await fetch.GetObjectAsync(objectIri, ct).ConfigureAwait(false);
                if (remoteObject is { }
                    && remoteObject.AttributedTo is { } remoteAttributed
                    && remoteAttributed.FirstOrDefault().ResolveObjectIri() is { } remoteOwner)
                {
                    return remoteOwner;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                // A remote fetch failure is an expected condition (the object may be on an unreachable
                // instance); degrade to the object IRI fallback rather than failing the publish.
            }
        }

        return objectIri;
    }

    /// <summary>
    /// Records the local announce edge (announcer → announced-object, both directions) for an
    /// <see cref="Announce"/> published to the actor's own outbox, and returns the announced object's
    /// owner (the recipient of the server→server delivery). Mirrors <see cref="RecordLikeLocalAsync"/>
    /// for the boost: this is the durable per-object boost counter (decision 056 (d), the object's
    /// <c>shares</c> collection) for the local outbox-write path. The inbound-federation path records the
    /// same edge in <see cref="Inbox.AnnounceActivityHandler"/>; this is the local-authoring counterpart.
    /// Returns <see langword="null"/> when the announce has no resolvable object.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IAnnounceStore"/>).</param>
    /// <param name="announcerIri">The acting actor (the announcer, the outbox owner).</param>
    /// <param name="announce">The <see cref="Announce"/> activity.</param>
    /// <param name="objectFetch">The outbound object fetcher used to resolve a remote object's owner (24.1);
    /// null disables remote-object owner resolution (the delivery degrades to the object IRI).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The announced object's owner IRI (or the object IRI when the owner is not resolvable), or
    /// <see langword="null"/> when the announce has no resolvable object.</returns>
    private static async Task<Iri?> RecordAnnounceLocalAsync(
        IPersistenceProvider persistence,
        Iri announcerIri,
        Announce announce,
        IActivityPubClient? objectFetch,
        CancellationToken ct)
    {
        var objectIri = announce.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        await persistence.Announces.RecordAnnounceAsync(announcerIri, objectIri.Value, ct).ConfigureAwait(false);
        return await ResolveObjectOwnerForDeliveryAsync(persistence, objectFetch, objectIri.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the local announce edge (announcer → announced-object, both directions) for an
    /// <see cref="Undo"/> of an <see cref="Announce"/> published to the actor's own outbox, mirroring
    /// <see cref="RemoveLikeLocalAsync"/> for the boost. This is the local-authoring counterpart of the
    /// <see cref="Inbox.UndoActivityHandler"/> <c>Undo(Announce)</c> branch (the inbound path). Returns
    /// <see langword="null"/> when the announce has no resolvable object.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IAnnounceStore"/>).</param>
    /// <param name="announcerIri">The acting actor (the announcer whose edge is removed).</param>
    /// <param name="announce">The undone <see cref="Announce"/> activity.</param>
    /// <param name="objectFetch">The outbound object fetcher used to resolve a remote object's owner (24.1);
    /// null disables remote-object owner resolution (the delivery degrades to the object IRI).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The announced object's owner IRI (or the object IRI when the owner is not resolvable), or
    /// <see langword="null"/> when the announce has no resolvable object.</returns>
    private static async Task<Iri?> RemoveAnnounceLocalAsync(
        IPersistenceProvider persistence,
        Iri announcerIri,
        Announce announce,
        IActivityPubClient? objectFetch,
        CancellationToken ct)
    {
        var objectIri = announce.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        await persistence.Announces.RemoveAnnounceAsync(announcerIri, objectIri.Value, ct).ConfigureAwait(false);
        return await ResolveObjectOwnerForDeliveryAsync(persistence, objectFetch, objectIri.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the local edge removal for an <see cref="Undo"/> published to the actor's outbox and
    /// returns the original activity's target (the recipient of the server→server delivery, so the remote
    /// side removes its edge). Resolves the undone activity from the activity store (the actor stored it
    /// when it authored it): an <see cref="Undo"/> of a <see cref="Follow"/> removes the follow edge (and
    /// the community's follows/followers sets when the target is a local community); of a
    /// <see cref="Block"/> removes the block edge; of a <see cref="Flag"/> removes the flag edge. Returns
    /// <see langword="null"/> when the undone activity is not a follow/block/flag or its target is not
    /// resolvable.
    /// </summary>
    private static async Task<Iri?> RecordUndoLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri actorIri,
        Undo undo,
        IActivityPubClient? objectFetch,
        CancellationToken ct)
    {
        var referenced = undo.Object?.FirstOrDefault().ResolveObjectIri();
        if (!referenced.HasValue
            || !await persistence.Activities.TryGetActivityAsync(referenced.Value, out var stored, ct)
                .ConfigureAwait(false))
        {
            return null;
        }

        return stored switch
        {
            Follow follow => await RemoveFollowLocalAsync(persistence, localActors, actorIri, follow, ct).ConfigureAwait(false),
            Block block => await RemoveBlockLocalAsync(persistence, actorIri, block, ct).ConfigureAwait(false),
            Flag flag => await RemoveFlagLocalAsync(persistence, actorIri, flag, ct).ConfigureAwait(false),
            MuteActivity mute => await RemoveMuteLocalAsync(persistence, actorIri, mute, ct).ConfigureAwait(false),
            Like like => await RemoveLikeLocalAsync(persistence, actorIri, like, objectFetch, ct).ConfigureAwait(false),
            Announce announce => await RemoveAnnounceLocalAsync(persistence, actorIri, announce, objectFetch, ct).ConfigureAwait(false),
            _ => null,
        };
    }

    private static async Task<Iri?> RemoveLikeLocalAsync(
        IPersistenceProvider persistence,
        Iri likerIri,
        Like like,
        IActivityPubClient? objectFetch,
        CancellationToken ct)
    {
        var objectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        // The inverse of RecordLikeLocalAsync: the actor's home instance removes its own like edge
        // (the actor's `liked` collection no longer lists the object). The return value is the object's
        // owner (the remote side that receives the Undo) — resolved from the object store for a local
        // object, or by fetching the remote object's document (24.1) so the Undo reaches the object's
        // author's inbox.
        await persistence.Likes.RemoveLikeAsync(likerIri, objectIri.Value, ct).ConfigureAwait(false);
        return await ResolveObjectOwnerForDeliveryAsync(persistence, objectFetch, objectIri.Value, ct).ConfigureAwait(false);
    }

    private static async Task<Iri?> RemoveFollowLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri followerIri,
        Follow follow,
        CancellationToken ct)
    {
        var targetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!targetIri.HasValue)
        {
            return null;
        }

        // The actor's home instance removes its own follow edge regardless of whether the target is
        // local (the inverse of RecordFollowLocalAsync — the actor's `following` collection no longer
        // lists the target, local or remote).
        await persistence.Follows
            .RemoveFollowAsync(followerIri, targetIri.Value, ct)
            .ConfigureAwait(false);

        if (await persistence.Communities.TryGetCommunityAsync(targetIri.Value, out _, ct).ConfigureAwait(false))
        {
            await persistence.Communities.RemoveFollowerAsync(targetIri.Value, followerIri, ct).ConfigureAwait(false);
            await persistence.Communities.RemoveFollowAsync(targetIri.Value, followerIri, ct).ConfigureAwait(false);
        }

        return targetIri.Value;
    }

    private static async Task<Iri?> RemoveBlockLocalAsync(
        IPersistenceProvider persistence,
        Iri blockerIri,
        Block block,
        CancellationToken ct)
    {
        var blockedIri = block.Object?.FirstOrDefault().ResolveObjectIri();
        if (!blockedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RemoveBlockAsync(blockerIri, blockedIri.Value, ct).ConfigureAwait(false);
        return blockedIri.Value;
    }

    private static async Task<Iri?> RemoveFlagLocalAsync(
        IPersistenceProvider persistence,
        Iri flaggerIri,
        Flag flag,
        CancellationToken ct)
    {
        var flaggedIri = flag.Object?.FirstOrDefault().ResolveObjectIri();
        if (!flaggedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RemoveFlagAsync(flaggerIri, flaggedIri.Value, ct).ConfigureAwait(false);
        return flaggedIri.Value;
    }

    /// <summary>
    /// Removes the local mute edge for an undone <see cref="MuteActivity"/> published to the actor's
    /// outbox and returns the muted actor (the recipient of the server→server delivery, so the remote
    /// side removes its edge). The inverse of <see cref="RecordMuteLocalAsync"/>: the actor's home
    /// instance removes its own mute edge (the actor's <c>mutes</c> collection no longer lists the muted
    /// actor). Returns <see langword="null"/> when the muted actor is not resolvable. (24.2.)
    /// </summary>
    private static async Task<Iri?> RemoveMuteLocalAsync(
        IPersistenceProvider persistence,
        Iri muterIri,
        MuteActivity mute,
        CancellationToken ct)
    {
        var mutedIri = mute.Object?.FirstOrDefault().ResolveObjectIri();
        if (!mutedIri.HasValue)
        {
            return null;
        }

        await persistence.Moderation.RemoveMuteAsync(muterIri, mutedIri.Value, ct).ConfigureAwait(false);
        return mutedIri.Value;
    }

    /// <summary>
    /// Records a <see cref="Create"/> published to the actor's outbox (stores the embedded object, so it
    /// can be served by IRI and later updated/deleted) and returns the author's remote followers' delivery
    /// target (the post's federation target). A local follower already sees the post in the author's
    /// outbox, so only the author's <em>remote</em> followers need a cross-instance delivery; a single
    /// representative remote follower IRI is returned (the server delivers the post to each remote
    /// follower's inbox via the same mechanism <see cref="CreateActivityHandler"/> uses). Returns an
    /// empty sequence when the author has no remote followers (no federation hop).
    /// </summary>
    /// <remarks>
    /// When the embedded object is a community (a <see cref="Group"/> whose IRI is this instance's
    /// <c>{base}/ap/v1/c/{name}</c>), the community is additionally stored in the community store with a
    /// freshly-minted (or reused) signing key — the 19.5.1 community-creation write path. A person
    /// authors a <c>Create</c> of a <c>Group</c> to their own outbox; the server materializes the
    /// community so its document endpoint, <c>members</c>, <c>feed</c>, and collections resolve. A
    /// <c>Create</c> of any other object type (a Note, a reply, …) is unchanged.
    /// </remarks>
    private static async Task<IEnumerable<Iri>> RecordCreateLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri authorIri,
        Create create,
        string baseUrl,
        CancellationToken ct)
    {
        // Store the embedded object (so it can be served by IRI, refreshed by an Update, tombstoned by a
        // Delete) + the reply edge when the object is a reply (the inverse of CreateActivityHandler's
        // StoreEmbeddedObjectAsync).
        var embedded = create.ExtractEmbeddedObject();
        if (embedded is not null)
        {
            await persistence.Objects.PutObjectAsync(embedded, ct).ConfigureAwait(false);
            var parentIri = embedded.GetParentIri();
            var childIri = embedded.ResolveObjectIri();
            if (parentIri is { } parent && childIri is { } child)
            {
                await persistence.Replies.RecordReplyAsync(parent, child, ct).ConfigureAwait(false);
            }

            // Decision 055: record the object → Create link so a later Delete (routed through the
            // DeleteActivityHandler) can resolve this locally-posted object's originating Create by lookup
            // and remove it from the author's outbox. Mirrors CreateActivityHandler's
            // StoreEmbeddedObjectAsync (the federation path records the same index); the local-post path
            // (this method, used by PostNoteAsync) previously omitted it, so a locally-posted note's
            // Delete left its Create in the outbox. A Create with a bare-link object (no embedded object
            // id) records no link.
            var objectIri = embedded.ResolveObjectIri();
            if (objectIri is { } obj && create.Id is { } createId)
            {
                await persistence.Creates
                    .RecordAsync(obj, new Iri(createId), ct)
                    .ConfigureAwait(false);
            }

            // 19.5.1 community-creation write path: a Create whose embedded object is a community (a
            // Group whose IRI is this instance's /ap/v1/c/{name}) materializes the community in the
            // community store (document endpoint, members, feed, collections). A Group with any other
            // IRI (a remote group, a non-community group) is left as an object-store entry only.
            if (embedded is Group group && TryParseLocalCommunityIri(baseUrl, group.Id, out var communityIri))
            {
                await StoreCreatedCommunityAsync(persistence, group, communityIri, ct).ConfigureAwait(false);
            }
        }

        // The federation targets are the author's remote, non-blocked followers (a local follower sees
        // the post in the author's outbox on this instance, so it needs no cross-instance delivery).
        // Mirrors CreateActivityHandler's fan-out loop (G-1 residual).
        return await GetRemoteNonBlockedFollowersAsync(persistence, localActors, authorIri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether <paramref name="groupIri"/> is a community IRI on this instance
    /// (<c>{baseUrl}/ap/v1/c/{name}</c>), and if so returns it as an <see cref="Iri"/>. Only a
    /// Group whose IRI is a <em>local</em> community IRI (same host as <paramref name="baseUrl"/>) is
    /// materialized as a community — a Group on a foreign host is a remote object, not a local community
    /// to create.
    /// </summary>
    private static bool TryParseLocalCommunityIri(string baseUrl, string? groupIri, out Iri communityIri)
    {
        communityIri = default;
        if (string.IsNullOrWhiteSpace(groupIri))
        {
            return false;
        }

        var baseNoSlash = baseUrl.TrimEnd('/');
        var prefix = $"{baseNoSlash}{ActivityPubServerConstants.RoutePrefix}/c/";
        if (!groupIri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = groupIri[prefix.Length..];
        // The community name is a single path segment (no '/', no query/fragment).
        if (name.Length == 0 ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('#', StringComparison.Ordinal) ||
            name.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        communityIri = new Iri(groupIri);
        return true;
    }

    /// <summary>
    /// Materializes a community created by a <see cref="Create"/> of a <see cref="Group"/> (the 19.5.1
    /// creation write path): ensures the community's signing key exists (minted on first creation, reused
    /// on re-creation so an existing community is not re-keyed), stamps the <c>publicKey</c> extension on
    /// the Group document, and stores the community in the community store (so its document endpoint,
    /// <c>members</c>, <c>feed</c>, and collections resolve).
    /// </summary>
    private static async Task StoreCreatedCommunityAsync(
        IPersistenceProvider persistence,
        Group group,
        Iri communityIri,
        CancellationToken ct)
    {
        // The community's key is {communityIri}#key-1 — the same convention the seeder and the sample
        // host use. Reuse an existing key (a re-creation must not re-key a live community); mint one on
        // first creation.
        var keyId = new Iri($"{communityIri.Value}#key-1");
        ISigningKey key;
        if (!persistence.Keys.TryGetKey(keyId, out var existing))
        {
            key = KeyPairGenerator.GenerateRsa(keyId);
            persistence.Keys.PutKey(key);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(existing);
            key = existing;
        }

        // Stamp the publicKey extension (id, owner, publicKeyPem) — the form the inbound key resolver
        // reads when verifying a community-signed request (the owner is the community IRI). Bare
        // (ecosystem convention, not an iris: extension) — see ActivityPubExtensionNames.PublicKey.
        group.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        group.ExtensionData[ActivityPubExtensionNames.PublicKey] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = communityIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });

        group.Id = communityIri.Value;
        await persistence.Communities.PutCommunityAsync(group, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the <paramref name="authorIri"/> actor's remote, non-blocked followers (the federation
    /// targets for an outbound <c>Create</c> or <c>Announce</c>). A local follower already sees the
    /// content in the author's outbox on this instance, so only remote followers need a cross-instance
    /// delivery. A remote follower who has blocked the author is skipped (F-07). Returns an empty
    /// sequence when the author has no eligible remote followers.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IFollowStore"/> and
    /// <see cref="IModerationStore"/>).</param>
    /// <param name="localActors">Resolves whether a candidate follower is a local actor.</param>
    /// <param name="authorIri">The actor whose followers are enumerated.</param>
    /// <param name="ct">A cancellation token.</param>
    private static async Task<IEnumerable<Iri>> GetRemoteNonBlockedFollowersAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri authorIri,
        CancellationToken ct)
    {
        var recipients = new List<Iri>();
        var followers = await persistence.Follows.GetFollowersAsync(authorIri, ct).ConfigureAwait(false);
        foreach (var followerIri in followers)
        {
            if (await localActors.IsLocalActorAsync(followerIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (await persistence.Moderation.IsBlockedAsync(followerIri, authorIri, ct).ConfigureAwait(false))
            {
                continue;
            }

            recipients.Add(followerIri);
        }

        return recipients;
    }

    /// <summary>
    /// Delivers an outbound activity to each of the actor's subscribed relays (F-06 relay fan-out).
    /// A relay is a <c>star</c>-subscribed fan-out server (ActivityPub §5.1.3): the actor advertises the
    /// relays it subscribes to via the <c>star</c> actor property, and its content is delivered to those
    /// relays so the relays can fan it out to the wider federation. The relays are read from the
    /// <see cref="IRelayStore"/> (the actor's <c>relays</c> collection); a delivery failure for one relay
    /// does not suppress delivery to the others (each relay is an independent delivery job).
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IRelayStore"/>).</param>
    /// <param name="delivery">The delivery service (enqueues the delivery jobs).</param>
    /// <param name="actorIri">The acting actor (whose relay subscriptions are read).</param>
    /// <param name="activity">The activity to deliver to each relay.</param>
    /// <param name="ct">A cancellation token.</param>
    private static async Task DeliverToRelaysAsync(
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        Iri actorIri,
        Activity activity,
        CancellationToken ct)
    {
        var relays = await persistence.Relays.GetRelaysAsync(actorIri, ct).ConfigureAwait(false);
        foreach (var relayIri in relays)
        {
            await delivery.DeliverToActorAsync(relayIri, activity, actorIri, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rewrites the on-the-wire audience (<c>to</c>/<c>cc</c>) of an outbound <see cref="Create"/> or
    /// <see cref="Announce"/> published to a local actor's outbox so the federation document enumerates
    /// the actual audience, not just the author's composed address (19.6.5 audience metadata — the
    /// production half that change 158 scoped out). The delivery already reaches the right inboxes (the
    /// follower set, via <see cref="GetRemoteNonBlockedFollowersAsync"/>); this adds the same set to the
    /// activity's audience so a conforming receiver that reads <c>to</c>/<c>cc</c> sees the correct
    /// distribution list.
    /// </summary>
    /// <remarks>
    /// The audience split follows the ActivityStreams convention (and mirrors the inbound-federation
    /// boost convention in <see cref="AnnounceIris.BuildAnnounce"/>):
    /// <list type="bullet">
    /// <item><see cref="Announce"/> — a boost is addressed <em>to</em> each follower, carbon-copied to the
    /// announcer. So <c>to</c> = the (remote, non-blocked) follower set; <c>cc</c> = the announcer.</item>
    /// <item><see cref="Create"/> — a post's followers are the carbon-copy (public/follower) audience, so
    /// the follower set is appended to <c>cc</c>; <c>to</c> keeps the author's composed direct recipients
    /// (which for a public post is the <c>as:Public</c> sentinel, preserved). When the embedded object is a
    /// <em>reply</em> (<c>inReplyTo</c> set), the reply target — the parent note's author — is a direct
    /// recipient and is appended to <c>to</c>.</item>
    /// </list>
    /// Existing entries are preserved and the result is de-duplicated (case-insensitive, keeping the
    /// first occurrence; the <c>as:Public</c> sentinel is retained where present). The author is never
    /// added to the audience (a post is not addressed to its author). No-op for an activity that is
    /// neither a <see cref="Create"/> nor an <see cref="Announce"/>.
    /// </remarks>
    /// <param name="activity">The outbound activity to rewrite (a <see cref="Create"/> or
    /// <see cref="Announce"/>).</param>
    /// <param name="authorIri">The acting local actor (the outbox owner; the author/announcer).</param>
    /// <param name="persistence">The persistence provider (provides the followers, moderation, and object
    /// stores used to compute the audience and resolve a reply's parent author).</param>
    /// <param name="localActors">Resolves whether a candidate follower is a local actor.</param>
    /// <param name="ct">A cancellation token.</param>
    private static async Task RewriteOutboundAudienceAsync(
        Activity activity,
        Iri authorIri,
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        CancellationToken ct)
    {
        var followers = await GetRemoteNonBlockedFollowersAsync(persistence, localActors, authorIri, ct)
            .ConfigureAwait(false);

        switch (activity)
        {
            case Announce announce:
                // A boost is addressed to each follower (to) and carbon-copied to the announcer (cc),
                // mirroring AnnounceIris.BuildAnnounce for the inbound path — here at the activity level
                // for the whole follower set (the local outbox path delivers one object to all).
                announce.Cc = [new Link { Href = new Uri(authorIri.Value) }];
                announce.To = MergeAudience(announce.To, followers);
                break;

            case Create create:
                // A post's followers are the cc (public/follower) audience; to keeps the composed direct
                // recipients (as:Public for a public post) and, for a reply, gains the reply target.
                create.Cc = MergeAudience(create.Cc, followers);

                if (create.ExtractEmbeddedObject()?.GetParentIri() is { } parentIri
                    && await persistence.Objects.TryGetObjectAsync(parentIri, out var parent, ct).ConfigureAwait(false)
                    && parent is { }
                    && parent.AttributedTo?.FirstOrDefault().ResolveObjectIri() is { } parentAuthor)
                {
                    // The reply target (the parent note's author) is a direct recipient of the reply.
                    create.To = MergeAudience(create.To, [parentAuthor]);
                }
                break;
        }
    }

    /// <summary>
    /// Merges a set of audience IRIs into an existing <c>to</c>/<c>cc</c> sequence, preserving the
    /// existing entries (including the <c>as:Public</c> sentinel) and de-duplicating case-insensitively
    /// (first occurrence wins). Returns <see langword="null"/> when the result is empty.
    /// </summary>
    /// <param name="existing">The activity's current audience sequence (may be null/empty).</param>
    /// <param name="toAppend">The IRIs to append (the follower set, or a reply target).</param>
    /// <returns>The merged audience as a sequence of <see cref="Link"/>s, or null when empty.</returns>
    private static IEnumerable<ILink>? MergeAudience(IEnumerable<IObjectOrLink>? existing, IEnumerable<Iri> toAppend)
    {
        var links = new List<ILink>();
        var seen = new HashSet<Iri>(AudienceIriComparer.Instance);

        void Add(Iri? iri)
        {
            if (iri is { } value && seen.Add(value))
            {
                links.Add(new Link { Href = new Uri(value.Value) });
            }
        }

        if (existing is not null)
        {
            foreach (var entry in existing)
            {
                Add(entry.ResolveObjectIri());
            }
        }

        foreach (var iri in toAppend)
        {
            Add(iri);
        }

        return links.Count == 0 ? null : links;
    }

    /// <summary>
    /// A case-insensitive, ordinal comparer for <see cref="Iri"/> used to de-duplicate audience entries
    /// in <see cref="MergeAudience"/> (the wire form is compared by its absolute URI string, ignoring
    /// case, so a follower IRI spelled with a different host case does not duplicate).
    /// </summary>
    private sealed class AudienceIriComparer : IEqualityComparer<Iri>
    {
        public static readonly AudienceIriComparer Instance = new();
        public bool Equals(Iri x, Iri y) => string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(Iri obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
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

    /// <summary>
    /// Builds the JSON-LD <c>@context</c> for a public actor/community document: the core ActivityStreams
    /// context followed by the <c>iris:</c> namespace base declared as <c>@vocab</c>. This makes every
    /// Iris-invented extension term (written as the full IRI <c>{NamespaceIri}{term}</c>) a resolvable
    /// JSON-LD term, and is the wire-level marker that distinguishes Iris extensions from the core-AP
    /// properties (which stay bare). The namespace base is deployment-configurable
    /// (<see cref="ActivityPubServerOptions.NamespaceIri"/>); the canonical default applies when unset.
    /// </summary>
    private static ITermDefinition[] BuildDocumentContext(ActivityPubServerOptions options)
        => [
            new ReferenceTermDefinition(new Uri(ActivityStreamsContextIri)),
            new ExpandedTermDefinition { Vocab = new Uri(IrisExtensionNamespace(options)) },
        ];

    /// <summary>
    /// The core ActivityStreams JSON-LD context IRI (the document vocabulary for the standard AP terms).
    /// </summary>
    private const string ActivityStreamsContextIri = "https://www.w3.org/ns/activitystreams";

    /// <summary>
    /// The deployment's <c>iris:</c> extension namespace base IRI. When <see cref="ActivityPubServerOptions.NamespaceIri"/>
    /// is set it is used verbatim (an operator may pin a cross-instance or shared namespace). When it is unset, the
    /// namespace is <em>derived from the instance's public (advertised) base URI</em> as
    /// <c>{BaseUri}/ns#</c> (Phase 31.8) — so the namespace lives on the same host as the rest of the
    /// instance's public IRIs, is unique per instance, and the namespace document is hosted at
    /// <c>{BaseUri}/ns</c> (the <see cref="ActivityPubServerConstants.NamespaceRouteSegment"/> route). When the
    /// base URI is also unset (a degenerate host that advertises no public IRIs), the canonical default
    /// (<see cref="ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri"/>) applies. Full extension keys are
    /// <c>base + localTerm</c>.
    /// </summary>
    private static string IrisExtensionNamespace(ActivityPubServerOptions options)
    {
        if (options.NamespaceIri is { } configured)
        {
            return configured.Value;
        }

        if (options.BaseUri is { } baseUri)
        {
            return $"{baseUri.Value.TrimEnd('/')}/{ActivityPubServerConstants.NamespaceRouteSegment}#";
        }

        return ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri;
    }

    /// <summary>
    /// Builds the JSON-LD context document hosted at the deployment's <c>iris:</c> extension namespace base
    /// (the <c>GET /ns</c> endpoint, Phase 31.8). This is the document a JSON-LD processor fetches when it
    /// resolves the namespace base declared as <c>@vocab</c> in a public actor/community document: it
    /// declares the core ActivityStreams context and lists the <c>iris:</c> extension terms (the
    /// <see cref="Iris.Core.CollectionExtensionNames"/> collection endpoints and the
    /// <see cref="Iris.Core.IrisExtensionTerms"/> extensions) as properties of the vocabulary. Hosting it at
    /// the namespace base is what makes the advertised <c>iris:</c> namespace resolvable rather than a
    /// dangling IRI (the namespace base is <c>{BaseUri}/ns#</c>, so the document is served at
    /// <c>{BaseUri}/ns</c>).
    /// </summary>
    /// <param name="options">The deployment options (the namespace base is derived from them).</param>
    private static string BuildNamespaceDocument(ActivityPubServerOptions options)
    {
        // The namespace base (e.g. https://a.domain.local/ns#) is the @vocab the documents declare; the
        // term names in this context are its local part (feed, blocks, ...), each mapping to an IRI-valued
        // property (the collection endpoints and IRI extensions carry IRIs) or a plain string (the search
        // query). Declaring them here makes the advertised namespace resolvable.
        var nsBase = IrisExtensionNamespace(options).TrimEnd('#');
        var nsId = IrisExtensionNamespace(options);

        var context = new Dictionary<string, object>
        {
            ["@vocab"] = ActivityStreamsContextIri,
            [nsBase] = new Dictionary<string, object>
            {
                ["@id"] = nsId,
                [CollectionExtensionNames.Feed] = "@id",
                [CollectionExtensionNames.Blocks] = "@id",
                [CollectionExtensionNames.Flags] = "@id",
                [CollectionExtensionNames.Mutes] = "@id",
                [CollectionExtensionNames.Search] = "@id",
                [CollectionExtensionNames.Star] = "@id",
                [IrisExtensionTerms.Capabilities] = "@id",
                [IrisExtensionTerms.Settings] = "@id",
                [IrisExtensionTerms.SearchQuery] = "string",
            },
        };

        // The top-level key must be the literal "@context" (a C# anonymous-type member named @context would
        // serialize as "context" — the @ escape is stripped — so a dictionary is used for the exact key).
        var document = new Dictionary<string, object>
        {
            ["@context"] = context,
        };
        return System.Text.Json.JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// The <c>GET /ns</c> handler (Phase 31.8): serves the <c>iris:</c> extension namespace document — the
    /// JSON-LD context declared as <c>@vocab</c> in every public actor/community document. The namespace
    /// base is <c>{BaseUri}/ns#</c>, so the document is hosted at <c>{BaseUri}/ns</c> (the fragment is
    /// dropped when served). It is long-cacheable (the vocabulary is immutable for the lifetime of a
    /// deployment's base URI). No authentication: a remote JSON-LD processor fetching the context must
    /// reach it without an ActivityPub signature.
    /// </summary>
    /// <param name="optionsAccessor">The deployment options (the namespace base is derived from them).</param>
    /// <param name="context">The HTTP context (the long-cache header is set on its response).</param>
    private static IResult NamespaceDocumentHandler(
        IOptions<ActivityPubServerOptions> optionsAccessor,
        HttpContext context)
    {
        var document = BuildNamespaceDocument(optionsAccessor.Value);
        // The vocabulary is immutable per deployment base URI; let intermediates cache it for a day.
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            "max-age=86400, stale-while-revalidate=604800";
        return Results.Text(document, "application/ld+json");
    }

    /// <summary>
    /// Adds an IRI-valued extension property under the given full <c>iris:</c> key to a document's
    /// <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/>, omitting it when the value is
    /// unset or the key is already present (idempotent re-advertisement on a re-rendered document).
    /// </summary>
    private static void AddExtensionIri(
        Dictionary<string, System.Text.Json.JsonElement> ext,
        string fullKey,
        string iri)
    {
        if (string.IsNullOrWhiteSpace(iri) || ext.ContainsKey(fullKey))
        {
            return;
        }

        ext[fullKey] = System.Text.Json.JsonSerializer.SerializeToElement(iri);
    }

    private static Actor BuildActorDocument(
        Actor actor,
        Iri actorIri,
        string? authenticatedHandle,
        IPersistenceProvider persistence,
        ActivityPubServerOptions options)
    {
        // Deep-copy via serialize/deserialize so we never mutate the stored actor.
        var doc = ActivityJson.Deserialize<Actor>(ActivityJson.Serialize(actor))!;

        // Declare the JSON-LD context: the core ActivityStreams context plus the iris: namespace (@vocab),
        // so the Iris-invented collection endpoints advertised below are resolvable JSON-LD terms under the
        // deployment's namespace base (the API-surface conformance contract: every non-core-AP property is
        // namespaced; the core-AP terms stay bare).
        doc.JsonLDContext = BuildDocumentContext(options);

        // Ensure the document carries the standard collection endpoints (inbox/outbox/followers/following).
        doc.Id ??= actorIri.Value;
        doc.Inbox ??= new Link { Href = new Uri(actorIri.InboxOf().Value) };
        doc.Outbox ??= new Link { Href = new Uri(actorIri.OutboxOf().Value) };
        doc.Followers ??= new Link { Href = new Uri(actorIri.FollowersOf().Value) };
        doc.Following ??= new Link { Href = new Uri(actorIri.FollowingOf().Value) };
        // Advertise the liked collection (F-04): a remote client reads it to enumerate the objects the
        // actor has liked (the ActivityPub `Liked` relationship, served at /u/{handle}/liked). The library
        // models `liked` as a typed Actor property, so it is emitted bare (not namespaced).
        doc.Liked ??= new Link { Href = new Uri(actorIri.LikedOf().Value) };

        // Advertise the Iris-invented collection endpoints. Each is an Iris extension (not a core-AP term),
        // so it is written under the iris: namespace (the full IRI key {NamespaceIri}{term}) — see the
        // @context declared above. A client reads them via IrisDocumentExtensions.
        {
            var ext = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            var ns = IrisExtensionNamespace(options);
            // blocks (F-07 moderation): the actors the actor has blocked (served at /u/{handle}/blocks).
            AddExtensionIri(ext, ns + CollectionExtensionNames.Blocks, $"{actorIri.Value}/blocks");
            // flags (F-07 moderation): the actors the actor has flagged (served at /u/{handle}/flags).
            AddExtensionIri(ext, ns + CollectionExtensionNames.Flags, $"{actorIri.Value}/flags");
            // mutes (F-07 moderation): the actors the actor has muted (served at /u/{handle}/mutes). A mute
            // is Iris-specific (no ActivityStreams type).
            AddExtensionIri(ext, ns + CollectionExtensionNames.Mutes, $"{actorIri.Value}/mutes");
            // star (F-06): the relays (fan-out servers) the actor subscribes to (served at /u/{handle}/relays).
            // Advertised unconditionally (even when empty) so a remote instance can discover the relays a
            // local actor fans out through and relay its content to them.
            AddExtensionIri(ext, ns + CollectionExtensionNames.Star, $"{actorIri.Value}/relays");
        }

        // Advertise the local-moderation capabilities (19.0b.2b): a person can mute (F-07) and can
        // subscribe to relays (F-06) — both are Iris-specific local decisions, so they are NOT part of
        // the /ap/v1 AP tree; they are Basic-authenticated writes under the /local/v1 tree (the
        // actor's document's mutes/star collection reads stay on /ap/v1). The iris:capabilities
        // extension (Resolved Decision #11) declares these specialized, non-AP capabilities for client
        // discovery so a client can tell the actor supports mute/relay (and where to POST) without
        // guessing. The full term is {NamespaceIri}capabilities (configurable per-deployment).
        // 22.6.1: "settings" is also advertised when the actor has the manuallyApprovesFollowers gate
        // (an AP-native settings surface exists — the operator can toggle the gate via Add/Remove of
        // the actor's own document to the outbox).
        {
            var capExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            var capabilitiesTerm =
                IrisExtensionNamespace(options) + ActivityPubServerConstants.CapabilitiesTerm;
            if (!capExt.ContainsKey(capabilitiesTerm))
            {
                var hasSettings = actor.ExtensionData is { } aExt &&
                    aExt.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var mafCheck) &&
                    mafCheck.ValueKind == System.Text.Json.JsonValueKind.True;
                var capabilities = hasSettings
                    ? new[]
                    {
                        ActivityPubServerConstants.CapabilityMute,
                        ActivityPubServerConstants.CapabilityRelay,
                        ActivityPubServerConstants.CapabilitySettings,
                    }
                    : new[]
                    {
                        ActivityPubServerConstants.CapabilityMute,
                        ActivityPubServerConstants.CapabilityRelay,
                    };
                capExt[capabilitiesTerm] = System.Text.Json.JsonSerializer.SerializeToElement(capabilities);
            }
        }

        // Advertise the followed feed (F-14): a client (or another instance) reads it to get the actor's
        // home timeline (the union of the actor's local and remote follows' outbox items). The library's
        // Actor type does not model a `feed` property, so it is an Iris extension — written under the iris:
        // namespace (served at /u/{handle}/feed).
        {
            var ext = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            AddExtensionIri(ext, IrisExtensionNamespace(options) + CollectionExtensionNames.Feed, $"{actorIri.Value}/feed");
        }

        // Advertise the instance's shared inbox (F-01) when the host configured one: a remote sender may
        // POST to it instead of the actor's own inbox. The per-actor Inbox is still advertised (above), so
        // a sender that ignores endpoints.sharedInbox still lands on the right collection.
        if (options.SharedInboxIri is { } sharedInbox)
        {
            doc.Endpoints ??= new Endpoints();
            if (doc.Endpoints is Endpoints typedEndpoints)
            {
                typedEndpoints.SharedInbox ??= sharedInbox.Uri;
            }
        }

        // Echo manuallyApprovesFollowers when the host set it (the library's Actor type does not model
        // it, so it rides in ExtensionData; it must appear on the public document so a remote follower
        // can tell the follow will not be auto-accepted — J-10 / Resolved Decision #46). A false value is
        // omitted (the default is auto-accept, so it need not be spelled out).
        if (actor.ExtensionData is { } actorExt &&
            actorExt.TryGetValue(ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName, out var maf) &&
            maf.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            doc.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] = maf;

            // 22.6.1: the iris:settings extension property — the IRI of the actor's settings surface
            // (the AP-native settings change endpoint: an Add/Remove of the actor's own document
            // carrying the manuallyApprovesFollowers flag, published to the outbox). A remote client
            // can discover the settings surface from the document alone (no hardcoded endpoint paths).
            var settingsTerm =
                IrisExtensionNamespace(options) + ActivityPubServerConstants.SettingsTerm;
            if (!doc.ExtensionData.ContainsKey(settingsTerm))
            {
                doc.ExtensionData[settingsTerm] = System.Text.Json.JsonSerializer.SerializeToElement(
                    actorIri.OutboxOf().Value);
            }
        }

        // Enrich the publicKey extension with the JWK form (kty/n/e for RSA) so remote instances that
        // expect JWK (e.g. Mastodon) can resolve the key. The publicKeyPem form is preserved for
        // implementations that use it (e.g. Iris itself). (F-1912-1: Mastodon rejected our signature
        // with 401 — likely because it could not resolve the key from publicKeyPem alone.)
        if (doc.ExtensionData is { } existingExt &&
            existingExt.TryGetValue(ActivityPubExtensionNames.PublicKey, out var pkEl) &&
            pkEl.ValueKind == System.Text.Json.JsonValueKind.Object &&
            pkEl.TryGetProperty("id", out var pkIdEl) &&
            pkIdEl.ValueKind == System.Text.Json.JsonValueKind.String &&
            pkIdEl.GetString() is { } pkIdStr &&
            !string.IsNullOrWhiteSpace(pkIdStr) &&
            Iri.TryParse(pkIdStr, out var keyIri) &&
            persistence.Keys.TryGetKey(keyIri, out var pkKeyPair) &&
            pkKeyPair is not null)
        {
            var jwk = pkKeyPair.GetPublicJwk();
            var jwkEl = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jwk);
            if (jwkEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Merge the JWK fields into the publicKey object (kty, n, e for RSA; kty, crv, x, y for EC).
                var pkObj = pkEl.EnumerateObject().ToDictionary(
                    (System.Text.Json.JsonProperty p) => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var prop in jwkEl.EnumerateObject())
                {
                    pkObj[prop.Name] = prop.Value.Clone();
                }
                doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
                doc.ExtensionData[ActivityPubExtensionNames.PublicKey] = System.Text.Json.JsonSerializer.SerializeToElement(
                    pkObj.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal));
            }
        }

        // If authenticated as the owner, include the privateKey + keyAlgorithm extensions.
        if (authenticatedHandle is not null)
        {
            var ext = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            var keyIdIri = ResolveKeyIri(doc, actorIri);
            if (persistence.Keys.TryGetKey(keyIdIri, out var keyPair) && keyPair is not null)
            {
                ext[ActivityPubExtensionNames.PrivateKey] =
                    System.Text.Json.JsonSerializer.SerializeToElement(keyPair.ExportPrivateKeyPem());
                ext[ActivityPubExtensionNames.KeyAlgorithm] =
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
    /// Rewrites an outbox-published activity's <c>object</c> reference to the instance's advertised base
    /// when it points at a local actor or community reached via a different (dial) base.
    /// </summary>
    /// <remarks>
    /// Docker-only-routable IRI normalization. The authoring client dials the instance on a
    /// host-published base (e.g. <c>http://localhost:8081</c>) and carries that base in the activity's
    /// object references (a <c>Follow</c>'s target), but the instance stores its local actors under the
    /// advertised base (e.g. <c>https://iris-dev1.luit.ink</c>). Without this rewrite the local-actor
    /// check (an exact-IRI store lookup) misses the actor — the instance treats its own actor as remote
    /// and attempts a cross-instance delivery that cannot route (the dial base is not reachable from
    /// inside the instance's network). The rewrite is a no-op when the object is already on the
    /// advertised base, is not a local-actor/local-community path, or the actor/community is not local.
    /// Only the first object reference is normalized (a person/community <c>Follow</c>/<c>Block</c>/
    /// <c>Flag</c> carries exactly one target).
    /// </remarks>
    /// <param name="activity">The activity whose object reference is normalized (mutated in place).</param>
    /// <param name="baseUrl">The instance's advertised base (scheme + host + port, no trailing slash).</param>
    /// <param name="requestHost">The host the authoring client used to dial this instance (the dial base,
    /// e.g. <c>localhost:8081</c>). The target IRI's host must match either the advertised base's host or
    /// this dial host for the rewrite to apply — a remote actor on a different instance that shares a
    /// handle with a local actor is left untouched.</param>
    /// <param name="persistence">The persistence provider (the actor + community stores, consulted to
    /// confirm the target is local).</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task NormalizeLocalActorObjectIriAsync(
        Activity activity,
        string baseUrl,
        string requestHost,
        IPersistenceProvider persistence,
        CancellationToken ct)
    {
        var target = activity.Object?.FirstOrDefault().ResolveObjectIri();
        if (target is not { } targetIri)
        {
            return;
        }

        // A person-actor path (/ap/v1/u/{handle}) or a community path (/ap/v1/c/{name}) is the only
        // shape a local follow/block/flag target takes. Anything else (a Note, a remote actor on a
        // genuinely foreign host) is left untouched.
        var personPrefix = $"{ActivityPubServerConstants.RoutePrefix}/u/";
        var communityPrefix = $"{ActivityPubServerConstants.RoutePrefix}/c/";
        var path = targetIri.Value;
        Iri? localIri = null;
        if (path.Contains(personPrefix, StringComparison.Ordinal))
        {
            var handle = path[(path.IndexOf(personPrefix, StringComparison.Ordinal) + personPrefix.Length)..];
            var candidate = BuildActorIri(baseUrl, handle);
            if (await persistence.Actors.TryGetActorAsync(candidate, out _, ct).ConfigureAwait(false))
            {
                localIri = candidate;
            }
        }
        else if (path.Contains(communityPrefix, StringComparison.Ordinal))
        {
            var name = path[(path.IndexOf(communityPrefix, StringComparison.Ordinal) + communityPrefix.Length)..];
            var candidate = new Iri($"{baseUrl.TrimEnd('/')}{ActivityPubServerConstants.RoutePrefix}/c/{name}");
            if (await persistence.Communities.TryGetCommunityAsync(candidate, out _, ct).ConfigureAwait(false))
            {
                localIri = candidate;
            }
        }

        if (localIri is not { } canonical)
        {
            return;
        }

        // Rewrite only when the target is local AND its base differs from the advertised base (the
        // dial-base case). An already-canonical local IRI, or a remote target, is left as-is.
        //
        // Guard: the target IRI's host must match either the advertised base's host or the request's
        // host (the dial base). This prevents a remote actor on a different instance that shares a
        // handle with a local actor from being incorrectly rewritten to the local actor (e.g. a
        // Follow of alice@iris-dev2 must not be rewritten to alice@iris-dev1 when iris-dev1 also has
        // an alice).
        if (canonical.Value == targetIri.Value)
        {
            return;
        }

        var targetUri = new Uri(targetIri.Value);
        var baseUri = new Uri(baseUrl);
        // Compare the host part (without port) so that a dial base on a different port (e.g.
        // localhost:8081 vs localhost) still matches. The rewrite applies when the target's host is
        // the advertised base's host, the request's host (the dial base), or a local-looking host
        // (localhost / 127.0.0.1). A genuinely foreign host (a different instance's public hostname)
        // is left untouched even if the handle matches a local actor.
        var targetHostOnly = targetUri.DnsSafeHost;
        var baseHostOnly = baseUri.DnsSafeHost;
        var requestHostOnly = requestHost.Contains(':')
            ? requestHost[..requestHost.LastIndexOf(':')]
            : requestHost;
        var isLocalHost = targetHostOnly.Equals(baseHostOnly, StringComparison.OrdinalIgnoreCase)
            || targetHostOnly.Equals(requestHostOnly, StringComparison.OrdinalIgnoreCase)
            || targetHostOnly.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || targetHostOnly.Equals("127.0.0.1", StringComparison.Ordinal);
        if (!isLocalHost)
        {
            return;
        }

        activity.Object = [new Link { Href = canonical.Uri }];
    }

    /// <summary>
    /// Mints the server-authoritative id for an outbox-published activity (decision 055): assigns the
    /// activity its id (<c>{actorBase}/{activity-namespace}/{ulid}</c>) and, when the activity carries an
    /// embedded object (a <see cref="Create"/> whose object is a <see cref="Note"/>/<see cref="Group"/>,
    /// not a link), assigns that object its id too (<c>{actorBase}/{object-namespace}/{ulid}</c>).
    /// </summary>
    /// <remarks>
    /// The authoring client sends the activity shape without ids; the server is the sole authority for
    /// the id of every object/activity it creates. A reference-carrying activity (a Follow, Undo, Accept,
    /// …) carries no embedded object — only a link to an existing object — so only the activity's own id
    /// is minted. The minted ids are unguessable (ULID) and permanent.
    /// </remarks>
    /// <param name="idMinter">The id authority.</param>
    /// <param name="actorIri">The IRI of the authoring actor (the id's base).</param>
    /// <param name="activity">The deserialized activity to mint ids on (mutated in place).</param>
    private static void MintActivityIds(IdMinter idMinter, Iri actorIri, Activity activity)
    {
        // The activity's own id (the client no longer sends it).
        activity.Id = idMinter.Mint(actorIri, activity).Value;

        // A Create (or other activity) may embed a full object (a Note, a Group) whose id the client no
        // longer sends either. Mint it under the object's own namespace. A reference-carrying activity
        // (Follow/Undo/Accept/…) has only a link as its object, so there is nothing to mint here.
        if (activity is Create create && create.Object is { } objects)
        {
            // The ActivityStreams library's Create.Object returns fresh object instances on each access
            // (mutating one does not persist), so build a NEW Object collection whose embedded objects
            // carry their minted ids, and replace create.Object with it.
            //
            // Only mint an embedded object's id when the client did NOT set one. A community (a Group
            // whose id is this instance's /ap/v1/c/{name}) carries a client-chosen, meaningful IRI (its
            // name is its identity); the server preserves it rather than overwriting it with a minted
            // /groups/{ulid}. A plain Note (no client-chosen id) gets a minted /notes/{ulid}.
            var mintedItems = new List<IObjectOrLink>();
            foreach (var item in objects)
            {
                if (item is IObject embedded && string.IsNullOrWhiteSpace(embedded.Id))
                {
                    var mintedId = idMinter.Mint(actorIri, embedded).Value;
                    embedded.Id = mintedId;
                    mintedItems.Add(embedded);
                }
                else
                {
                    mintedItems.Add(item);
                }
            }
            create.Object = mintedItems;
        }
    }

    /// <summary>
    /// Reads the request body to a UTF-8 string without seeking (the request stream may not be
    /// seekable). Used by the operator reject endpoint to read the posted <c>Follow</c> activity.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The request body as a UTF-8 string (empty when the body is empty).</returns>
    private static async Task<string> ReadAsBufferedStringAsync(HttpContext context, CancellationToken ct)
        => await ReadAsBufferedStringAsync(context.Request.Body, ct).ConfigureAwait(false);

    /// <summary>
    /// Reads a (possibly non-seekable) request body stream into a buffered UTF-8 string. The signature
    /// middleware drains the body to compute the digest, so the handler cannot seek back to position 0;
    /// reading the (already-buffered) stream to a <see cref="MemoryStream"/> is safe and idempotent.
    /// </summary>
    private static async Task<string> ReadAsBufferedStringAsync(Stream body, CancellationToken ct)
    {
        // The signature middleware (EnableBuffering + CopyToAsync) drains the body and leaves the
        // stream at its end, so reset to position 0 (the buffered stream is seekable) before reading —
        // otherwise the body reads empty and the activity never deserializes.
        if (body.CanSeek && body.Position != 0)
        {
            body.Position = 0;
        }

        using var memoryStream = new MemoryStream();
        await body.CopyToAsync(memoryStream, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    /// <summary>
    /// The object-document endpoint (<c>GET /ap/v1/{**path}</c>, F-02/F-03/F-10). Serves a content
    /// object by its IRI: the <c>{**path}</c> catch-all is the object IRI's path relative to the route
    /// prefix (e.g. <c>u/alice/notes/1</c>), which is combined with the base URL to reconstruct the
    /// absolute object IRI (the object IRI IS the endpoint IRI — no serving prefix). A stored object is
    /// served as itself (the <c>Note</c> a <c>Create</c> stored, refreshed in place by an
    /// <c>Update</c>); a deleted object is served as its AS2.0
    /// <see cref="KristofferStrube.ActivityStreams.Tombstone"/> ({"type":"Tombstone","id":…,"formerType":[…]});
    /// an IRI this instance does not store 404s. This is the wire surface that makes <c>Update</c> (the
    /// object reflects the edit) and <c>Delete</c> (the object serves a tombstone, not a 404) observable.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="path">The object IRI's path relative to the route prefix (the <c>{**path}</c> catch-all).</param>
    /// <param name="persistence">The persistence provider (provides the <see cref="IObjectStore"/>).</param>
    /// <param name="optionsAccessor">The server options (provides the advertised base URL).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The object (or its tombstone) as <c>application/activity+json</c>, or <c>404</c>.</returns>
    private static async Task<IResult> ObjectDocumentHandler(
        HttpContext context,
        string path,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var normalized = baseUrl.TrimEnd('/');
        // The {**path} catch-all is the object IRI's path relative to the route prefix (e.g. the Note at
        // https://host/ap/v1/u/alice/notes/n1 is served at /ap/v1/u/alice/notes/n1, so the catch-all
        // binds u/alice/notes/n1). The object IRI IS the endpoint IRI (no serving prefix), so the IRI is
        // reconstructed as base + route prefix + path.
        //
        // F-12 replies: when the catch-all path ends in a /replies segment, the request is for the
        // parent object's replies collection, not the object itself. The parent IRI is the catch-all
        // path minus the trailing /replies (e.g. u/alice/notes/n1/replies → parent u/alice/notes/n1).
        // A catch-all route cannot be followed by another segment, so the replies surface is dispatched
        // here (the same route) rather than a separate {**path}/replies route.
        const string repliesSegment = "replies";
        var isReplies = path.EndsWith($"/{repliesSegment}", StringComparison.Ordinal)
            && path.Length > repliesSegment.Length + 1;
        if (isReplies)
        {
            var parentPath = path.Substring(0, path.Length - (repliesSegment.Length + 1));
            return await ObjectRepliesAsync(context, parentPath, persistence, normalized, ct).ConfigureAwait(false);
        }

        // Per-object interaction collections (decision 056 (d)): when the catch-all path ends in a
        // /likes or /shares segment, the request is for the object's likers / announcers (the reverse
        // indexes that back the like / boost counts), not the object itself. These are Iris-namespaced
        // extension collections (NOT core ActivityStreams Object collections — the only core one is
        // /replies): the compact terms `likes` / `shares` resolve to the Iris namespace in the
        // collection document's @context, and they are discoverable via the iris:capabilities extension
        // (the `likes` / `shares` capability values). Dispatched here (the same route) for the same
        // reason as /replies — a catch-all cannot be followed by another segment.
        const string likesSegment = "likes";
        if (path.EndsWith($"/{likesSegment}", StringComparison.Ordinal)
            && path.Length > likesSegment.Length + 1)
        {
            var parentPath = path.Substring(0, path.Length - (likesSegment.Length + 1));
            return await ObjectLikesAsync(context, parentPath, persistence, normalized, ct).ConfigureAwait(false);
        }

        const string sharesSegment = "shares";
        if (path.EndsWith($"/{sharesSegment}", StringComparison.Ordinal)
            && path.Length > sharesSegment.Length + 1)
        {
            var parentPath = path.Substring(0, path.Length - (sharesSegment.Length + 1));
            return await ObjectSharesAsync(context, parentPath, persistence, normalized, ct).ConfigureAwait(false);
        }

        var objectIri = new Iri($"{normalized}{ActivityPubServerConstants.RoutePrefix}/{path}");

        // A content object (a Note, a Link, an embedded object) is in the Objects store. A minted
        // ACTIVITY id (a Follow/Block/Flag/Like/Create the outbox publish minted, e.g.
        // /u/{handle}/blocks/{ulid}) is in the Activities store — the object-document catch-all serves
        // both, so the Object view / raw inspector can fetch a minted activity back by its IRI (the
        // 19.6.1 raw-inspector invariant: the rendered signed message is the stored activity document).
        IObject? obj = null;
        if (!await persistence.Objects.TryGetObjectAsync(objectIri, out var contentObj, ct).ConfigureAwait(false)
            || contentObj is null)
        {
            if (await persistence.Activities.TryGetActivityAsync(objectIri, out var storedActivity, ct).ConfigureAwait(false)
                && storedActivity is not null)
            {
                obj = storedActivity;
            }
        }
        else
        {
            obj = contentObj;
        }

        if (obj is null)
        {
            return Results.NotFound();
        }

        // Cache-Control: an object (or its tombstone) is a stable, addressable document; cache it like
        // the actor document (max-age=60, stale-while-revalidate=300).
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.ActorCacheControl;
        return Results.Text(ServeObjectDocument(obj, objectIri), ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serializes a stored content object for the object-document endpoint, ensuring it carries a
    /// canonical <c>url</c> (F-29): a client can offer a "view in browser" link pointing at the object's
    /// own IRI. The object's IRI IS the canonical addressable form (Iris serves the object at its IRI),
    /// so when the stored object has no <c>url</c> it is set to the object's own IRI.
    /// </summary>
    /// <remarks>
    /// The object is deep-copied (via serialize/deserialize) before mutation so the stored object is
    /// never modified — the <c>url</c> is a serving-time convenience, not stored state. An object that
    /// already carries a <c>url</c> (e.g. authored by a remote instance with a separate HTML page) keeps
    /// its author-provided value. A <see cref="KristofferStrube.ActivityStreams.Tombstone"/> (a deleted
    /// object) is served as-is — it has no <c>url</c> to surface.
    /// </remarks>
    /// <param name="obj">The stored object to serve.</param>
    /// <param name="objectIri">The object's canonical IRI (the addressable form this endpoint serves it at).</param>
    /// <returns>The object as <c>application/activity+json</c>, with a canonical <c>url</c> when absent.</returns>
    private static string ServeObjectDocument(IObject obj, Iri objectIri)
    {
        if (obj is KristofferStrube.ActivityStreams.Tombstone)
        {
            return ActivityJson.Serialize(obj);
        }

        // Deep-copy via serialize/deserialize so we never mutate the stored object (the same technique
        // the actor/community document handlers use). The concrete type is unknown (a Note, an Article,
        // a generic Object, ...), so re-serialize into the dynamic IObject and set `url` on it.
        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(obj))!;
        if (!HasCanonicalUrl(document))
        {
            document.Url = [new Link { Href = new Uri(objectIri.Value) }];
        }

        return ActivityJson.Serialize(document);
    }

    /// <summary>
    /// Returns true when the object already carries a non-empty <c>url</c> (an author-provided canonical
    /// URL that must not be overwritten).
    /// </summary>
    private static bool HasCanonicalUrl(IObject obj)
    {
        foreach (var url in obj.Url ?? [])
        {
            if (url is Link { Href: { IsAbsoluteUri: true } })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Serves the replies to a content object as a paged collection for <c>GET /ap/v1/{**path}/replies</c>
    /// (F-12). The <c>{**path}</c> catch-all is the parent object's IRI path relative to the route prefix
    /// (the same convention the object-document endpoint uses); the absolute parent IRI is reconstructed
    /// from the base URL. The items are the IRIs of the objects that reply to the parent (their
    /// <c>inReplyTo</c> is the parent's IRI), read from the <see cref="IReplyStore"/> and embedded as
    /// <see cref="Link"/>s (the same shape as the followers/following/liked collections — a client
    /// resolves a reply's full object via the object endpoint). Page 1 is an <c>OrderedCollection</c>
    /// (with <c>first</c>); page N &gt; 1 an <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/
    /// <c>next</c>), paged via <c>?page</c>/<c>?limit</c>. An object this instance does not store 404s.
    /// The response carries the collection <c>Cache-Control</c>.
    /// </summary>
    private static async Task<IResult> ObjectRepliesAsync(
        HttpContext context,
        string parentPath,
        IPersistenceProvider persistence,
        string normalizedBase,
        CancellationToken ct)
    {
        // The object IRI IS the endpoint IRI (no serving prefix), so the parent IRI is base + route
        // prefix + parent path (the same reconstruction the object-document endpoint uses).
        var parentIri = new Iri($"{normalizedBase}{ActivityPubServerConstants.RoutePrefix}/{parentPath}");

        // An object this instance does not store has no replies to serve (404, mirroring the object
        // document). The replies of a stored object are listed even when there are none (empty
        // collection).
        if (!await persistence.Objects.TryGetObjectAsync(parentIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var replyIris = await persistence.Replies.GetRepliesAsync(parentIri, ct).ConfigureAwait(false);
        var items = ActorIrisToLinks(replyIris);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());

        var collectionIri = parentIri.RepliesOf();
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        var refresh = HasRefreshBypass(context);
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the actors that like a content object (the like reverse index) as the per-object
    /// <c>likes</c> collection for <c>GET /ap/v1/{**path}/likes</c> (decision 056 (d), the per-object
    /// like counter). The items are the IRIs of the actors that liked the object, read from
    /// <see cref="ILikeStore.GetLikersAsync"/> and embedded as <see cref="Link"/>s (the same shape as the
    /// followers/following/liked/replies collections — a client resolves a liker's full actor via the
    /// actor endpoint). Unlike the paged collections, this is a <em>full, non-paged</em>
    /// <c>OrderedCollection</c>: a like/boost set is small and bounded (unlike an outbox), so the whole
    /// set is served at once and a client can read the exact count from <c>totalItems</c>.
    /// </summary>
    /// <remarks>
    /// <c>likes</c> is an <em>extension collection</em>, not a core ActivityStreams <c>Object</c>
    /// property (the only core object collection is <c>replies</c> — likes/shares are only core-AS
    /// <c>Like</c>/<c>Announce</c> activities). It is exposed under the <em>bare, non-namespaced</em>
    /// term <c>likes</c> — the same convention the wider ActivityPub ecosystem uses for object-side
    /// interaction collections — so an ecosystem client that knows the term can read the count off the
    /// object's <c>likes</c> collection uniformly for local and external objects. Per the ActivityStreams
    /// extensibility rule a strict consumer that does not know the term MUST ignore it (not error), so the
    /// bare term is safe. An object this instance does not store 404s (mirroring the object document).
    /// The wire is verbatim — the object document itself is not rewritten; the count is derived from the
    /// reverse index at read time (decision 057).
    /// </remarks>
    private static async Task<IResult> ObjectLikesAsync(
        HttpContext context,
        string parentPath,
        IPersistenceProvider persistence,
        string normalizedBase,
        CancellationToken ct)
    {
        var parentIri = new Iri($"{normalizedBase}{ActivityPubServerConstants.RoutePrefix}/{parentPath}");
        if (!await persistence.Objects.TryGetObjectAsync(parentIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var likers = await persistence.Likes.GetLikersAsync(parentIri, ct).ConfigureAwait(false);
        return BuildInteractionCollection(context, parentIri.LikesOf(), ActorIrisToLinks(likers));
    }

    /// <summary>
    /// Serves the actors that announced (boosted) a content object (the announce reverse index) as the
    /// per-object <c>shares</c> collection for <c>GET /ap/v1/{**path}/shares</c> (decision 056 (d), the
    /// per-object boost counter). Same shape and full/non-paged semantics as the <c>likes</c> surface;
    /// the items are the IRIs of the actors that announced the object, read from
    /// <see cref="IAnnounceStore.GetAnnouncersAsync"/>. An object this instance does not store 404s.
    /// </summary>
    /// <remarks>
    /// <c>shares</c> is an extension collection exposed under the <em>bare, non-namespaced</em> term
    /// <c>shares</c> — exactly like <c>likes</c> (see <see cref="ObjectLikesAsync"/>): the ecosystem
    /// convention for an object-side interaction collection, safe for strict consumers to ignore.
    /// </remarks>
    private static async Task<IResult> ObjectSharesAsync(
        HttpContext context,
        string parentPath,
        IPersistenceProvider persistence,
        string normalizedBase,
        CancellationToken ct)
    {
        var parentIri = new Iri($"{normalizedBase}{ActivityPubServerConstants.RoutePrefix}/{parentPath}");
        if (!await persistence.Objects.TryGetObjectAsync(parentIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var announcers = await persistence.Announces.GetAnnouncersAsync(parentIri, ct).ConfigureAwait(false);
        return BuildInteractionCollection(context, parentIri.SharesOf(), ActorIrisToLinks(announcers));
    }

    /// <summary>
    /// Builds a full, non-paged <c>OrderedCollection</c> document (id + <c>first</c> +
    /// <c>totalItems</c> + all items) for a per-object interaction collection (the per-object
    /// <c>likes</c> / <c>shares</c> reverse indexes) and sets the collection <c>Cache-Control</c>. The
    /// collection is served under the bare (non-namespaced) term — the ecosystem convention for
    /// object-side interaction collections — with no <c>@context</c> ceremony.
    /// </summary>
    /// <param name="context">The request context (for the cache-control header).</param>
    /// <param name="collectionIri">The collection's IRI (the object's <c>likes</c> / <c>shares</c> IRI).</param>
    /// <param name="items">The items (the actor IRIs as <see cref="Link"/>s), already resolved.</param>
    /// <returns>The collection as <c>application/activity+json</c>.</returns>
    private static IResult BuildInteractionCollection(
        HttpContext context,
        Iri collectionIri,
        IReadOnlyList<IObjectOrLink> items)
    {
        var collection = new OrderedCollection
        {
            Id = collectionIri.Value,
            Items = [.. items],
            First = new Link { Href = new Uri(collectionIri.Value) },
            TotalItems = (uint)items.Count,
        };

        var refresh = HasRefreshBypass(context);
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(ActivityJson.Serialize(collection), ActivityJson.ActivityJsonContentType);
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
        if (actor.ExtensionData is { } ext && ext.TryGetValue(ActivityPubExtensionNames.PublicKey, out var pk))
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
        KeyAlgorithm.Ed25519 => ActivityPubServerConstants.KeyAlgorithmEd25519,
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
        var accountHost = acct[(at + 1)..];
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var instanceHost = new Uri(baseUrl).Host;

        // RFC 7033: a WebFinger query for an account whose host is not this instance is answered
        // with a 404, because the account does not exist on this instance. This is the response a
        // mis-routed (forwarded-to-the-wrong-instance) query gets, and it is what tells a retrying
        // client (WebFingerClient) that this instance is not the account's home.
        if (!string.Equals(accountHost, instanceHost, StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }

        var actorIri = BuildActorIri(baseUrl, handle);

        Iri? resolvedIri = null;
        if (await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            resolvedIri = actorIri;
        }
        else
        {
            // Fallback: the handle may be a community (Group) rather than a person. Communities live
            // in the community store (not the actor store), so a community handle like
            // @iris@host resolves to {base}/ap/v1/c/iris (19.5.1 discovery).
            var communityIri = BuildCommunityIri(baseUrl, handle);
            if (await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
            {
                resolvedIri = communityIri;
            }
        }

        if (resolvedIri is null)
        {
            return Results.NotFound();
        }

        // WebFinger response: { subject, links: [{ rel: self, type: activity+json, href: resolvedIri }] }.
        // The href must be a plain string (the Iri struct serializes as an object with Uri/Value/etc.).
        var href = resolvedIri.ToString();
        var webFinger = new
        {
            subject = $"acct:{handle}@{instanceHost}",
            links = new[]
            {
                new
                {
                    rel = "self",
                    type = ActivityJson.ActivityJsonContentType,
                    href,
                },
            },
        };

        // RFC 8615 §4.1: the WebFinger response is a JRD document served as application/jrd+json
        // (not the generic application/json). The client (WebFingerClient.WebFingerContentType) already
        // expects this media type, and a spec-conformant remote client may check it.
        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(webFinger),
            "application/jrd+json");
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
                // The description is a neutral one-liner, not a repeat of the name — the explorer's
                // Instance page renders the description verbatim, so echoing the instance name there
                // produced a redundant duplicate line (29.2 visual review).
                description = "An Iris ActivityPub instance",
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
        // BaseUri.Value is the absolute wire form and ends in a slash for a bare host (e.g.
        // "https://host/"); trim it so appending the route prefix yields a single-slash link rather
        // than "https://host//ap/v1/nodeinfo/2.0".
        var trimmedBase = baseUrl.TrimEnd('/');
        var link = new
        {
            links = new[]
            {
                new
                {
                    rel = "http://nodeinfo.dpl.dev/ns/1.0/nodeinfo",
                    version = "2.0",
                    href = $"{trimmedBase}{ActivityPubServerConstants.RoutePrefix}/nodeinfo/2.0",
                },
            },
        };

        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(link),
            "application/json");
    }

    /// <summary>
    /// The <c>GET /ap/v1/health</c> handler (Phase 17.1). Runs every registered
    /// <see cref="IHealthCheck"/> and reports the aggregate status: 200 when every check is
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy"/> or
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded"/>, 503 when any
    /// check is <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy"/> (or
    /// a check faults). The body is <c>{ "status": "healthy" | "degraded" | "unhealthy", "checks": {
    /// &lt;name&gt;: { "status": "...", "description": "..." } } }</c>.
    /// </summary>
    /// <param name="checks">The registered health checks (resolved from <c>IEnumerable&lt;IHealthCheck&gt;</c>).</param>
    /// <param name="ct">The request's cancellation token.</param>
    private static async Task<IResult> HealthHandler(
        IEnumerable<IHealthCheck> checks,
        CancellationToken ct)
    {
        var perCheck = new Dictionary<string, (HealthStatus Status, string? Description, Exception? Error)>();
        var overall = HealthStatus.Healthy;

        foreach (var check in checks)
        {
            string name = check.GetType().Name;
            HealthCheckResult result;
            try
            {
                // The checks Iris registers (InstanceHealthCheck, DeliveryQueueHealthCheck) do not read the
                // context's Registration; a default context is sufficient. A host that registers its own
                // context-reading check via UseHealthChecks gets the full context from the framework's runner
                // (this Iris endpoint is the lightweight, no-runner path).
                result = await check.CheckHealthAsync(new HealthCheckContext(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the request was cancelled — let the host handle it
            }
            catch (Exception ex)
            {
                // A check that faults is treated as unhealthy (an exception is a recoverable report, not
                // a reason to 500 the health endpoint).
                perCheck[name] = (HealthStatus.Unhealthy, $"The health check threw: {ex.Message}", ex);
                overall = HealthStatus.Unhealthy;
                continue;
            }

            perCheck[name] = (result.Status, result.Description, result.Exception);
            if (result.Status == HealthStatus.Unhealthy)
            {
                overall = HealthStatus.Unhealthy;
            }
            else if (result.Status == HealthStatus.Degraded && overall == HealthStatus.Healthy)
            {
                overall = HealthStatus.Degraded;
            }
        }

        var payload = new
        {
            status = overall.ToString().ToLowerInvariant(),
            checks = perCheck
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(
                    kv => kv.Key,
                    kv => new { status = kv.Value.Status.ToString().ToLowerInvariant(), description = kv.Value.Description },
                    StringComparer.Ordinal),
        };

        var status = overall == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
        return Results.Content(
            System.Text.Json.JsonSerializer.Serialize(payload),
            "application/json",
            System.Text.Encoding.UTF8,
            status);
    }

    /// <summary>
    /// Handles GET /ap/v1/ready — the readiness probe (Phase 30.2). Reports whether the instance is ready
    /// to receive traffic (<see cref="Observability.IReadinessGate.IsReadyAsync"/>). Returns
    /// <c>200 { "ready": true }</c> when ready and <c>503 { "ready": false }</c> otherwise. No
    /// authentication (a load balancer / orchestrator probe reaches it without an ActivityPub signature).
    /// </summary>
    private static async Task<IResult> ReadyHandler(
        Observability.IReadinessGate readiness,
        CancellationToken ct)
    {
        var ready = await readiness.IsReadyAsync(ct).ConfigureAwait(false);
        var status = ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        return Results.Content(
            System.Text.Json.JsonSerializer.Serialize(new { ready }),
            "application/json",
            System.Text.Encoding.UTF8,
            status);
    }

    // --- OAuth2 token endpoints ------------------------------------------------

    /// <summary>
    /// Handles POST /ap/v1/oauth2/token — exchanges an authorization code or a refresh token for a
    /// Bearer token.
    /// <para>
    /// <c>grant_type=authorization_code</c> + <c>code</c>: redeems the code (one-time), issues a
    /// random Bearer token + a random refresh token, stores both, and returns
    /// <c>{ access_token, token_type: "bearer", refresh_token }</c>.
    /// </para>
    /// <para>
    /// <c>grant_type=refresh_token</c> + <c>refresh_token</c>: redeems the refresh token (one-time,
    /// rotation), issues a new Bearer token + a new refresh token, stores both, and returns
    /// <c>{ access_token, token_type: "bearer", refresh_token }</c>.
    /// </para>
    /// </summary>
    private static async Task<IResult> OAuthTokenHandler(
        HttpContext context,
        IOAuthTokenStore tokenStore,
        CancellationToken ct)
    {
        // Parse the form-encoded body.
        string? grantType;
        string? code;
        string? refreshToken;
        try
        {
            var form = await context.Request.ReadFormAsync(ct);
            grantType = form["grant_type"].ToString();
            code = form["code"].ToString();
            refreshToken = form["refresh_token"].ToString();
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "unsupported_media_type" });
        }

        Iri? actorIri;

        if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }

            // Redeem the code (one-time).
            var redeemed = await tokenStore.RedeemAuthorizationCodeAsync(code, ct).ConfigureAwait(false);
            if (!redeemed.HasValue)
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            actorIri = redeemed.Value;
        }
        else if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }

            // Redeem the refresh token (one-time, rotation).
            var redeemed = await tokenStore.RedeemRefreshTokenAsync(refreshToken, ct).ConfigureAwait(false);
            if (!redeemed.HasValue)
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            actorIri = redeemed.Value;
        }
        else
        {
            return Results.BadRequest(new { error = "unsupported_grant_type" });
        }

        // Issue a random Bearer token + a random refresh token and store both.
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var newRefreshToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await tokenStore.StoreTokenAsync(token, actorIri.Value, ct).ConfigureAwait(false);
        await tokenStore.StoreRefreshTokenAsync(newRefreshToken, actorIri.Value, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            access_token = token,
            token_type = "bearer",
            refresh_token = newRefreshToken,
        });
    }

    /// <summary>
    /// Handles POST /ap/v1/oauth2/revoke — revokes a Bearer token. The request body is
    /// form-encoded: <c>token</c>. The server removes the token from the <see cref="IOAuthTokenStore"/>
    /// and returns 200 (RFC 7009: always 200, even for unknown tokens, to avoid leaking token validity).
    /// </summary>
    private static async Task<IResult> OAuthRevokeHandler(
        HttpContext context,
        IOAuthTokenStore tokenStore,
        CancellationToken ct)
    {
        string? token;
        try
        {
            var form = await context.Request.ReadFormAsync(ct);
            token = form["token"].ToString();
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "unsupported_media_type" });
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Ok();
        }

        await tokenStore.RevokeTokenAsync(token, ct).ConfigureAwait(false);
        return Results.Ok();
    }

    /// <summary>
    /// Handles GET /ap/v1/oauth2/authorize — the browser-redirect half of the OAuth2
    /// authorization-code flow (RFC 6749 §4.1). The browser is redirected here by the client app
    /// with <c>?client_id</c> (the actor handle to authenticate as), <c>?redirect_uri</c> (where the
    /// authorization code is delivered), and <c>?state</c> (an opaque value echoed back to the client
    /// to prevent CSRF). The handler auto-approves (the v1 model — there is no interactive consent
    /// screen), issues a random one-time authorization code, stores it in the
    /// <see cref="IOAuthTokenStore"/> keyed by the actor IRI, and responds with a 302 redirect to
    /// <c>redirect_uri?code=...&amp;state=...</c>.
    /// <para>
    /// The authorization code is opaque (not an IRI) and is redeemed exactly once at
    /// <c>POST /ap/v1/oauth2/token</c> (the code→token exchange implemented in Phase 15.2a). The
    /// <c>state</c> parameter is required (RFC 6749 §10.12) and is echoed back verbatim.
    /// </para>
    /// </summary>
    private static async Task<IResult> OAuthAuthorizeHandler(
        HttpContext context,
        IOAuthTokenStore tokenStore,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var clientId = context.Request.Query["client_id"].ToString();
        var redirectUri = context.Request.Query["redirect_uri"].ToString();
        var state = context.Request.Query["state"].ToString();

        if (string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(redirectUri)
            || string.IsNullOrWhiteSpace(state))
        {
            return Results.BadRequest(new { error = "invalid_request", error_description = "client_id, redirect_uri, and state are required." });
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri must be an absolute URI." });
        }

        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, clientId);

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            return Results.BadRequest(new { error = "invalid_client", error_description = "Unknown actor." });
        }

        var code = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await tokenStore.StoreAuthorizationCodeAsync(code, actorIri, ct).ConfigureAwait(false);

        var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return Results.Redirect($"{redirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
    }

    // --- Inbox endpoint (owner-only, decision 056) ----------------------------

    /// <summary>
    /// Serves a local actor's <c>inbox</c> (the activities delivered TO the actor — what they received,
    /// as opposed to the outbox, what they authored) as a paged <c>OrderedCollection</c> for
    /// <c>GET /ap/v1/u/{handle}/inbox</c>. Decision 056: the inbox is private — it is served only to the
    /// owner (Basic auth via <see cref="IActorCredentialValidator"/>, the same seam that gates the
    /// owner-only <c>privateKey</c> extension) and is never cached (no-store). An unauthenticated or
    /// non-owner request is <c>403</c>; an unknown actor is <c>404</c>. Paged via <c>?page=N</c> /
    /// <c>?limit=N</c>.
    /// </summary>
    private static async Task<IResult> InboxEndpointHandler(
        string handle,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        IActorCredentialValidator credentialValidator,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // Owner-only: the inbox is the actor's private delivery surface. The requester must be the owner
        // (Basic auth matching this actor). A non-owner / unauthenticated request is 403 (the collection
        // exists but the requester may not read it). A bare 403 (not Results.Forbid, which would require
        // IAuthenticationService) so the endpoint works in any host (with or without authentication).
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var items = await persistence.Activities.GetInboxAsync(actorIri, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());
        var collectionIri = new Iri($"{actorIri}/inbox");
        var pageIri = page == 1 ? collectionIri : new Iri($"{collectionIri}/?page={page}");
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        // Private, owner-scoped data: never cached (the same no-store treatment as the owner-only actor
        // document). Intermediates and the browser must not serve a stale copy of someone's inbox.
        var result = Results.Text(document, ActivityJson.ActivityJsonContentType);
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.NoStoreCacheControl;
        return result;
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

        // Resolve the collection items (newest-first outbox; insertion-ordered followers/following/liked;
        // IRI-sorted blocks/flags/mutes, F-07; IRI-sorted relays/star, F-06).
        IReadOnlyList<IObjectOrLink> items = collectionName switch
        {
            "outbox" => await persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false),
            "followers" => ActorIrisToLinks(await persistence.Follows.GetFollowersAsync(actorIri, ct).ConfigureAwait(false)),
            "following" => ActorIrisToLinks(await persistence.Follows.GetFollowingAsync(actorIri, ct).ConfigureAwait(false)),
            "liked" => ActorIrisToLinks(await persistence.Likes.GetLikedAsync(actorIri, ct).ConfigureAwait(false)),
            "blocks" => ActorIrisToLinks(await persistence.Moderation.GetBlocksAsync(actorIri, ct).ConfigureAwait(false)),
            "flags" => ActorIrisToLinks(await persistence.Moderation.GetFlagsAsync(actorIri, ct).ConfigureAwait(false)),
            "mutes" => ActorIrisToLinks(await persistence.Moderation.GetMutesAsync(actorIri, ct).ConfigureAwait(false)),
            "relays" => ActorIrisToLinks(await persistence.Relays.GetRelaysAsync(actorIri, ct).ConfigureAwait(false)),
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
    /// Drops the cached page-1 entry for an owner's (a local actor's or community's) outbox collection
    /// page in the local collection-page response cache, so the next non-<c>?refresh</c> read re-renders
    /// with the collection's current contents.
    /// </summary>
    /// <remarks>
    /// The outbox collection page is served through the <see cref="LocalCollectionPageCache"/> (a 60s-TTL
    /// server→client response cache). An outbox write prepends to the collection (newest-first), so a
    /// write that is not paired with an invalidation leaves the cached page-1 stale: the owner's outbox
    /// card would not surface the activity it just published until the TTL lapses or a
    /// <c>?refresh=true</c> bypass is issued. The page-1 key is the bare collection IRI
    /// (<c>{owner}/outbox</c>); a newest-first insert always lands the new item at the head of the
    /// collection (page 1), so invalidating page 1 is sufficient for the primary read. Deeper pages shift
    /// when page 1 is full, but their staleness self-heals within the short TTL and the new item is never
    /// on a deeper page.
    /// </remarks>
    /// <param name="collectionCache">The local collection-page response cache.</param>
    /// <param name="ownerIri">The owning actor's or community's IRI.</param>
    private static void InvalidateLocalOutboxPage(LocalCollectionPageCache collectionCache, Iri ownerIri)
        => InvalidateLocalCollectionPage(collectionCache, ownerIri, "outbox");

    /// <summary>
    /// Drops the cached page-1 entry for any of an owner's local collection pages in the local
    /// collection-page response cache, so the next non-<c>?refresh</c> read re-renders with the
    /// collection's current contents.
    /// </summary>
    /// <remarks>
    /// Every local collection page (<c>outbox</c>, <c>blocks</c>, <c>flags</c>, <c>mutes</c>,
    /// <c>followers</c>, <c>following</c>, <c>liked</c>, <c>relays</c>, …) is served through the
    /// <see cref="LocalCollectionPageCache"/> (a 60s-TTL server→client response cache). Any local write
    /// that mutates a collection — an outbox publish, a moderation edge (block/flag/mute), or an undo of
    /// one — must be paired with an invalidation of that collection's page-1 entry. Without it the next
    /// non-<c>?refresh</c> read within the TTL returns the stale page: the owner's card would not reflect
    /// the edge it just recorded (or removed) until the TTL lapses or a <c>?refresh=true</c> bypass is
    /// issued. The page-1 key is the bare collection IRI (<c>{owner}/{collection}</c>); a change always
    /// lands in page 1's contents (an insert at the head, or a removal that shrinks the set), so
    /// invalidating page 1 is sufficient for the primary read. Deeper pages self-heal within the short
    /// TTL. Generalized from <see cref="InvalidateLocalOutboxPage"/>, which is now a thin alias for the
    /// outbox name.
    /// </remarks>
    /// <param name="collectionCache">The local collection-page response cache.</param>
    /// <param name="ownerIri">The owning actor's or community's IRI.</param>
    /// <param name="collectionName">The collection's name segment (e.g. <c>outbox</c>, <c>blocks</c>).</param>
    private static void InvalidateLocalCollectionPage(
        LocalCollectionPageCache collectionCache,
        Iri ownerIri,
        string collectionName)
        => collectionCache.Invalidate(new Iri($"{ownerIri.Value}/{collectionName}"));

    /// <summary>
    /// Serves an actor's followed feed (home timeline, F-14) as a paged collection for
    /// <c>GET /ap/v1/u/{handle}/feed</c>. The feed is the union of the actor's local and remote
    /// follows' outbox items (newest first, de-duplicated, capped by <see cref="FeedOptions"/>),
    /// computed by the <see cref="IFollowFeedService"/>. Page 1 is an <c>OrderedCollection</c> (with
    /// <c>first</c>); page N &gt; 1 is an <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>
    /// /<c>next</c>), paged via <c>?page</c>/<c>?limit</c>. Unlike the local collections, the feed is not
    /// served through the local collection-page response cache (it merges remote follows' outboxes over
    /// the wire on every request), but it still carries the collection <c>Cache-Control</c> so
    /// intermediates may cache briefly. An unknown actor 404s.
    /// </summary>
    private static async Task<IResult> FollowFeedHandler(
        string handle,
        HttpContext context,
        IPersistenceProvider persistence,
        IFollowFeedService feedService,
        IOptions<ActivityPubServerOptions> optionsAccessor,
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

        // A ?q query filters the feed to the items whose content/name matches it, case-insensitively
        // (21.4.2 — the followed feed's content filter, mirroring the community feed's F-23 ?q). An
        // empty/absent ?q returns the feed unfiltered.
        var query = context.Request.Query["q"].ToString();
        var items = await feedService.GetFeedAsync(actorIri, query.Length > 0 ? query : null, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());

        var collectionIri = new Iri($"{actorIri.Value}/feed");
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        // The feed is not served through the local collection-page response cache (it merges remote
        // follows' outboxes over the wire on every request), but it still carries the collection
        // Cache-Control so intermediates may cache briefly.
        var refresh = HasRefreshBypass(context);
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
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

        // Declare the JSON-LD context (core AS + the iris: namespace @vocab) so the Iris-invented
        // collection endpoints advertised below are resolvable JSON-LD terms (see the actor document above).
        doc.JsonLDContext = BuildDocumentContext(options);

        doc.Id ??= communityIri.Value;
        doc.Inbox ??= new Link { Href = new Uri(communityIri.InboxOf().Value) };
        doc.Outbox ??= new Link { Href = new Uri(communityIri.OutboxOf().Value) };
        doc.Followers ??= new Link { Href = new Uri(communityIri.FollowersOf().Value) };
        doc.Following ??= new Link { Href = new Uri(communityIri.FollowingOf().Value) };

        // Advertise the instance's shared inbox (F-01) when configured, so remote senders may POST to it
        // instead of the community's own inbox (mirrors the actor document above).
        if (options.SharedInboxIri is { } sharedInbox)
        {
            doc.Endpoints ??= new Endpoints();
            if (doc.Endpoints is Endpoints typedEndpoints)
            {
                typedEndpoints.SharedInbox ??= sharedInbox.Uri;
            }
        }

        var ext = doc.ExtensionData ?? new Dictionary<string, System.Text.Json.JsonElement>();
        var changed = false;
        // members is a core ActivityStreams Group term — emitted BARE (not namespaced), unlike the
        // Iris-invented collection endpoints below.
        if (!ext.ContainsKey(CollectionExtensionNames.Members))
        {
            ext[CollectionExtensionNames.Members] = System.Text.Json.JsonSerializer.SerializeToElement(
                $"{communityIri.Value}/members");
            changed = true;
        }

        // The Iris-invented collection endpoints (feed/search/blocks/flags/mutes) are written under the
        // iris: namespace (full IRI key), declared in the @context above.
        var ns = IrisExtensionNamespace(options);
        if (!ext.ContainsKey(ns + CollectionExtensionNames.Feed))
        {
            ext[ns + CollectionExtensionNames.Feed] =
                System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/feed");
            changed = true;
        }

        if (!ext.ContainsKey(ns + CollectionExtensionNames.Search))
        {
            ext[ns + CollectionExtensionNames.Search] =
                System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/search");
            changed = true;
        }

        // Advertise the community moderation collections (19.5.4) — blocks/flags/mutes (the actors the
        // community has blocked/flagged/muted), mirroring the person actor document's moderation links so
        // a client can discover the community's moderation surface.
        if (!ext.ContainsKey(ns + CollectionExtensionNames.Blocks))
        {
            ext[ns + CollectionExtensionNames.Blocks] =
                System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/blocks");
            changed = true;
        }

        if (!ext.ContainsKey(ns + CollectionExtensionNames.Flags))
        {
            ext[ns + CollectionExtensionNames.Flags] =
                System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/flags");
            changed = true;
        }

        if (!ext.ContainsKey(ns + CollectionExtensionNames.Mutes))
        {
            ext[ns + CollectionExtensionNames.Mutes] =
                System.Text.Json.JsonSerializer.SerializeToElement($"{communityIri.Value}/mutes");
            changed = true;
        }

        // The iris:capabilities extension (Resolved Decision #11) declares the community's available
        // specialized capabilities for client discovery: the specialized collections (feed/members/search)
        // plus the local-moderation mute (19.0b.2b — a community can mute a member, a non-AP local write
        // under /local/v1/c/{name}/mutes). The full term is {NamespaceIri}capabilities (configurable
        // per-deployment, Resolved Decision #9; the canonical default when unset, Resolved Decision #1).
        var capabilitiesTerm =
            IrisExtensionNamespace(options) + ActivityPubServerConstants.CapabilitiesTerm;
        if (!ext.ContainsKey(capabilitiesTerm))
        {
            // 22.6.1: advertise "settings" in the capabilities list when the community has the
            // manuallyApprovesMembers gate (an AP-native settings surface exists — the operator can
            // toggle the gate via Add/Remove of the community's own document to the outbox). A client
            // that sees "settings" in iris:capabilities knows the community has a settings surface and
            // can read the iris:settings IRI (below) to discover where to publish settings changes.
            var hasSettings = ext.TryGetValue(
                ActivityPubServerConstants.ManuallyApprovesMembersExtensionName, out var mam) &&
                mam.ValueKind == System.Text.Json.JsonValueKind.True;
            var capabilities = hasSettings
                ? new[]
                {
                    ActivityPubServerConstants.CapabilityFeed,
                    ActivityPubServerConstants.CapabilityMembers,
                    ActivityPubServerConstants.CapabilitySearch,
                    ActivityPubServerConstants.CapabilityMute,
                    ActivityPubServerConstants.CapabilitySettings,
                }
                : new[]
                {
                    ActivityPubServerConstants.CapabilityFeed,
                    ActivityPubServerConstants.CapabilityMembers,
                    ActivityPubServerConstants.CapabilitySearch,
                    ActivityPubServerConstants.CapabilityMute,
                };
            ext[capabilitiesTerm] = System.Text.Json.JsonSerializer.SerializeToElement(capabilities);
            changed = true;
        }

        // 22.6.1: the iris:settings extension property — the IRI of the community's settings surface
        // (the AP-native settings change endpoint: an Add/Remove of the community's own document
        // carrying the manuallyApprovesMembers flag, published to the outbox). When the community has
        // the manuallyApprovesMembers gate, a remote client can discover the settings surface from the
        // document alone (no hardcoded endpoint paths). The settings IRI is the community's outbox
        // (where the settings activities are published).
        if (ext.TryGetValue(
            ActivityPubServerConstants.ManuallyApprovesMembersExtensionName, out var mamExt) &&
            mamExt.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            var settingsTerm =
                IrisExtensionNamespace(options) + ActivityPubServerConstants.SettingsTerm;
            if (!ext.ContainsKey(settingsTerm))
            {
                ext[settingsTerm] = System.Text.Json.JsonSerializer.SerializeToElement(
                    communityIri.OutboxOf().Value);
                changed = true;
            }
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
    /// Shared core for the community collection endpoints (members, feed, following/followers, and the
    /// moderation collections blocks/flags/mutes): community existence check, page/limit parsing,
    /// collection-page document build, and response.
    /// </summary>
    /// <remarks>
    /// The response is served through the <see cref="LocalCollectionPageCache"/> (the server → client
    /// response cache for the paged collection endpoints) and carries the collection
    /// <c>Cache-Control</c> header; <c>?refresh=true</c> bypasses the cache for the read and emits a
    /// <c>no-cache</c> header (the value was just re-rendered — intermediates must not serve a stale
    /// copy). This mirrors the actor collection endpoint
    /// (<see cref="CollectionEndpointHandler"/>, <c>GET /u/{handle}/{collection}</c>) and the community
    /// outbox (<see cref="CommunityOutboxHandler"/>), so every Iris collection honors the same
    /// <c>Cache-Control</c> + <c>?refresh=true</c> contract. A newly-recorded item (a member post in the
    /// feed, a new member, a new follow, a new moderation edge) is therefore visible within the TTL
    /// (<c>max-age=60</c>) and immediately with <c>?refresh=true</c>.
    /// </remarks>
    private static async Task<IResult> CommunityCollectionEndpointAsync(
        string name,
        string collectionPath,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        Func<Iri, Task<IReadOnlyList<IObjectOrLink>>> fetchItems,
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

        var items = await fetchItems(communityIri).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());
        var refresh = context.Request.Query["refresh"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        var collectionIri = new Iri($"{communityIri.Value}/{collectionPath}");

        // The cache key is the page IRI, extended with the content filter (?q, the feed's F-23 filter)
        // when present: a filtered read and an unfiltered read of the same collection+page are distinct
        // entries (they render different items), so the filter must be part of the key or a ?q= read
        // would return a stale unfiltered page (or vice versa).
        var query = context.Request.Query["q"].ToString();
        var keySuffix = query.Length > 0 ? $"?q={Uri.EscapeDataString(query)}" : string.Empty;
        var pageIri = page == 1
            ? (query.Length > 0 ? new Iri($"{collectionIri}{keySuffix}") : collectionIri)
            : new Iri($"{collectionIri}/?page={page}{(query.Length > 0 ? $"&q={Uri.EscapeDataString(query)}" : string.Empty)}");

        // Read (or render on a miss) through the local collection-page response cache. A ?refresh=true
        // read bypasses the cache (re-rendering now) and still writes back a fresh entry.
        var (document, _, _) = await collectionCache.GetAsync(
            pageIri,
            refresh,
            _ => Task.FromResult<string?>(BuildCollectionPageDocument(collectionIri, page, limit, items)),
            ct).ConfigureAwait(false);

        // Cache-Control: only an explicit ?refresh=true bypass emits no-cache (the value was just
        // re-rendered). A fresh hit, a stale-while-revalidate hit, and a first render (a miss we now
        // populate) are all cacheable.
        var cacheControl = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = cacheControl;
        return Results.Text(document!, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the community's member actor IRIs as a paged collection for <c>GET /ap/v1/c/{name}/members</c>.
    /// Page 1 is an <c>OrderedCollection</c> (with <c>first</c>); page N &gt; 1 is an
    /// <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>), paged via <c>?page</c>/<c>?limit</c>.
    /// </summary>
    private static Task<IResult> CommunityMembersHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        return CommunityCollectionEndpointAsync(
            name,
            "members",
            context,
            persistence,
            optionsAccessor,
            collectionCache,
            async communityIri => ActorIrisToLinks((await persistence.Communities.GetMembersAsync(communityIri, ct).ConfigureAwait(false)).ToList()),
            ct);
    }

    /// <summary>
    /// Serves the community's unified feed as a paged collection for <c>GET /ap/v1/c/{name}/feed</c>.
    /// The feed is the union of the community's local members' outbox activities (newest first),
    /// computed by the <see cref="ICommunityFeedService"/>. Page 1 is an <c>OrderedCollection</c> (with
    /// <c>first</c>); page N &gt; 1 is an <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>),
    /// paged via <c>?page</c>/<c>?limit</c>.
    /// </summary>
    /// <remarks>
    /// A <c>?q</c> query filters the feed to the items whose content/name matches it, case-insensitively
    /// (F-23 — the feed endpoint's content filter). An empty/absent <c>?q</c> returns the feed unfiltered.
    /// The filtered and unfiltered shapes are identical (the same paged collection), so the client's
    /// <c>GetCommunityFeedAsync</c> reads both identically.
    /// </remarks>
    private static Task<IResult> CommunityFeedHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        ICommunityFeedService feedService,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        var query = context.Request.Query["q"].ToString();
        return CommunityCollectionEndpointAsync(
            name,
            "feed",
            context,
            persistence,
            optionsAccessor,
            collectionCache,
            communityIri => feedService.GetFeedAsync(communityIri, query, ct),
            ct);
    }

    /// <summary>
    /// Serves the community's outbox — the activities the local community (a <see cref="Group"/>) authors
    /// and publishes to its own outbox — as a paged collection for <c>GET /ap/v1/c/{name}/outbox</c>.
    /// This is the READ counterpart of <c>POST /ap/v1/c/{name}/outbox</c>
    /// (<see cref="CommunityOutboxPublishHandler"/>), which stores each published activity in the
    /// community's outbox (<see cref="IActivityStore.GetOutboxAsync"/>, keyed by the community IRI). The
    /// community document advertises this outbox IRI, so serving it keeps the document honest.
    /// </summary>
    /// <remarks>
    /// Mirrors the actor outbox collection endpoint (<c>GET /u/{handle}/outbox</c>) for a
    /// <see cref="Group"/>: page 1 is an <c>OrderedCollection</c> (with <c>first</c>); page N &gt; 1 is an
    /// <c>OrderedCollectionPage</c> (with <c>partOf</c>/<c>prev</c>/<c>next</c>), paged via
    /// <c>?page</c>/<c>?limit</c>, and served through the local collection-page response cache (so
    /// <c>?refresh=true</c> bypasses it and emits a <c>no-cache</c> <c>Cache-Control</c>). An unknown
    /// community 404s.
    /// </remarks>
    private static async Task<IResult> CommunityOutboxHandler(
        string name,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
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

        var items = await persistence.Activities.GetOutboxAsync(communityIri, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var page = ParsePageNumber(context.Request.Query["page"].ToString());
        var refresh = context.Request.Query["refresh"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        var collectionIri = new Iri($"{communityIri.Value}/outbox");
        var pageIri = page == 1 ? collectionIri : new Iri($"{collectionIri}/?page={page}");

        var (document, _, _) = await collectionCache.GetAsync(
            pageIri,
            refresh,
            _ => Task.FromResult<string?>(BuildCollectionPageDocument(collectionIri, page, limit, items)),
            ct).ConfigureAwait(false);

        var cacheControl = refresh
            ? ActivityPubServerConstants.NoCacheCacheControl
            : ActivityPubServerConstants.CollectionCacheControl;
        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] = cacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves a community's <c>following</c> or <c>followers</c> collection as a paged
    /// <c>OrderedCollection</c> (page 1) / <c>OrderedCollectionPage</c> (page N&gt;1) for
    /// <c>GET /ap/v1/c/{name}/{collection}</c>. Mirrors the actor collection endpoint for a
    /// <see cref="Group"/>: a community follows (and is followed by) actors and other communities the
    /// same way a person does, so it carries the same <c>following</c>/<c>followers</c> collections.
    /// </summary>
    /// <remarks>
    /// <c>following</c> is backed by the community's follows set
    /// (<see cref="ICommunityStore.GetFollowsAsync"/> — the community "follows" the follower, Resolved
    /// Decision #36); the items are the followed actors'/communities' IRIs as <c>Link</c>s.
    /// <c>followers</c> is backed by the community's followers set
    /// (<see cref="ICommunityStore.GetFollowersAsync"/> — F-24: when an actor follows a local community,
    /// the <c>FollowActivityHandler</c> records the follower in this set, so the collection lists the
    /// actors/communities that follow the community); the items are the follower IRIs as <c>Link</c>s.
    /// Both were previously the community's follows set only — the followers set was absent, so
    /// <c>followers</c> always served the empty collection (the documented J-12 asymmetry); F-24 closes
    /// that. Pagination is the shared <c>?page</c>/<c>?limit</c> shape (page 1 is the
    /// <c>OrderedCollection</c> with a self <c>first</c>; page N&gt;1 an <c>OrderedCollectionPage</c>
    /// with <c>partOf</c>/<c>prev</c>/<c>next</c>). An unknown community 404s. The response carries the
    /// collection <c>Cache-Control</c>.
    /// </remarks>
    private static Task<IResult> CommunityCollectionHandler(
        string name,
        string collection,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        return CommunityCollectionEndpointAsync(
            name,
            collection,
            context,
            persistence,
            optionsAccessor,
            collectionCache,
            async communityIri =>
            {
                // `following` = the actors/communities the community follows (the follows set);
                // `followers` = the actors/communities that follow the community (the followers set, F-24).
                var items = collection == "following"
                    ? await persistence.Communities.GetFollowsAsync(communityIri, ct).ConfigureAwait(false)
                    : await persistence.Communities.GetFollowersAsync(communityIri, ct).ConfigureAwait(false);
                return ActorIrisToLinks(items.ToList());
            },
            ct);
    }

    /// <summary>
    /// Serves a community's <c>blocks</c>, <c>flags</c>, or <c>mutes</c> collection (19.5.4 community
    /// moderation) as a paged <c>OrderedCollection</c> (page 1) / <c>OrderedCollectionPage</c> (page
    /// N&gt;1) for <c>GET /ap/v1/c/{name}/{blocks|flags|mutes}</c>. Mirrors the person moderation
    /// collections (<c>GET /u/{handle}/{blocks|flags|mutes}</c>) for a <see cref="Group"/>: a community
    /// moderates the actors whose content it surfaces in its unified feed, and the edges live in the
    /// community's own moderation sets (<see cref="ICommunityStore"/>), not the person
    /// <see cref="IModerationStore"/>. An unknown community 404s; the response carries the collection
    /// <c>Cache-Control</c>.
    /// </summary>
    private static Task<IResult> CommunityModerationCollectionHandler(
        string name,
        string collection,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        return CommunityCollectionEndpointAsync(
            name,
            collection,
            context,
            persistence,
            optionsAccessor,
            collectionCache,
            async communityIri =>
            {
                var items = collection switch
                {
                    "blocks" => await persistence.Communities.GetBlocksAsync(communityIri, ct).ConfigureAwait(false),
                    "flags" => await persistence.Communities.GetFlagsAsync(communityIri, ct).ConfigureAwait(false),
                    _ => await persistence.Communities.GetMutesAsync(communityIri, ct).ConfigureAwait(false),
                };
                return ActorIrisToLinks(items.ToList());
            },
            ct);
    }

    /// <summary>
    /// The community mute endpoint (19.5.4): a community's operator records or removes a community-scoped
    /// mute for an actor (the community hides the actor's content from its unified feed without severing
    /// the membership). The request is authenticated by Basic auth (the community's IRI is the credential
    /// seam, the same validator as the person mute endpoint and the community follow-decision endpoint).
    /// <c>?unmute=true</c> removes the mute; otherwise it records it. Both are idempotent (re-muting /
    /// un-muting a non-existent mute is a no-op) and return 204; an unknown community 404s, an
    /// unparseable target 400s, and an unauthenticated request 401s.
    /// </summary>
    /// <param name="context">The HTTP context (provides the route values and the Authorization header).</param>
    /// <param name="name">The community's name/handle (the community whose feed is being moderated).</param>
    /// <param name="credentialValidator">Validates the community's Basic-auth credentials.</param>
    /// <param name="persistence">Provides the community store (the moderation sets).</param>
    /// <param name="optionsAccessor">The server options (the instance base URI).</param>
    /// <param name="collectionCache">The local collection-page response cache (invalidated on the mute
    /// write so the community's mutes card reflects the change immediately).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>401</c> (unauthenticated), <c>404</c> (unknown community), <c>400</c> (no resolvable target), or
    /// <c>204</c> (muted/un-muted).
    /// </returns>
    private static async Task<IResult> CommunityMuteHandler(
        HttpContext context,
        string name,
        IActorCredentialValidator credentialValidator,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        LocalCollectionPageCache collectionCache,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        // The community must exist (an unknown community 404s, mirroring the other community endpoints).
        if (!await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // 1. Authenticate the requesting community (Basic auth) for this community's IRI.
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticated = await credentialValidator
            .TryValidateAsync(communityIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        // 2. Resolve the target IRI from the catch-all route value ({target} = the absolute target IRI).
        const string targetRouteKey = "target";
        if (context.Request.RouteValues[targetRouteKey] is not string targetValue
            || string.IsNullOrWhiteSpace(targetValue))
        {
            return Results.NotFound();
        }

        if (!Iri.TryParse(targetValue, out var target))
        {
            return Results.BadRequest();
        }

        // 3. Record or remove the mute edge (?unmute=true removes). The mute is idempotent (re-muting is a
        // no-op); an un-mute of a non-existent mute is also a no-op (both return 204 — the mute's steady
        // state is authoritative).
        var remove = context.Request.Query.TryGetValue("unmute", out var unmuteValues)
            && unmuteValues.Count > 0
            && string.Equals(unmuteValues[0], "true", StringComparison.OrdinalIgnoreCase);
        if (remove)
        {
            await persistence.Communities.RemoveMuteAsync(communityIri, target, ct).ConfigureAwait(false);
        }
        else
        {
            await persistence.Communities.AddMuteAsync(communityIri, target, ct).ConfigureAwait(false);
        }

        // 19.6.2 moderation-collection enumeration correctness: the community's mutes collection is
        // served through the local collection-page response cache (a 60s TTL). A mute or un-mute that is
        // not paired with an invalidation leaves the cached page-1 stale: the community's card would not
        // reflect the mute it just recorded (or removed) until the TTL lapses or a ?refresh=true bypass
        // is issued. Drop the mutes page-1 entry so the next non-?refresh read re-renders.
        InvalidateLocalCollectionPage(collectionCache, communityIri, "mutes");

        return Results.NoContent();
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
        var document = BuildSearchPageDocument(collectionIri, offset, limit, items, query, IrisExtensionNamespace(options));

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
    }

    /// <summary>
    /// Serves the instance-wide search / directory for <c>GET /ap/v1/search</c> (F-13). Searches the
    /// instance's local actors (the directory) and stored content objects case-insensitively via <c>?q</c>
    /// (an empty/whitespace query lists everything) and slices the result into the requested page
    /// (<c>?limit</c>/<c>?offset</c>, the shared limit/offset pagination shape, Resolved Decision #6). The
    /// search is computed fresh per request (like the community search — not served through the local
    /// collection-page cache), and the response carries the collection <c>Cache-Control</c> so
    /// intermediates may cache briefly.
    /// </summary>
    private static async Task<IResult> GlobalSearchHandler(
        HttpContext context,
        IGlobalSearchService searchService,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var query = context.Request.Query["q"].ToString();
        var type = context.Request.Query["type"].ToString();
        var items = await searchService.SearchAsync(query, ct, type).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var offset = ParseOffset(context.Request.Query[ActivityPubServerConstants.OffsetQueryParameterName].ToString());

        // The collection IRI is the endpoint IRI (the /ap/v1 prefix is the route prefix), so the page
        // links (?offset/?limit) are relative to it and resolve back to this route. Trim any trailing
        // slash from the base before appending the route prefix (the same convention as the community
        // search handler's BuildCommunityIri).
        var baseUrl = (options.BaseUri?.Value ?? $"{context.Request.Scheme}://{context.Request.Host}").TrimEnd('/');
        var collectionIri = new Iri($"{baseUrl}{ActivityPubServerConstants.RoutePrefix}/search");
        var document = BuildSearchPageDocument(collectionIri, offset, limit, items, query, IrisExtensionNamespace(options));

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
        IInboundRateLimiter rateLimiter,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var communityIri = BuildCommunityIri(baseUrl, name);

        var exists = await persistence.Communities.TryGetCommunityAsync(communityIri, out _, ct).ConfigureAwait(false);
        return await HandleInboxPostAsync(context, communityIri, exists, inboxProcessor, rateLimiter, ct).ConfigureAwait(false);
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
            // self-referencing `first` link. When more pages remain, it also carries a `next` pointer
            // to page 2 — without it a client that treats the OrderedCollection as the first page
            // (as the Iris client does) cannot walk past page 1. The `next` pointer is emitted as a
            // bare IRI string, the same wire shape the typed `Link` (next/prev) properties produce on
            // OrderedCollectionPage, so page 1 and page N>1 stay uniform.
            return SerializeCollectionPage(
                id: collectionIri.Value,
                type: "OrderedCollection",
                slice: slice,
                total: total,
                first: collectionIri.Value,
                partOf: null,
                startIndex: null,
                next: pageCount > 1 ? $"{collectionIri.Value}/?page=2" : null,
                prev: null);
        }

        return SerializeCollectionPage(
            id: $"{collectionIri.Value}/?page={page}",
            type: "OrderedCollectionPage",
            slice: slice,
            total: total,
            first: null,
            partOf: collectionIri.Value,
            startIndex: start,
            next: page < pageCount ? $"{collectionIri.Value}/?page={page + 1}" : null,
            prev: $"{collectionIri.Value}/?page={page - 1}");
    }

    /// <summary>
    /// Serializes one page of a local collection to JSON-LD. The top-level document is written by hand
    /// (rather than through the ActivityStreams library's <c>OrderedCollection</c>/<c>OrderedCollectionPage</c>
    /// types) because the library serializes the <c>items</c> property with its one-or-multiple
    /// converter, which collapses a single-element page into a bare JSON string. That is legal
    /// one-or-many ActivityStreams but breaks clients that read <c>items</c> as an array — and the
    /// ActivityStreams spec defines <c>OrderedCollection.items</c> as a list. Writing the envelope by
    /// hand guarantees <c>items</c> is always a JSON array (empty included). Each item is still
    /// serialized through <see cref="Iris.Core.ActivityJson"/>, so the item bytes are identical to the
    /// library output.
    /// </summary>
    /// <param name="id">The document's <c>id</c> (the collection IRI for page 1, or <c>{iri}/?page=N</c>).</param>
    /// <param name="type">The <c>type</c> term (<c>OrderedCollection</c> or <c>OrderedCollectionPage</c>).</param>
    /// <param name="slice">This page's items, in order.</param>
    /// <param name="total">The full collection size (for <c>totalItems</c>).</param>
    /// <param name="first">The <c>first</c> IRI (page 1 only; null otherwise).</param>
    /// <param name="partOf">The <c>partOf</c> IRI (page N&gt;1 only; null otherwise).</param>
    /// <param name="startIndex">The 1-based <c>startIndex</c> (page N&gt;1 only; null otherwise).</param>
    /// <param name="next">The <c>next</c> page IRI, or null when this is the last page.</param>
    /// <param name="prev">The <c>prev</c> page IRI, or null when this is page 1.</param>
    /// <returns>The serialized JSON-LD document for the page.</returns>
    private static string SerializeCollectionPage(
        string id,
        string type,
        IReadOnlyList<IObjectOrLink> slice,
        int total,
        string? first,
        string? partOf,
        int? startIndex,
        string? next,
        string? prev)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            // `items` is always a JSON array — including the single-item and empty cases.
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in slice)
            {
                // Serialize through the polymorphic IObjectOrLink type (not the concrete runtime
                // type) so a Link item renders as a bare IRI string and an object item renders as a
                // full JSON object — the same wire shape the library's one-or-multiple items
                // converter produces, just always inside an array.
                System.Text.Json.JsonSerializer.Serialize(writer, item, typeof(IObjectOrLink), ActivityJson.Options);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("totalItems");
            writer.WriteNumberValue(total);

            if (first is not null)
            {
                writer.WriteString("first", first);
            }

            if (partOf is not null)
            {
                writer.WriteString("partOf", partOf);
            }

            if (startIndex is not null)
            {
                writer.WritePropertyName("startIndex");
                writer.WriteNumberValue(startIndex.Value);
            }

            if (next is not null)
            {
                writer.WriteString("next", next);
            }

            if (prev is not null)
            {
                writer.WriteString("prev", prev);
            }

            writer.WriteString("@context", "https://www.w3.org/ns/activitystreams");
            writer.WriteString("id", id);
            writer.WriteString("type", type);

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
        var searchQueryTerm = $"{namespaceBase}{IrisExtensionTerms.SearchQuery}";
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

    /// <summary>
    /// Builds the <see cref="IDeliveryRateLimiter"/> the <see cref="DeliveryWorker"/> uses for Phase 16.3
    /// per-peer outbound-delivery rate limiting. When <see cref="DeliveryRateLimitOptions.PerPeerMaxRequestsPerMinute"/>
    /// is 0 the returned limiter is a no-op (disabled) so the default behavior is unchanged.
    /// </summary>
    private static IDeliveryRateLimiter CreateDeliveryRateLimiter(DeliveryRateLimitOptions options)
        => new SlidingWindowDeliveryRateLimiter(options.PerPeerMaxRequestsPerMinute, TimeSpan.FromMinutes(1));

    /// <summary>
    /// Creates the per-peer circuit breaker from its options (Phase 17.3). When <see
    /// cref="DeliveryCircuitBreakerOptions.FailureThreshold"/> is 0 the returned breaker is a no-op
    /// (disabled) so the default behavior is unchanged.
    /// </summary>
    private static IDeliveryCircuitBreaker CreateDeliveryCircuitBreaker(DeliveryCircuitBreakerOptions options)
        => new PerPeerDeliveryCircuitBreaker(options.FailureThreshold, options.OpenDuration);

    /// <summary>
    /// Creates the per-peer inbound rate limiter from its options (Phase 17.4). When <see
    /// cref="InboundRateLimitOptions.PerPeerMaxRequestsPerMinute"/> is 0 the returned limiter is a
    /// no-op (disabled) so the default behavior is unchanged.
    /// </summary>
    private static IInboundRateLimiter CreateInboundRateLimiter(InboundRateLimitOptions options)
        => new SlidingWindowInboundRateLimiter(options.PerPeerMaxRequestsPerMinute, TimeSpan.FromMinutes(1));

    /// <summary>
    /// Replaces the default in-memory delivery queue and dead-letter store with the persistent,
    /// file-backed implementations (Phase 16.2, production persistence): pending outbound deliveries
    /// (and dead-lettered deliveries) are journaled to disk and survive a host restart.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <param name="deliveryJournalPath">The path of the delivery-queue journal file (one JSON object per
    /// line). The directory must already exist; the file is created if it does not exist.</param>
    /// <param name="deadLetterJournalPath">The path of the dead-letter journal file. The directory must
    /// already exist; the file is created if it does not exist.</param>
    /// <param name="queueCapacity">The in-memory channel capacity (back-pressure bound). Defaults to
    /// <see cref="FileBackedDeliveryQueue.DefaultCapacity"/>.</param>
    /// <param name="deadLetterCapacity">The dead-letter store's bounded view capacity (newest-first).
    /// Defaults to <see cref="FileBackedDeliveryDeadLetterStore.DefaultCapacity"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Call this AFTER <see cref="AddActivityPubServer(IServiceCollection)"/> to override the in-memory
    /// defaults. A host
    /// that calls this gets at-least-once, restart-surviving delivery: a job is journaled (and flushed)
    /// to disk before it is handed to the in-memory channel, and on startup the journal is replayed into
    /// the channel. A job that was already delivered before a crash is re-delivered and deduped by its
    /// <c>Id</c> (C-07). Call <see cref="FileBackedDeliveryQueue.TruncateAsync"/> on a clean shutdown to
    /// keep the journal from growing without bound.
    /// </remarks>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> or a path is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When a capacity is less than or equal to 0.</exception>
    public static IServiceCollection UseFileBackedDelivery(
        this IServiceCollection services,
        string deliveryJournalPath,
        string deadLetterJournalPath,
        int queueCapacity = FileBackedDeliveryQueue.DefaultCapacity,
        int deadLetterCapacity = FileBackedDeliveryDeadLetterStore.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(deliveryJournalPath))
        {
            throw new ArgumentNullException(nameof(deliveryJournalPath));
        }

        if (string.IsNullOrWhiteSpace(deadLetterJournalPath))
        {
            throw new ArgumentNullException(nameof(deadLetterJournalPath));
        }

        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Capacity must be greater than zero.");
        }

        if (deadLetterCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deadLetterCapacity), deadLetterCapacity, "Capacity must be greater than zero.");
        }

        // Replace the in-memory defaults with the file-backed implementations.
        services.AddSingleton<IDeliveryQueue>(_ => new FileBackedDeliveryQueue(deliveryJournalPath, queueCapacity));
        services.AddSingleton<IDeliveryDeadLetterStore>(_ => new FileBackedDeliveryDeadLetterStore(deadLetterJournalPath, deadLetterCapacity));
        return services;
    }

    /// <summary>
    /// Registers the file-backed <see cref="IPersistenceProvider"/> (Phase 16.4, production persistence):
    /// every store (actors, activities, follows, likes, replies, moderation, relays, objects, communities)
    /// and the local instance's signing keys are persisted to one JSON file per store under
    /// <paramref name="directory"/> and survive a host restart.
    /// </summary>
    /// <param name="services">The service collection. Must not be null.</param>
    /// <param name="directory">The directory that holds the per-store files. It must already exist; the
    /// files are created on first write.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Call this AFTER <see cref="AddActivityPubServer(IServiceCollection)"/> to override the default
    /// (in-memory) persistence. It replaces the <see cref="IPersistenceProvider"/> and
    /// <see cref="IKeyStore"/> registrations with file-backed implementations, so a restart does not lose
    /// the federation graph (follows, likes, replies, moderation, relays), the stored actor/object/
    /// activity documents, or the local actor's signing key (a signature made before a restart still
    /// verifies after one). A host that wants a real database swaps in a different
    /// <see cref="IPersistenceProvider"/> behind the same seam.
    /// </remarks>
    /// <exception cref="ArgumentNullException">When <paramref name="services"/> or <paramref name="directory"/> is null or empty.</exception>
    public static IServiceCollection UseFileBackedPersistence(this IServiceCollection services, string directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // The directory must exist: the store files are created inside it, and a missing directory would
        // fail on first write with a confusing IOException. Fail fast at registration instead.
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The persistence directory does not exist: {directory}");
        }

        // Replace whatever persistence provider the host registered (in-memory by default) with the
        // file-backed aggregate. Re-registering IPersistenceProvider + IKeyStore overrides the
        // in-memory singletons, so the server resolves the file-backed stores.
        services.AddSingleton<IPersistenceProvider>(_ => new FileBackedPersistenceProvider(directory));
        services.AddSingleton<IKeyStore>(_ => new FileBackedKeyStore(Path.Combine(directory, "keys.json")));
        return services;
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
