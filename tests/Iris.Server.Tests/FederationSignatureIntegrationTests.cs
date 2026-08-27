using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 integration tests: the first true <strong>instance-to-instance federation</strong> test.
/// Two live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> instances (A and B) are
/// wired together over a genuine HTTP stack:
/// </summary>
/// <list type="bullet">
/// <item>Instance A (a.domain.local) hosts actor <c>alice</c> (key <c>keyA</c>).</item>
/// <item>Instance B (b.domain.local) hosts actor <c>bob</c> (key <c>keyB</c>).</item>
/// </list>
/// <para>
/// alice follows bob: a client signed as alice POSTs a <c>Follow</c> activity to B's inbox. B's
/// <see cref="SignatureValidationMiddleware"/> validates the HTTP signature by resolving alice's public
/// key — fetching A's actor document over the wire (B's <see cref="IActorDocumentFetcher"/> is wired to
/// A's <c>TestServer</c>) — and checking it cryptographically. The inbox handler then stores the
/// validated activity.
/// </para>
/// <remarks>
/// This proves the full inbound validation path end-to-end: signature parsing, remote key resolution
/// via actor-document fetch, key reconstruction from a JWK, and cryptographic verification — all over
/// real HTTP between two independent Iris instances.
/// </remarks>
public sealed class FederationSignatureIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;

    private readonly Iri AliceActorIri;
    private readonly Iri AliceKeyId;
    private readonly Iri BobActorIri;
    private readonly Iri BobInboxIri;

    public FederationSignatureIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = Seed(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;
        AliceKeyId = aSeeded.KeyId;

        var bSeeded = Seed(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        BobActorIri = bSeeded.ActorIri;
        BobInboxIri = BobActorIri.InboxOf();

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, _a.CreateHandler()));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- The happy path: alice follows bob over the wire ----------------------

    [Fact]
    public async Task Follow_SignedByAlice_IsValidatedAndAcceptedAtBobInbox()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // A client signed as alice, whose transport routes to B's TestServer.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);

        Assert.Equal(202, statusCode);

        // B validated the signature (by fetching A's actor doc to resolve alice's key) and stored
        // the activity under its IRI.
        var stored = await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var activity);
        Assert.True(stored);
        Assert.NotNull(activity);
        Assert.Equal(follow.Id, activity!.Id);

        // A's actor document endpoint was hit by B's key-resolution fetch (the federation round-trip).
        // (The middleware on A does not validate GETs, so this is a plain public document fetch.)
    }

    // --- The follow is not just stored: the FollowActivityHandler records the edge ----

    [Fact]
    public async Task Follow_SignedByAlice_RecordsFollowEdgeInBobFollowStore()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // Deliver the signed Follow over the wire to B's inbox.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);
        Assert.Equal(202, statusCode);

        // B's inbox processor dispatched the validated Follow to the FollowActivityHandler, which
        // recorded the directed edge alice → bob in B's follow store. This proves the full inbound
        // pipeline end-to-end: signature validation → store → interpret (record the follow).
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "After a signed Follow, alice should follow bob in B's follow store");

        var bobFollowers = await _bPersistence.Follows.GetFollowersAsync(BobActorIri);
        Assert.Contains(AliceActorIri, bobFollowers);

        // And the reverse direction (bob's following list) is also recorded.
        var aliceFollowing = await _bPersistence.Follows.GetFollowingAsync(AliceActorIri);
        Assert.Contains(BobActorIri, aliceFollowing);
    }

    // --- The full Follow/Accept loop: bob accepts alice's follow, delivered back over the wire --

    [Fact]
    public async Task Follow_ThenAccept_FullFederationLoop_AcceptIsDeliveredBackToAliceInbox()
    {
        // Self-contained two-instance federation (mirrors DeliveryIntegrationTests): fresh
        // persistence per instance so this test is isolated from the fixture's servers. B's outbound
        // delivery transport is wired to A so B's DeliveryWorker delivers the Accept back to alice's
        // inbox over the wire (signed as bob); A validates bob's signature by fetching B's actor doc.
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = Seed(aPersistence, AHost, Alice);
        var bSeeded = Seed(bPersistence, BHost, Bob);
        var aliceActorIri = aSeeded.ActorIri;
        var bobActorIri = bSeeded.ActorIri;
        var bobInboxIri = bobActorIri.InboxOf();

        // A's fetcher routes to the NEW b via a lazy handler (resolved on first use), which breaks the
        // A↔B wiring chicken-and-egg (A's fetcher needs B's handler; B's transport needs A's handler)
        // and ensures A fetches bob's actor doc carrying bSeeded.Key — the key B's worker actually
        // signs the Accept with — so A's signature validation succeeds.
        TestServer? bRef = null;
        var a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())));
        bRef = StartServer(BHost, Bob, bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, a.CreateHandler()),
            deliveryTransport: () => a.CreateHandler());
        using var scope = new DisposeBoth(bRef, a);

        // Deliver a signed Follow from alice to bob's inbox over the wire.
        var follow = BuildFollow(aliceActorIri, bobActorIri);
        using var client = BuildDeliveryClient(aliceActorIri, aSeeded.Key, bRef.CreateHandler());
        var statusCode = await client.DeliverAsync(bobInboxIri, follow);
        Assert.Equal(202, statusCode);
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "B should record that alice follows bob");

        // B's FollowActivityHandler scheduled an Accept with a deterministic IRI (bob's actor IRI +
        // /accepts + the follow IRI). B's DeliveryWorker delivers it to alice's inbox over the wire;
        // A validates bob's signature (fetching B's actor doc) and stores it under that IRI.
        var acceptIri = new Iri($"{bobActorIri}/accepts/{follow.Id}");
        await WaitForAsync(
            () => aPersistence.Activities.TryGetActivityAsync(acceptIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(
            await aPersistence.Activities.TryGetActivityAsync(acceptIri, out var stored),
            "A should have stored the Accept delivered by B over the wire");
        var accept = Assert.IsType<Accept>(stored!);

        // The Accept's object references the original follow (by IRI) and its actor is bob.
        Assert.NotNull(accept.Object);
        Assert.Contains(accept.Object!, o => o is ILink { Href: { } href } && href == new Uri(follow.Id!));
        Assert.NotNull(accept.Actor);
        Assert.Contains(accept.Actor!, a => a is ILink { Href: { } href } && href == new Uri(bobActorIri.Value));
    }

    // --- Key resolution: B resolves alice's key by fetching A's actor doc --------

    [Fact]
    public async Task Resolver_ResolvesRemoteKey_ByFetchingActorDocumentOverWire()
    {
        var resolver = _b.Services.GetRequiredService<IInboundKeyResolver>();
        var key = await resolver.ResolveAsync(AliceKeyId);
        Assert.True(key is not null,
            "B's IInboundKeyResolver should resolve alice's key by fetching A's actor doc over the wire");
    }

    // --- Negative: an unsigned inbox POST is rejected with 401 ------------------

    [Fact]
    public async Task Follow_UntouchedBySignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // A plain (unsigned) POST to B's inbox: no Signature header → 401.
        using var http = new HttpClient(_b.CreateHandler());
        using var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        var response = await http.PostAsync(
            $"https://{BHost}/ap/v1/u/{Bob}/inbox", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Negative: a tampered signature is rejected with 401 -------------------

    [Fact]
    public async Task Follow_TamperedSignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // Deliver to a capture handler that records the signed request, so we can replay it tampered.
        using var capture = new CaptureHandler(_b.CreateHandler());
        using var captureClient = BuildDeliveryClient(AliceActorIri, _aliceKey, capture);
        // (captureClient's transport is the CaptureHandler, which forwards to B's TestServer.)
        _ = await captureClient.DeliverAsync(BobInboxIri, follow);
        var captured = Assert.Single(capture.Captured);

        // Tamper with the body: the original Digest header no longer matches the (changed) body →
        // validation fails → 401. Reuse the signed request's Date + Signature message headers and
        // Digest + Content-Type content headers; only the body changes.
        var tampered = new HttpRequestMessage(HttpMethod.Post, captured.RequestUri!)
        {
            // The body is tampered (a trailing space changes the bytes → the digest mismatches).
            Content = new StringContent(json + " "),
        };

        // Copy the signature-relevant message headers (Date + Signature). Skip Host: HttpClient sets it.
        foreach (var key in new[] { "Date", "Signature" })
        {
            if (captured.Headers.TryGetValue(key, out var values))
            {
                tampered.Headers.TryAddWithoutValidation(key, values);
            }
        }

        // Copy the content headers (Digest + Content-Type) verbatim.
        tampered.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        if (captured.ContentHeaders.TryGetValue("Digest", out var digests))
        {
            tampered.Content.Headers.TryAddWithoutValidation("Digest", digests);
        }

        using var http = new HttpClient(_b.CreateHandler());
        var response = await http.SendAsync(tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Seeds a persistence provider with a single actor (Person) + a real EC key.
    /// The actor's <c>publicKey</c> extension carries the real JWK (so a remote resolver can verify).
    /// Returns the key, the actor IRI (string + <see cref="Iri"/>), and the key IRI.
    /// </summary>
    private static (KeyPair Key, string ActorIriString, Iri ActorIri, Iri KeyId) Seed(
        InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIriString = $"https://{host}/ap/v1/u/{handle}";
        var actorIri = new Iri(actorIriString);
        var keyId = new Iri($"{actorIriString}#key-1");

        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIriString,
            // The real JWK, so a remote instance can reconstruct the public key and verify signatures.
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        return (key, actorIriString, actorIri, keyId);
    }

    private static string ExtractJwkComponent(KeyPair key, string name)
    {
        // The JWK is the canonical serialization; pull the component out of it.
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }


    /// <summary>
    /// Builds a delivery <see cref="IActivityPubClient"/> signed with the given key (as the given
    /// actor), whose transport is the given <paramref name="handler"/>.
    /// </summary>
    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as the given
    /// <paramref name="handle"/>) routes over <paramref name="handler"/> — i.e. B's fetcher reaches
    /// A's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair bobKey, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var bobActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(bobActorIri, bobKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = bobActorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client);
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, optionally
    /// overriding the <see cref="IActorDocumentFetcher"/> (for the federation wiring) and the
    /// <c>Func&lt;HttpMessageHandler&gt;</c> delivery transport (so this instance's outbound
    /// <see cref="DeliveryWorker"/> routes to the other in-process <see cref="TestServer"/> instead of
    /// the real network).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? extraServices = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    // The instance actor is the federation signing identity (outbound fetches).
                    opts.InstanceActorId = new Iri($"https://{host}/ap/v1/u/{handle}");
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                // The seeded persistence carries the local actor's private signing key; bind the
                // IKeyStore seam to it so the outbound DeliveryWorker (which signs as InstanceActorId)
                // can find the key. AddInMemoryPersistence otherwise registers a fresh, empty
                // InMemoryKeyStore.
                s.AddSingleton<IKeyStore>(persistence.Keys);

                if (fetcher is not null)
                {
                    // Override the default fetcher (which would use a real HttpClientHandler and fail
                    // to resolve the in-process host) with one wired to the other TestServer.
                    s.AddSingleton<IActorDocumentFetcher>(fetcher);
                }

                if (deliveryTransport is { } transport)
                {
                    // Override the default delivery transport (real HttpClientHandler) so this
                    // instance's outbound DeliveryWorker routes to the other in-process TestServer.
                    s.AddSingleton<Func<HttpMessageHandler>>(() => transport());
                }

                extraServices?.Invoke(s);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        var server = new TestServer(builder);

        // Register the local actor's key with the IKeyProvider so the outbound DeliveryWorker (which
        // signs as InstanceActorId = the local actor) can find the key. The key IRI is the actor's
        // publicKey.id (the #key-1 convention used by Seed).
        var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, new Iri($"{actorIri}#key-1"));

        return server;
    }

    private static Follow BuildFollow(Iri actorIri, Iri targetIri)
    {
        // Multi-valued Actor/Object: set via an object initializer of Links (Rule 2 — never a
        // positional constructor; Rule 3 — read multi-valued as IEnumerable).
        var follow = new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
        return follow;
    }

    /// <summary>
    /// A handler that records the signed request (headers + URI) and forwards it to the inner handler.
    /// Forwarding goes through an <see cref="HttpClient"/> over the inner handler (the handler's
    /// <c>SendAsync</c> is protected and cannot be invoked through a base-typed reference).
    /// </summary>
    private sealed class CaptureHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpClient _forward = new(inner, disposeHandler: true);

        /// <summary>
        /// The captured (signed) requests, in order.
        /// </summary>
        public List<CapturedRequest> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest
            {
                RequestUri = request.RequestUri,
                Headers = new Dictionary<string, IList<string>>(),
                ContentHeaders = new Dictionary<string, IList<string>>(),
            };
            foreach (var header in request.Headers)
            {
                captured.Headers[header.Key] = header.Value.ToList();
            }

            if (request.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    captured.ContentHeaders[header.Key] = header.Value.ToList();
                }
            }

            Captured.Add(captured);

            // Forward a clone: the outer HttpClient has already marked the original request as
            // sent, so re-sending the same instance through _forward would throw. A clone carries
            // the same method/URI/headers/content (and the signing is already applied upstream).
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } sourceContent)
            {
                var body = await sourceContent.ReadAsByteArrayAsync(cancellationToken);
                var clonedContent = new ByteArrayContent(body);
                foreach (var header in sourceContent.Headers)
                {
                    clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = clonedContent;
            }

            return await _forward.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _forward.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A captured HTTP request (URI + headers) for replaying with a tampered body.
    /// </summary>
    private sealed class CapturedRequest
    {
        public Uri? RequestUri { get; init; }

        public Dictionary<string, IList<string>> Headers { get; init; } = new();

        public Dictionary<string, IList<string>> ContentHeaders { get; init; } = new();
    }

    /// <summary>
    /// Awaits until <paramref name="probe"/> returns true or the timeout elapses.
    /// </summary>
    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Disposes two <see cref="TestServer"/> instances (for the tests that spin up an extra pair).
    /// </summary>
    private sealed class DisposeBoth(TestServer one, TestServer two) : IDisposable
    {
        public void Dispose()
        {
            one.Dispose();
            two.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that records each fetch (actor IRI + outcome) then
    /// forwards to an inner fetcher. Used to detect whether the inbound key resolver's fetch runs
    /// (and whether it completes) during a signed inbox request.
    /// </summary>
    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request. Used to break the A↔B wiring chicken-and-egg (A's fetcher needs B's handler; B's
    /// transport needs A's handler) — both servers exist by the time any request flows.
    /// </summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> innerFactory) : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory = innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            // Clone the request: the inner pipeline may retry (RetryHandler), and HttpClient
            // forbids sending the same request message more than once.
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return _client.SendAsync(clone, cancellationToken);
        }
    }
}
