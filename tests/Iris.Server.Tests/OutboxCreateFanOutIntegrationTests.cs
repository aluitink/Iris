using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
[Collection("OutboxCreateFanOut")]
public sealed class OutboxCreateFanOutIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "b.domain.local";
    internal const string CHost = "c.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    private readonly OutboxCreateFanOutSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private readonly HttpClient _aHttp;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;

    public OutboxCreateFanOutIntegrationTests(OutboxCreateFanOutSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _cPersistence = (InMemoryPersistenceProvider)fixture.PersistenceC;
        _aHttp = new HttpClient(_fixture.ServerA.CreateHandler(), disposeHandler: false);
        _aliceKey = null!;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _carolActorIri = new Iri($"https://{CHost}/ap/v1/u/{Carol}");
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence, _cPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores alice (on A), bob (on B), and carol (on C) with their existing keys and the
    /// bob→alice + carol→alice follow edges on A.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence, InMemoryPersistenceProvider cPersistence)
    {
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"https://{AHost}/ap/v1/u/{Alice}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"https://{BHost}/ap/v1/u/{Bob}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(cPersistence, CHost, Carol, new Iri($"https://{CHost}/ap/v1/u/{Carol}#key-1"));
        aPersistence.Follows.RecordFollowAsync(
            new Iri($"https://{BHost}/ap/v1/u/{Bob}"),
            new Iri($"https://{AHost}/ap/v1/u/{Alice}")).GetAwaiter().GetResult();
        aPersistence.Follows.RecordFollowAsync(
            new Iri($"https://{CHost}/ap/v1/u/{Carol}"),
            new Iri($"https://{AHost}/ap/v1/u/{Alice}")).GetAwaiter().GetResult();
    }

    // --- A post published to the author's outbox is federated to ALL remote followers -------

    [Fact]
    public async Task OutboxPublish_CreateWithTwoRemoteFollowers_FederatesToBoth()
    {
        var create = BuildCreate(_aliceActorIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var createIri = await LearnMintedIdAsync(response);

        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == createIri.Value);

        await TestFederation.WaitForAsync(async () =>
                await _bPersistence.Activities.TryGetActivityAsync(createIri, out _)
                && await _cPersistence.Activities.TryGetActivityAsync(createIri, out _),
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(createIri, out var storedB),
            "B should have stored the Create federated by A's server (signed as alice)");
        Assert.True(
            await _cPersistence.Activities.TryGetActivityAsync(createIri, out var storedC),
            "C should have stored the Create federated by A's server (signed as alice)");
        Assert.IsType<Create>(storedB);
        Assert.IsType<Create>(storedC);
    }

    // --- A post with no remote followers is surfaced locally but not federated --------------

    [Fact]
    public async Task OutboxPublish_CreateWithNoRemoteFollowers_SurfacesLocallyOnly()
    {
        var daveSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "dave");
        var daveActorIri = daveSeeded.ActorIri;

        var create = BuildCreate(daveActorIri);
        using var request = SignedRequest(daveActorIri, daveSeeded.Key, create, "/ap/v1/u/dave/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var createIri = await LearnMintedIdAsync(response);

        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(daveActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == createIri.Value);

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(createIri, out _));
        Assert.False(await _cPersistence.Activities.TryGetActivityAsync(createIri, out _));
    }

    // --- A blocked remote follower does not receive the federated Create ---------------------

    [Fact]
    public async Task OutboxPublish_CreateWithBlockedRemoteFollower_SkipsBlocked()
    {
        await _aPersistence.Moderation.RecordBlockAsync(_bobActorIri, _aliceActorIri);

        var create = BuildCreate(_aliceActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var createIri = await LearnMintedIdAsync(response);

        await TestFederation.WaitForAsync(async () =>
            await _cPersistence.Activities.TryGetActivityAsync(createIri, out _),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(await _cPersistence.Activities.TryGetActivityAsync(createIri, out _));

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(createIri, out _));
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox. Uses the client pipeline (via a capture handler) to produce a correctly signed
    /// request, then replays the signed headers onto a fresh request for delivery to A's TestServer.
    /// </summary>
    private static HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string path)
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

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that routes to the B or C server based on the request's host
    /// header (A's delivery worker sends to both remote followers' inboxes, which are on different
    /// instances).
    /// </summary>
    internal sealed class RoutingHandler : HttpMessageHandler
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
    internal sealed class RoutingFetcher : IActorDocumentFetcher
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
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Content = ["fan-out post"] },
        ],
    };

    /// <summary>
    /// Learns the server-minted id of the created activity from the 202 response body (decision 055:
    /// the server mints the id and returns the created object in the 2xx body).
    /// </summary>
    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created?.Id);
        return new Iri(created!.Id!);
    }
}

/// <summary>
/// Shared three-host fixture for <see cref="OutboxCreateFanOutIntegrationTests"/> (A: a.domain.local
/// alice, B: b.domain.local bob, C: c.domain.local carol). Seeds alice + bob + carol with keys ONCE;
/// A's identity + RoutingFetcher (A/B/C docs) + RoutingHandler delivery to B/C by host; B's and C's
/// fetchers reach A (validate alice's key). Both bob→alice and carol→alice follow edges are on A.
/// </summary>
public sealed class OutboxCreateFanOutSharedHost : SharedThreeHostFixture
{
    public OutboxCreateFanOutSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B, ActivityPubHostOptions C) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var cPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, OutboxCreateFanOutIntegrationTests.AHost, OutboxCreateFanOutIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, OutboxCreateFanOutIntegrationTests.BHost, OutboxCreateFanOutIntegrationTests.Bob);
        var cSeeded = TestSeeder.SeedPersonWithKey(cPersistence, OutboxCreateFanOutIntegrationTests.CHost, OutboxCreateFanOutIntegrationTests.Carol);

        aPersistence.Follows.RecordFollowAsync(bSeeded.ActorIri, aSeeded.ActorIri).GetAwaiter().GetResult();
        cPersistence.Follows.RecordFollowAsync(bSeeded.ActorIri, aSeeded.ActorIri).GetAwaiter().GetResult();

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);
        var serverCRef = SharedHostFixture.ServerRefFor(cPersistence);

        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var aSigner = new HttpSignatureSigner(aKeyStore);

        var bKeyStore = new InMemoryKeyStore();
        bKeyStore.PutKey(bSeeded.Key);
        var bKeyProvider = new InMemoryKeyProvider(bKeyStore);
        bKeyProvider.RegisterKey(bSeeded.ActorIri, bSeeded.Key.KeyId);
        var bSigner = new HttpSignatureSigner(bKeyStore);

        var cKeyStore = new InMemoryKeyStore();
        cKeyStore.PutKey(cSeeded.Key);
        var cKeyProvider = new InMemoryKeyProvider(cKeyStore);
        cKeyProvider.RegisterKey(cSeeded.ActorIri, cSeeded.Key.KeyId);
        var cSigner = new HttpSignatureSigner(cKeyStore);

        var optionsA = new ActivityPubHostOptions
        {
            Host = OutboxCreateFanOutIntegrationTests.AHost,
            Handle = OutboxCreateFanOutIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            DeliveryTransport = () => new OutboxCreateFanOutIntegrationTests.RoutingHandler(
                OutboxCreateFanOutIntegrationTests.BHost, serverBRef,
                OutboxCreateFanOutIntegrationTests.CHost, serverCRef),
            Fetcher = new OutboxCreateFanOutIntegrationTests.RoutingFetcher(
                OutboxCreateFanOutIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                OutboxCreateFanOutIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                OutboxCreateFanOutIntegrationTests.CHost, new LazyHandler(() => serverCRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = OutboxCreateFanOutIntegrationTests.BHost,
            Handle = OutboxCreateFanOutIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = new IdentityKeys(bKeyStore, bKeyProvider, bSigner),
            Fetcher = BuildFetcherForLazy(bSeeded.Key, bSeeded.ActorIri, serverARef),
        };

        var optionsC = new ActivityPubHostOptions
        {
            Host = OutboxCreateFanOutIntegrationTests.CHost,
            Handle = OutboxCreateFanOutIntegrationTests.Carol,
            Persistence = cPersistence,
            IdentityKeys = new IdentityKeys(cKeyStore, cKeyProvider, cSigner),
            Fetcher = BuildFetcherForLazy(cSeeded.Key, cSeeded.ActorIri, serverARef),
        };

        return (optionsA, optionsB, optionsC);
    }

    private static IActorDocumentFetcher BuildFetcherForLazy(
        KeyPair key, Iri actorIri, Func<TestServer> targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => targetServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>
/// xunit collection definition for the outbox create fan-out shared three-host fixture.
/// </summary>
[CollectionDefinition("OutboxCreateFanOut")]
public sealed class OutboxCreateFanOutCollection : ICollectionFixture<OutboxCreateFanOutSharedHost>
{
}
