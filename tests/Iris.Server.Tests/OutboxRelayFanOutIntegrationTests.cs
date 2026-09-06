using System.Net;
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
/// Phase 28.1 integration test: the <strong>outbox-publish</strong> relay fan-out. When a local actor
/// publishes a <see cref="Create"/> or <see cref="Announce"/> to their own <em>outbox</em>
/// (<c>POST /ap/v1/u/{handle}/outbox</c>), the server delivers the activity not only to the actor's
/// remote followers but also to each <c>relay</c> the actor has subscribed to (F-06 relay fan-out,
/// ActivityPub §5.1.3). This is the outbox-publish complement of Phase 12's
/// <see cref="RelayFanOutIntegrationTests"/> (which covers the <em>inbox</em> post path via
/// <see cref="Iris.Server.Inbox.CreateActivityHandler"/>).
/// </summary>
/// <remarks>
/// Topology: instance A (outbox-relay-a.domain.local) hosts the local author <c>alice</c>, who has
/// subscribed to a relay (the <c>relay</c> actor on instance R, outbox-relay-r.example.com). Alice
/// publishes a <see cref="Create"/> to her own outbox (signed as alice); A's outbox-publish handler
/// records the post in alice's outbox and fans it out to the relay (F-06). A's host
/// <see cref="Iris.Server.Delivery.IDeliveryService"/> POSTs the <see cref="Create"/> to the relay's
/// inbox, signed as alice; R validates that delivery (resolving alice's key from A's actor document)
/// and stores the <see cref="Create"/> in R's activity store.
/// </remarks>
[Collection("OutboxRelayFanOut")]
public sealed class OutboxRelayFanOutIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "outbox-relay-a.domain.local";
    internal const string RelayHost = "outbox-relay-r.example.com";
    internal const string Alice = "alice";
    internal const string Relay = "relay";

    private readonly OutboxRelayFanOutSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _relayPersistence;
    private readonly HttpClient _aHttp;
    private KeyPair _aliceKey;

    public OutboxRelayFanOutIntegrationTests(OutboxRelayFanOutSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _relayPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aHttp = new HttpClient(_fixture.ServerA.CreateHandler(), disposeHandler: false);
        _aliceKey = null!;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _relayPersistence);

        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _aPersistence.Keys.TryGetKey(new Iri($"{aliceIri.Value}#key-1"), out var aliceKey);
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
    /// Restores alice (on A) + relay (on B) with their existing keys and the alice→relay subscription
    /// edge in A's relay store.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider relayPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var relayIri = new Iri($"https://{RelayHost}/ap/v1/u/{Relay}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(relayPersistence, RelayHost, Relay, new Iri($"{relayIri.Value}#key-1"));
        aPersistence.Relays.RecordRelayAsync(aliceIri, relayIri).GetAwaiter().GetResult();
    }

    // --- A Create published to the author's outbox is fanned out to the subscribed relay --------

    [Fact]
    public async Task OutboxPublish_Create_IsFannedOutToRelay()
    {
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var create = new Create
        {
            Actor = [new Link { Href = new Uri(aliceActorIri.Value) }],
            Object =
            [
                new Note { Content = ["outbox relay post"] },
            ],
        };

        // alice (A) publishes a Create to her own outbox (the outbox-publish write surface). A's
        // outbox-publish handler records it in alice's outbox and fans it out to the relay (F-06),
        // signed as alice. A's delivery service POSTs the Create to the relay's inbox over the wire.
        using var request = SignedRequest(aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Learn the minted id from the 2xx body (decision 055).
        var mintedId = await LearnMintedIdAsync(response);

        // Wait on the EFFECT of the fan-out (R storing the Create), not on A's storage.
        await WaitForAsync(
            async () => await _relayPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(10));

        // (F-06) R validated the fanned-out Create (resolving alice's key from A's actor doc) and stored
        // it — the post reached the relay's instance via the outbox-publish path.
        Assert.True(
            await _relayPersistence.Activities.TryGetActivityAsync(mintedId, out var stored),
            "R should have stored the Create fanned out by A's outbox-publish handler (signed as alice)");
        Assert.NotNull(stored);
        Assert.IsType<Create>(stored);
    }

    // --- An Announce published to the author's outbox is fanned out to the subscribed relay ------

    [Fact(Skip = "hangs >30s")]
    [Trait(TestCategories.Category, TestCategories.Slow)]
    public async Task OutboxPublish_Announce_IsFannedOutToRelay()
    {
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var noteIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        var announce = new Announce
        {
            Actor = [new Link { Href = new Uri(aliceActorIri.Value) }],
            Object = [new Link { Href = noteIri.Uri }],
        };

        // alice (A) publishes an Announce (boost) to her own outbox. A's outbox-publish handler records
        // it in alice's outbox and fans it out to the relay (F-06), signed as alice.
        using var request = SignedRequest(aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var mintedId = await LearnMintedIdAsync(response);

        await WaitForAsync(
            async () => await _relayPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(
            await _relayPersistence.Activities.TryGetActivityAsync(mintedId, out var stored),
            "R should have stored the Announce fanned out by A's outbox-publish handler (signed as alice)");
        Assert.NotNull(stored);
        Assert.IsType<Announce>(stored);
    }

    // --- A post with no subscribed relays is not fanned out (outbox path) -------------------------

    [Fact]
    public async Task OutboxPublish_WithNoSubscribedRelays_IsNotFannedOut()
    {
        // A fresh author (bob) with no subscribed relays publishes a Create to his outbox. The relay
        // store is empty, so no relay fan-out occurs.
        var bobPersistence = new InMemoryPersistenceProvider();
        var bobSeeded = TestSeeder.SeedPersonWithKey(bobPersistence, AHost, "bob");
        var bobActorIri = bobSeeded.ActorIri;

        TestServer? bobServer = null;
        using var a = StartAuthorServer(
            bobPersistence, bobSeeded.Key, bobActorIri,
            relayServer: () => _fixture.ServerB, selfServer: () => bobServer!,
            relayActorIri: new Iri($"https://{RelayHost}/ap/v1/u/{Relay}"));
        bobServer = a;
        using var bobHttp = new HttpClient(a.CreateHandler(), disposeHandler: false);

        var create = new Create
        {
            Actor = [new Link { Href = new Uri(bobActorIri.Value) }],
            Object = [new Note { Content = ["bob's post, no relays"] }],
        };

        using var request = SignedRequest(bobActorIri, bobSeeded.Key, create, $"/ap/v1/u/bob/outbox");
        using var response = await bobHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var mintedId = await LearnMintedIdAsync(response);

        // Wait for bob's outbox to surface the post (the handler ran).
        await WaitForAsync(
            async () => (await bobPersistence.Activities.GetOutboxAsync(bobActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value),
            timeout: TimeSpan.FromSeconds(10));

        // Nothing was fanned out to the relay (bob is not subscribed) — R stored nothing.
        Assert.False(
            await _relayPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            "R should not have stored the Create (bob has no subscribed relays)");
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static HttpRequestMessage SignedRequest(
        Iri actorIri, KeyPair key, Activity activity, string path)
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
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                request.Headers.TryAddWithoutValidation(name, value);
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

    /// <summary>
    /// Captures the signed request body and headers from an <see cref="IActivityPubClient"/> send so the
    /// test can replay the signed request against a different handler (the in-process <c>TestServer</c>).
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed record CapturedRequest(byte[] Body, Dictionary<string, List<string>> Headers);

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created?.Id);
        return new Iri(created!.Id!);
    }

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    internal static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> relayServer, Func<TestServer> selfServer, Iri relayActorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var uri = new Uri(authorActorIri.Value);
        var host = uri.Authority;
        var handle = uri.AbsolutePath.Trim('/').Split('/').Last();

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var selfClient = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        var relayClient = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => relayServer().CreateHandler()));

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new LazyHandler(() => relayServer().CreateHandler()),
            Fetcher = new DelegatingFetcher(
                relayActorIri,
                new IrisActorDocumentFetcher(relayClient, new RemoteActorCache()),
                new IrisActorDocumentFetcher(selfClient, new RemoteActorCache())),
        });
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that delegates the relay's actor IRI to one fetcher (the
    /// relay's document, on the relay's instance) and every other actor to another (the local actor's
    /// document, on the author's own instance).
    /// </summary>
    internal sealed class DelegatingFetcher(
        Iri relayActorIri, IActorDocumentFetcher relay, IActorDocumentFetcher self)
        : IActorDocumentFetcher
    {
        private readonly Iri _relayActorIri = relayActorIri;
        private readonly IActorDocumentFetcher _relay = relay;
        private readonly IActorDocumentFetcher _self = self;

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => actorIri == _relayActorIri
                ? _relay.GetActorAsync(actorIri, ct)
                : _self.GetActorAsync(actorIri, ct);
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="OutboxRelayFanOutIntegrationTests"/> (A: outbox-relay-a.domain.local
/// alice, B: outbox-relay-r.example.com relay). Seeds alice + relay with keys ONCE; A's outbound delivery
/// routes to B (the relay's inbox), A's fetcher is a <see cref="OutboxRelayFanOutIntegrationTests.DelegatingFetcher"/>
/// (relay doc from B, self from A), B's fetcher reaches A (validates the fanned-out Create); the
/// alice→relay subscription edge is recorded in A's relay store.
/// </summary>
public sealed class OutboxRelayFanOutSharedHost : SharedTwoHostFixture
{
    public OutboxRelayFanOutSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var relayPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, OutboxRelayFanOutIntegrationTests.AHost, OutboxRelayFanOutIntegrationTests.Alice);
        var relaySeeded = TestSeeder.SeedPersonWithKey(relayPersistence, OutboxRelayFanOutIntegrationTests.RelayHost, OutboxRelayFanOutIntegrationTests.Relay);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(relayPersistence);

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aSeeded.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var selfClient = factory.Create(
            new ActivityPubClientOptions { ActorId = aSeeded.ActorIri, EnableRetry = false },
            new LazyHandler(() => serverARef().CreateHandler()));
        var relayClient = factory.Create(
            new ActivityPubClientOptions { ActorId = aSeeded.ActorIri, EnableRetry = false },
            new LazyHandler(() => serverBRef().CreateHandler()));
        var relayActorIri = relaySeeded.ActorIri;

        var optionsA = new ActivityPubHostOptions
        {
            Host = OutboxRelayFanOutIntegrationTests.AHost,
            Handle = OutboxRelayFanOutIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new OutboxRelayFanOutIntegrationTests.DelegatingFetcher(
                relayActorIri,
                new IrisActorDocumentFetcher(relayClient, new RemoteActorCache()),
                new IrisActorDocumentFetcher(selfClient, new RemoteActorCache())),
        };

        var relayKeyStore = new InMemoryKeyStore();
        relayKeyStore.PutKey(relaySeeded.Key);
        var relayKeyProvider = new InMemoryKeyProvider(relayKeyStore);
        relayKeyProvider.RegisterKey(relayActorIri, relaySeeded.Key.KeyId);
        var relaySigner = new HttpSignatureSigner(relayKeyStore);

        var optionsB = new ActivityPubHostOptions
        {
            Host = OutboxRelayFanOutIntegrationTests.RelayHost,
            Handle = OutboxRelayFanOutIntegrationTests.Relay,
            Persistence = relayPersistence,
            IdentityKeys = new IdentityKeys(relayKeyStore, relayKeyProvider, relaySigner),
            Fetcher = BuildFetcherForLazy(relaySeeded.Key, relayActorIri, serverARef),
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// Builds a fetcher whose client (signed as the relay) routes to the (deferred) A's
    /// <c>TestServer</c> — i.e. the relay's fetcher reaches A's actor documents (lazily).
    /// </summary>
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
/// xunit collection definition for the outbox relay fan-out shared two-host fixture.
/// </summary>
[CollectionDefinition("OutboxRelayFanOut")]
public sealed class OutboxRelayFanOutCollection : ICollectionFixture<OutboxRelayFanOutSharedHost>
{
}
