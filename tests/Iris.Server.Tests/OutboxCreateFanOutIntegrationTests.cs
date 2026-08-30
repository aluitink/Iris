using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 integration test for the <strong>G-1 residual</strong>: the outbox-publish Create path
/// (<c>POST /ap/v1/u/{handle}/outbox</c>) must fan out to <em>every</em> remote, non-blocked follower,
/// not just the first. Previously <c>RecordCreateLocalAsync</c> returned a single representative
/// recipient (the first remote follower); now it returns the full set, and the handler delivers the
/// signed <see cref="Create"/> to each one's inbox (mirroring <c>CreateActivityHandler</c>'s fan-out
/// loop on the inbound path).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts author <c>alice</c>. Instances B (b.domain.local,
/// <c>bob</c>) and C (c.domain.local, <c>carol</c>) each host a remote follower. The follow edges
/// bob→alice and carol→alice are recorded on A (A is alice's home; it owns her follower set). Alice
/// publishes a <see cref="Create"/> to her own outbox (signed as alice); A's server records it and
/// server-delivers the signed <c>Create</c> to <em>both</em> bob's and carol's inboxes. B and C
/// validate the signature (fetching A's actor doc for alice's key) and store the <c>Create</c>.
/// </remarks>
public sealed class OutboxCreateFanOutIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string CHost = "c.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly TestServer _c;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;

    public OutboxCreateFanOutIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();
        _cPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        var cSeeded = TestSeeder.SeedPersonWithKey(_cPersistence, CHost, Carol);
        _carolActorIri = cSeeded.ActorIri;

        // The follow edges bob→alice and carol→alice are recorded on A (A is alice's home instance).
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();
        _aPersistence.Follows.RecordFollowAsync(_carolActorIri, _aliceActorIri).GetAwaiter().GetResult();

        // A's delivery transport routes to both B and C (a routing handler dispatches on the request's
        // host header). A's fetcher routes by actor IRI host: alice (A) → A's TestServer, bob (B) →
        // B's TestServer, carol (C) → C's TestServer. B and C's fetchers are wired to A (to validate
        // alice's key on the federated Creates).
        _a = StartAuthorServer(_aPersistence, _aliceKey, _aliceActorIri,
            bServer: () => _b!, cServer: () => _c!, selfServer: () => _a!);
        _b = StartServer(BHost, Bob, _bPersistence, bSeeded.Key,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
        _c = StartServer(CHost, Carol, _cPersistence, cSeeded.Key,
            fetcher: BuildFetcherFor(CHost, Carol, cSeeded.Key, _a.CreateHandler()));
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _c.Dispose();
    }

    // --- A post published to the author's outbox is federated to ALL remote followers -------

    [Fact]
    public async Task OutboxPublish_CreateWithTwoRemoteFollowers_FederatesToBoth()
    {
        var create = BuildCreate(_aliceActorIri);

        // Sign the request as alice (the outbox-publish write surface requires a valid signature from
        // the acting actor) and POST to A's /ap/v1/u/alice/outbox.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A recorded the Create in alice's outbox (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // Both B and C validated the federated Create (resolving alice's key from A's actor doc) and
        // stored it — the post reached BOTH remote followers' instances (the G-1 residual fix: full
        // fan-out, not just the first follower).
        await WaitForAsync(async () =>
                await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _)
                && await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var storedB),
            "B should have stored the Create federated by A's server (signed as alice)");
        Assert.True(
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var storedC),
            "C should have stored the Create federated by A's server (signed as alice)");
        Assert.IsType<Create>(storedB);
        Assert.IsType<Create>(storedC);
    }

    // --- A post with no remote followers is surfaced locally but not federated --------------

    [Fact]
    public async Task OutboxPublish_CreateWithNoRemoteFollowers_SurfacesLocallyOnly()
    {
        // A fresh author (dave) with no followers posts a Create to his outbox; it is recorded in dave's
        // outbox but nothing is scheduled for delivery (no remote followers).
        var daveSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "dave");
        var daveActorIri = daveSeeded.ActorIri;

        var create = BuildCreate(daveActorIri);
        using var request = SignedRequest(daveActorIri, daveSeeded.Key, create, "/ap/v1/u/dave/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Surfaced in dave's outbox ...
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(daveActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // ... and nothing was federated (no followers) — B and C stored nothing.
        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
        Assert.False(await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
    }

    // --- A blocked remote follower does not receive the federated Create ---------------------

    [Fact]
    public async Task OutboxPublish_CreateWithBlockedRemoteFollower_SkipsBlocked()
    {
        // Bob blocks alice (recorded on A). Alice publishes a Create; carol (unblocked) receives it,
        // but bob (blocked) does not.
        await _aPersistence.Moderation.RecordBlockAsync(_bobActorIri, _aliceActorIri);

        var create = BuildCreate(_aliceActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Carol (unblocked) received the federated Create.
        await WaitForAsync(async () =>
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));

        // Bob (blocked) did NOT receive the federated Create.
        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a capture handler) to produce a correctly signed
    /// request, then replays the signed headers onto a fresh request for delivery to A's TestServer.
    /// </summary>
    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{path}")
        {
            Content = content,
        };
        foreach (var (name, values) in captured.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (captured.Headers.TryGetValue("date", out var dateValues))
        {
            foreach (var value in dateValues)
            {
                request.Headers.TryAddWithoutValidation("date", value);
            }
        }

        return request;
    }

    private static IActivityPubClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> bServer, Func<TestServer> cServer, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new RoutingHandler(BHost, bServer, CHost, cServer),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => selfServer().CreateHandler()),
                BHost, new LazyHandler(() => bServer().CreateHandler()),
                CHost, new LazyHandler(() => cServer().CreateHandler()),
                authorKey, authorActorIri),
        });
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey, IActorDocumentFetcher? fetcher = null)
    {
        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
        });
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that routes to the B or C server based on the request's host
    /// header (A's delivery worker sends to both remote followers' inboxes, which are on different
    /// instances).
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _bHost;
        private readonly Func<TestServer> _bServer;
        private readonly string _cHost;
        private readonly Func<TestServer> _cServer;

        public RoutingHandler(string bHost, Func<TestServer> bServer, string cHost, Func<TestServer> cServer)
        {
            _bHost = bHost;
            _bServer = bServer;
            _cHost = cHost;
            _cServer = cServer;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var handler = host == _cHost ? _cServer().CreateHandler() : _bServer().CreateHandler();

            // Clone the request (the inner pipeline may retry, and HttpClient forbids sending the same
            // request message more than once).
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
                clone.Content = new ByteArrayContent(
                    content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return new HttpClient(handler, disposeHandler: false).SendAsync(clone, cancellationToken);
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents
    /// based on the actor IRI's host (A's fetcher needs to reach A, B, and C to validate alice's own
    /// signature and resolve bob's/carol's inboxes).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            string cHost, HttpMessageHandler cHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
                [cHost] = BuildFetcherFor(cHost, "local", signingKey, cHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Host;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }
    }

    /// <summary>
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can be
    /// replayed through a plain <see cref="HttpClient"/>.
    /// </summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public CapturedRequest? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToList();
            }

            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    if (headers.TryGetValue(name, out var existing))
                    {
                        existing.AddRange(values);
                    }
                    else
                    {
                        headers[name] = values.ToList();
                    }
                }
            }

            Captured = new CapturedRequest(body, headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);

    private static Create BuildCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["fan-out post"] },
        ],
    };

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
}
