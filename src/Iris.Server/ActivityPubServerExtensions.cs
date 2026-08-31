using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Observability;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using CollectionPageCache = Iris.Server.Caching.CollectionPageCache;
using WebFingerCache = Iris.Server.Caching.WebFingerCache;
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

        // The OAuth2 token store for the /ap/v1/oauth2/token + /ap/v1/oauth2/revoke endpoints
        // (Phase 15.2a). The default is in-memory; a host app replaces this with a database-backed
        // or Redis-backed store for production.
        services.TryAddSingleton<IOAuthTokenStore, InMemoryOAuthTokenStore>();

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
        services.TryAddSingleton<ICommunityFeedService, CommunityFeedService>();

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

        // Health: GET /ap/v1/health — the observability endpoint (Phase 17.1). Runs every registered
        // IHealthCheck and reports the aggregate status. 200 when every check is healthy (or degraded),
        // 503 when any check is unhealthy. The body is a JSON object { "status": "...", "checks": { name:
        // { "status": "...", "description": "..." } } }. No authentication: a load balancer / orchestrator
        // health probe must reach it without an ActivityPub signature.
        group.MapGet($"/{ActivityPubServerConstants.HealthRouteSegment}", HealthHandler);

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
        // a paged collection (page 1 is an OrderedCollection with `first`; page N>1 an OrderedCollectionPage).
        group.MapGet("/c/{name}/members", CommunityMembersHandler);

        // Community feed: GET /ap/v1/c/{name}/feed — the community's unified feed (the union of its
        // local members' outbox activities, newest first), served as a paged collection.
        group.MapGet("/c/{name}/feed", CommunityFeedHandler);

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
                    CancellationToken ct)
                    => CommunityCollectionHandler(name, collection, context, persistence, optionsAccessor, ct))
            .WithName("community-collection-endpoint");

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

        // Local moderation (mute): POST /ap/v1/u/{handle}/mutes/{target} — a local actor records a mute
        // (F-07); the same route with ?unmute=true removes it. A mute is Iris-specific (no
        // ActivityStreams type) and is a local moderation decision, so it is not interpreted from a
        // federated activity: the endpoint authenticates the requesting actor by Basic auth
        // (IActorCredentialValidator — the same credential seam as the actor document's owner-only
        // extension and the proxy endpoint) and records/removes the mute edge in the moderation store.
        // {target} is a catch-all of the absolute IRI of the actor being muted.
        group.MapPost("/u/{handle}/mutes/{**target}", LocalMuteHandler).WithName("local-mute-endpoint");

        // Local relay subscription: POST /ap/v1/u/{handle}/relays/{target} — a local actor subscribes to
        // a relay (F-06); the same route with ?unsubscribe=true removes the subscription. A relay
        // subscription is an Iris-specific local decision (a local actor configures the relays it wants
        // to fan out through), so it is not interpreted from a federated activity: the endpoint
        // authenticates the requesting actor by Basic auth (IActorCredentialValidator — the same
        // credential seam as the mute endpoint) and records/removes the relay edge in the relay store.
        // {target} is a catch-all of the absolute IRI of the relay being subscribed to.
        group.MapPost("/u/{handle}/relays/{**target}", LocalRelayHandler).WithName("local-relay-endpoint");

        // Operator follow-rejection: POST /ap/v1/u/{handle}/follows/{followId} — a local actor's operator
        // (or the acting actor's own client) rejects a follow that a remote actor made of this local actor.
        // This is the live outbound side of the manually-approves-followers gate (J-10 / gap G-2's Reject
        // half): when a local person has manuallyApprovesFollowers set, an inbound Follow records the
        // provisional follow edge but schedules NO Accept — the operator must respond with an explicit
        // Accept or Reject, and this endpoint is that Reject path. The endpoint authenticates the
        // requesting actor by Basic auth (the same credential seam as the mute/relay endpoints), builds
        // the deterministic Reject (FollowIris.BuildReject, actor = the local actor, object = the
        // original follow by IRI), records it in the local activity store + the local actor's outbox
        // (so it is inspectable and idempotent), removes the provisional local follow edge (follower →
        // this actor), and server-delivers the Reject to the follower's inbox (signed as the local actor),
        // so the remote follower's RejectActivityHandler removes its own edge. {followId} is a catch-all
        // of the absolute IRI of the original Follow activity.
        group.MapPost("/u/{handle}/follows/{**followId}", LocalFollowRejectHandler).WithName("local-follow-reject-endpoint");

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

        if (!Iri.TryParse(targetValue, out var target))
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

        return Results.NoContent();
    }

    /// <summary>
    /// The operator follow-rejection endpoint: a local actor's operator rejects a follow that a remote
    /// actor made of this local actor (the live outbound side of the manually-approves-followers gate,
    /// J-10 / gap G-2's Reject half). The request is authenticated by Basic auth (the acting actor's
    /// credentials, the same seam as the mute/relay endpoints); the body is the original
    /// <see cref="Follow"/> activity (its <c>id</c> is the follow the operator is rejecting). The handler
    /// builds the deterministic <see cref="Reject"/> (<see cref="FollowIris.BuildReject"/>, actor = the
    /// local actor, object = the original follow by IRI), records it in the local activity store + the
    /// local actor's outbox (so it is inspectable and a re-reject is idempotent), removes the provisional
    /// local follow edge (follower → local actor), and server-delivers the Reject to the follower's inbox
    /// (signed as the local actor) so the remote follower's <see cref="Inbox.RejectActivityHandler"/>
    /// removes its own edge.
    /// </summary>
    /// <param name="context">The HTTP context (provides the route values, the Authorization header, and
    /// the request body).</param>
    /// <param name="handle">The local actor's handle (the followed actor being managed).</param>
    /// <param name="credentialValidator">Validates the acting actor's Basic-auth credentials.</param>
    /// <param name="persistence">Provides the activity + follow stores.</param>
    /// <param name="delivery">Schedules the signed <c>Reject</c> to the follower's inbox.</param>
    /// <param name="localActors">Resolves whether an actor IRI is local.</param>
    /// <param name="optionsAccessor">The server options (the instance base URI).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>401</c> (unauthenticated), <c>404</c> (unknown local actor), <c>400</c> (malformed / no-follow
    /// body or a non-Follow), <c>409</c> (the follow's target is not this local actor), <c>410</c> (the
    /// follow is unknown to this instance — neither recorded nor already rejected), <c>403</c> (the
    /// follow is local, not a remote follow), or <c>202</c> (rejected + the <c>Reject</c> scheduled for
    /// delivery).
    /// </returns>
    /// <remarks>
    /// A reject is idempotent at the activity level: the <c>Reject</c> carries the deterministic IRI
    /// <c>{localActor}/rejects/{followId}</c> (see <see cref="FollowIris.RejectIri(Iri, Follow)"/>), so a
    /// re-reject of the same follow stores the same activity (a no-op) and re-schedules the delivery (the
    /// remote dedupes on the IRI). The local edge removal is also a no-op once already removed. A re-reject
    /// is therefore accepted (<c>202</c>) when the follow was already rejected (the Reject is recorded
    /// under its deterministic IRI), whereas a follow that was never recorded is <c>410 Gone</c>.
    /// </remarks>
    private static async Task<IResult> LocalFollowRejectHandler(
        HttpContext context,
        string handle,
        IActorCredentialValidator credentialValidator,
        IPersistenceProvider persistence,
        IDeliveryService delivery,
        ILocalActorResolver localActors,
        IOptions<ActivityPubServerOptions> optionsAccessor,
        CancellationToken ct)
    {
        var options = optionsAccessor.Value;
        var baseUrl = options.BaseUri?.Value
            ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var actorIri = BuildActorIri(baseUrl, handle);

        // 1. Authenticate the acting actor (Basic auth) for this actor's IRI (the owner-only seam).
        var authorization = context.Request.Headers.Authorization.ToString();
        var authenticatedHandle = await credentialValidator
            .TryValidateAsync(actorIri, authorization, ct)
            .ConfigureAwait(false);
        if (authenticatedHandle is null)
        {
            return Results.Unauthorized();
        }

        if (!await persistence.Actors.TryGetActorAsync(actorIri, out _, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        // 2. Read the original Follow from the body. The operator posts the follow activity it is
        // rejecting (its id is the follow IRI); the handler reconstructs the deterministic Reject from it.
        // Read as a string (buffered) rather than seeking the request stream, which may not be seekable.
        var json = await ReadAsBufferedStringAsync(context, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Results.BadRequest();
        }

        IObjectOrLink? payload = ActivityJson.Deserialize<IObjectOrLink>(json);
        if (payload is not Follow { Id: not null } follow)
        {
            return Results.BadRequest();
        }

        // 3. The follow's target must be this local actor (the actor being managed). A reject is always
        // the followed side's decision about a follow made OF that actor.
        var followTargetIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
        if (!followTargetIri.HasValue || followTargetIri.Value != actorIri)
        {
            return Results.Conflict();
        }

        // 4. The follow must be a remote actor's follow of this local actor: the follower is a remote
        // actor (a local follow is not rejected here — a local un-follow is an Undo, not a Reject). The
        // follow must be a known follow of this actor — either the provisional follow edge (follower →
        // this actor) is recorded, or this actor has already recorded the Reject for this exact follow
        // (a re-reject, which is idempotent). A follow that is neither recorded nor already rejected is
        // unknown to this instance → 410 Gone.
        var followerIri = follow.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!followerIri.HasValue)
        {
            return Results.BadRequest();
        }

        if (await localActors.IsLocalActorAsync(followerIri.Value, ct).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var edgePresent =
            await persistence.Follows.IsFollowingAsync(followerIri.Value, actorIri, ct).ConfigureAwait(false);
        var rejectIri = FollowIris.RejectIri(actorIri, follow);
        var alreadyRejected =
            await persistence.Activities.TryGetActivityAsync(rejectIri, out _).ConfigureAwait(false);
        if (!edgePresent && !alreadyRejected)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        // 5. Build + record the deterministic Reject (actor = this local actor, object = the original
        // follow by IRI) in the activity store + the local actor's outbox (so it is inspectable and a
        // re-reject stores the same IRI — a no-op).
        var reject = FollowIris.BuildReject(actorIri, follow);
        await persistence.Activities.PutActivityAsync(reject, ct).ConfigureAwait(false);
        await persistence.Activities.AddToOutboxAsync(actorIri, reject, ct).ConfigureAwait(false);

        // 6. Remove the provisional local follow edge (follower → this actor; a no-op if already
        // removed) and server-deliver the Reject to the follower's inbox (signed as this local actor)
        // so the remote removes its own edge.
        await persistence.Follows.RemoveFollowAsync(followerIri.Value, actorIri, ct).ConfigureAwait(false);
        await delivery.DeliverToActorAsync(followerIri.Value, reject, actorIri, ct).ConfigureAwait(false);

        return Results.Accepted();
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
        if (payload is not Activity { Id: not null } activity)
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
        if (payload is not Activity { Id: not null } activity)
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

        try
        {
            // 1. Record the activity in the actor's outbox (surfaces it in the actor's feed / outbox
            //    collection) and the activity store (for Undo resolution + the remote side to look it up).
            await persistence.Activities.AddToOutboxAsync(actorIri, activity, ct).ConfigureAwait(false);
            await persistence.Activities.PutActivityAsync(activity, ct).ConfigureAwait(false);

            // 2. Record the local edge the activity implies + resolve the recipient(s) for the
            //    server→server delivery hop (the client never enumerates recipients — that is the
            //    server's job).
            if (activity is Create create)
            {
                // A Create fans out to every remote, non-blocked follower (G-1 residual: the full
                // fan-out, mirroring CreateActivityHandler's loop, not just the first follower).
                var recipients = await RecordCreateLocalAsync(persistence, localActors, actorIri, create, ct)
                    .ConfigureAwait(false);
                foreach (var recipient in recipients)
                {
                    await delivery.DeliverToActorAsync(recipient, activity, actorIri, ct).ConfigureAwait(false);
                }
            }
            else if (activity is Announce)
            {
                // An Announce (boost/repost) fans out to every remote, non-blocked follower, mirroring
                // the Create branch (F-15: outbound Announce federation). Unlike a Create, an Announce
                // carries no embedded object — it is a reference to an existing object IRI — so no
                // object-store write is needed.
                var recipients = await GetRemoteNonBlockedFollowersAsync(persistence, localActors, actorIri, ct)
                    .ConfigureAwait(false);
                foreach (var recipient in recipients)
                {
                    await delivery.DeliverToActorAsync(recipient, activity, actorIri, ct).ConfigureAwait(false);
                }
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
            else
            {
                Iri? recipientIri = activity switch
                {
                    Follow follow => await RecordFollowLocalAsync(persistence, localActors, actorIri, follow, ct).ConfigureAwait(false),
                    Block block => await RecordBlockLocalAsync(persistence, localActors, actorIri, block, ct).ConfigureAwait(false),
                    Flag flag => await RecordFlagLocalAsync(persistence, localActors, actorIri, flag, ct).ConfigureAwait(false),
                    Like like => await RecordLikeLocalAsync(persistence, actorIri, like, ct).ConfigureAwait(false),
                    Undo undo => await RecordUndoLocalAsync(persistence, localActors, actorIri, undo, ct).ConfigureAwait(false),
                    _ => null,
                };

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
        catch (Exception)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted();
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
    /// <param name="optionsAccessor">The server options (the base URL, for the community IRI).</param>
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

        try
        {
            Iri? recipientIri = payload switch
            {
                Follow follow => await RecordCommunityFollowAsync(persistence, communityIri, follow, ct)
                    .ConfigureAwait(false),
                Undo undo => await RecordCommunityUnfollowAsync(persistence, communityIri, undo, ct)
                    .ConfigureAwait(false),
                _ => null,
            };
            if (recipientIri is null)
            {
                return Results.BadRequest();
            }

            // Record the activity in the community's outbox (so the `following` collection surfaces the
            // edge) and the activity store (for Undo resolution + the remote side to look it up). The
            // payload is a Follow/Undo (a non-null Activity) — recipientIri is only non-null in that case.
            ArgumentNullException.ThrowIfNull(payload);
            await persistence.Activities.AddToOutboxAsync(communityIri, payload, ct).ConfigureAwait(false);
            await persistence.Activities.PutActivityAsync((IObject)payload, ct).ConfigureAwait(false);

            // The server (not the client) delivers the activity to the target's inbox. A local target
            // needs no cross-instance hop (the local edge is already recorded); only a remote target is
            // delivered to, signed as the community.
            if (recipientIri is { } recipient
                && !await IsLocalCommunityAsync(persistence, recipient, ct).ConfigureAwait(false)
                && !await IsLocalActorAsync(persistence, recipient, ct).ConfigureAwait(false))
            {
                await delivery.DeliverToActorAsync(recipient, (Activity)payload, communityIri, ct).ConfigureAwait(false);
            }
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
    /// Records the local like edge for a <see cref="Like"/> published to the actor's outbox and returns
    /// the object's owner (the recipient of the server→server delivery). The liker is the acting local
    /// actor, so the edge (liker → object) is always recorded in the liker's <c>liked</c> collection. The
    /// owner is the object's <c>attributedTo</c> (fetched from the object store when the object is a
    /// local object); a remote object's owner is resolved by the remote instance on receipt. Returns
    /// <see langword="null"/> when the owner is not resolvable.
    /// </summary>
    private static async Task<Iri?> RecordLikeLocalAsync(
        IPersistenceProvider persistence,
        Iri likerIri,
        Like like,
        CancellationToken ct)
    {
        var objectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        await persistence.Likes.RecordLikeAsync(likerIri, objectIri.Value, ct).ConfigureAwait(false);

        // The owner is the object's attributedTo. A local object is in the object store; a remote
        // object's owner is resolved by the remote instance (the delivery still goes to the object IRI,
        // whose instance routes it to the owner).
        if (await persistence.Objects.TryGetObjectAsync(objectIri.Value, out var storedObject, ct)
            .ConfigureAwait(false) &&
            storedObject is { } &&
            storedObject.AttributedTo is { } attributed &&
            attributed.FirstOrDefault().ResolveObjectIri() is { } ownerIri)
        {
            return ownerIri;
        }

        return objectIri.Value;
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
            Like like => await RemoveLikeLocalAsync(persistence, actorIri, like, ct).ConfigureAwait(false),
            _ => null,
        };
    }

    private static async Task<Iri?> RemoveLikeLocalAsync(
        IPersistenceProvider persistence,
        Iri likerIri,
        Like like,
        CancellationToken ct)
    {
        var objectIri = like.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return null;
        }

        // The inverse of RecordLikeLocalAsync: the actor's home instance removes its own like edge
        // (the actor's `liked` collection no longer lists the object). The return value is the object's
        // owner (the remote side that must remove its like edge) when the object is local; otherwise the
        // object IRI (whose instance routes the delivery to the owner) — mirroring RecordLikeLocalAsync.
        await persistence.Likes.RemoveLikeAsync(likerIri, objectIri.Value, ct).ConfigureAwait(false);

        if (await persistence.Objects.TryGetObjectAsync(objectIri.Value, out var storedObject, ct)
                .ConfigureAwait(false) &&
            storedObject is { } &&
            storedObject.AttributedTo is { } attributed &&
            attributed.FirstOrDefault().ResolveObjectIri() is { } ownerIri)
        {
            return ownerIri;
        }

        return objectIri.Value;
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
    /// Records a <see cref="Create"/> published to the actor's outbox (stores the embedded object, so it
    /// can be served by IRI and later updated/deleted) and returns the actor's remote followers' delivery
    /// target (the post's federation target). A local follower already sees the post in the author's
    /// outbox, so only the author's <em>remote</em> followers need a cross-instance delivery; a single
    /// representative remote follower IRI is returned (the server delivers the post to each remote
    /// follower's inbox via the same mechanism <see cref="CreateActivityHandler"/> uses). Returns
    /// <see langword="null"/> when the author has no remote followers (no federation hop).
    /// </summary>
    private static async Task<IEnumerable<Iri>> RecordCreateLocalAsync(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        Iri authorIri,
        Create create,
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
        }

        // The federation targets are the author's remote, non-blocked followers (a local follower sees
        // the post in the author's outbox on this instance, so it needs no cross-instance delivery).
        // Mirrors CreateActivityHandler's fan-out loop (G-1 residual).
        return await GetRemoteNonBlockedFollowersAsync(persistence, localActors, authorIri, ct).ConfigureAwait(false);
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
        // Advertise the liked collection (F-04): a remote client reads it to enumerate the objects the
        // actor has liked (the ActivityPub `Liked` relationship, served at /u/{handle}/liked).
        doc.Liked ??= new Link { Href = new Uri(actorIri.LikedOf().Value) };

        // Advertise the blocks collection (F-07 moderation): a client (or another instance) reads it to
        // enumerate the actors the actor has blocked (served at /u/{handle}/blocks). The library's Actor
        // type does not model a `blocks` property, so it rides in ExtensionData (the same wire shape the
        // `feed` extension uses, served at /u/{handle}/feed).
        {
            var blocksExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            if (!blocksExt.ContainsKey("blocks"))
            {
                blocksExt["blocks"] = System.Text.Json.JsonSerializer.SerializeToElement($"{actorIri.Value}/blocks");
            }
        }

        // Advertise the flags collection (F-07 moderation): a client (or another instance) reads it to
        // enumerate the actors the actor has flagged (served at /u/{handle}/flags). The library's Actor
        // type does not model a `flags` property, so it rides in ExtensionData (the same wire shape the
        // `blocks` extension uses).
        {
            var flagsExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            if (!flagsExt.ContainsKey("flags"))
            {
                flagsExt["flags"] = System.Text.Json.JsonSerializer.SerializeToElement($"{actorIri.Value}/flags");
            }
        }

        // Advertise the mutes collection (F-07 moderation): a client reads it to enumerate the actors
        // the actor has muted (served at /u/{handle}/mutes). A mute is Iris-specific (no ActivityStreams
        // type), so — like `blocks`/`flags` — it rides in ExtensionData (the same wire shape).
        {
            var mutesExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            if (!mutesExt.ContainsKey("mutes"))
            {
                mutesExt["mutes"] = System.Text.Json.JsonSerializer.SerializeToElement($"{actorIri.Value}/mutes");
            }
        }

        // Advertise the relays collection (F-06): the relays (fan-out servers) the actor subscribes to —
        // the ActivityPub `star` set (AP §5.1.3). A relay subscription is an Iris-specific local decision
        // (a local actor configures the relays it fanned-out through), so — like `blocks`/`flags`/`mutes`
        // — it rides in ExtensionData (the same wire shape), served at /u/{handle}/relays. It is
        // advertised unconditionally (even when empty) so a remote instance can discover the relays a
        // local actor fans out through and relay its content to them.
        {
            var relaysExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            if (!relaysExt.ContainsKey("star"))
            {
                relaysExt["star"] = System.Text.Json.JsonSerializer.SerializeToElement($"{actorIri.Value}/relays");
            }
        }

        // Advertise the followed feed (F-14): a client (or another instance) reads it to get the actor's
        // home timeline (the union of the actor's local and remote follows' outbox items). The library's
        // Actor type does not model a `feed` property, so it rides in ExtensionData (the same wire shape
        // the community document uses for its `feed` extension, served at /u/{handle}/feed).
        {
            var feedExt = doc.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
            if (!feedExt.ContainsKey("feed"))
            {
                feedExt["feed"] = System.Text.Json.JsonSerializer.SerializeToElement($"{actorIri.Value}/feed");
            }
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
        }

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

        var objectIri = new Iri($"{normalized}{ActivityPubServerConstants.RoutePrefix}/{path}");

        if (!await persistence.Objects.TryGetObjectAsync(objectIri, out var obj, ct).ConfigureAwait(false) ||
            obj is null)
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

        var items = await feedService.GetFeedAsync(actorIri, ct).ConfigureAwait(false);

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
    /// Shared core for the community collection endpoints (members, feed, following/followers):
    /// community existence check, page/limit parsing, collection-page document build, and response.
    /// </summary>
    private static async Task<IResult> CommunityCollectionEndpointAsync(
        string name,
        string collectionPath,
        HttpContext context,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> optionsAccessor,
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

        var collectionIri = new Iri($"{communityIri.Value}/{collectionPath}");
        var document = BuildCollectionPageDocument(collectionIri, page, limit, items);

        context.Response.Headers[ActivityPubServerConstants.CacheControlHeaderName] =
            ActivityPubServerConstants.CollectionCacheControl;
        return Results.Text(document, ActivityJson.ActivityJsonContentType);
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
        CancellationToken ct)
    {
        return CommunityCollectionEndpointAsync(
            name,
            "members",
            context,
            persistence,
            optionsAccessor,
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
        CancellationToken ct)
    {
        var query = context.Request.Query["q"].ToString();
        return CommunityCollectionEndpointAsync(
            name,
            "feed",
            context,
            persistence,
            optionsAccessor,
            communityIri => feedService.GetFeedAsync(communityIri, query, ct),
            ct);
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
        CancellationToken ct)
    {
        return CommunityCollectionEndpointAsync(
            name,
            collection,
            context,
            persistence,
            optionsAccessor,
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
        var items = await searchService.SearchAsync(query, ct).ConfigureAwait(false);

        var limit = ParsePageSize(context.Request.Query["limit"].ToString());
        var offset = ParseOffset(context.Request.Query[ActivityPubServerConstants.OffsetQueryParameterName].ToString());

        // The collection IRI is the endpoint IRI (the /ap/v1 prefix is the route prefix), so the page
        // links (?offset/?limit) are relative to it and resolve back to this route. Trim any trailing
        // slash from the base before appending the route prefix (the same convention as the community
        // search handler's BuildCommunityIri).
        var baseUrl = (options.BaseUri?.Value ?? $"{context.Request.Scheme}://{context.Request.Host}").TrimEnd('/');
        var collectionIri = new Iri($"{baseUrl}{ActivityPubServerConstants.RoutePrefix}/search");
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
            // (as the Iris client does) cannot walk past page 1. The ActivityStreams OrderedCollection
            // type has no typed `next` property (only OrderedCollectionPage does), so the pointer is
            // carried in ExtensionData, which serializes as raw JSON at the document root.
            var collection = new OrderedCollection
            {
                Id = collectionIri.Value,
                Items = [.. slice],
                First = new Link { Href = new Uri(collectionIri.Value) },
                TotalItems = (uint)total,
            };

            if (pageCount > 1)
            {
                var ext = collection.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
                // Emitted as a bare IRI string — the same wire shape the typed `Link` (next/prev)
                // properties produce on OrderedCollectionPage — so page 1 and page N>1 are uniform.
                ext["next"] = System.Text.Json.JsonSerializer.SerializeToElement(
                    $"{collectionIri.Value}/?page=2");
            }

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
