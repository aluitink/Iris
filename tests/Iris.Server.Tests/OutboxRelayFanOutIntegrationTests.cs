using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

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
public sealed class OutboxRelayFanOutIntegrationTests : IDisposable
{
    private const string AHost = "outbox-relay-a.domain.local";
    private const string RelayHost = "outbox-relay-r.example.com";
    private const string Alice = "alice";
    private const string Relay = "relay";

    private readonly TestServer _a;
    private readonly TestServer _relay;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _relayPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;

    public OutboxRelayFanOutIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _relayPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var relaySeeded = TestSeeder.SeedPersonWithKey(_relayPersistence, RelayHost, Relay);

        // alice (on A) has subscribed to the relay: the F-06 subscription edge.
        _aPersistence.Relays
            .RecordRelayAsync(aSeeded.ActorIri, relaySeeded.ActorIri)
            .GetAwaiter().GetResult();

        // A hosts alice; its outbound delivery worker routes to the relay's TestServer (so the fanned-out
        // Create reaches the relay's inbox) and signs as alice. The transport and the relay-fetcher target
        // are deferred (Func) because the relay is created after A (chicken-and-egg).
        _a = StartAuthorServer(
            _aPersistence, aSeeded.Key, aSeeded.ActorIri,
            relayServer: () => _relay!, selfServer: () => _a!,
            relayActorIri: relaySeeded.ActorIri);

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);

        // R hosts the relay; its fetcher is wired to A so R validates the fanned-out Create by fetching
        // A's actor document (alice's key).
        _relay = StartServer(
            RelayHost, Relay, _relayPersistence, relaySeeded.Key,
            fetcher: BuildFetcherFor(RelayHost, Relay, relaySeeded.Key, targetServer: _a));
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _a.Dispose();
        _relay.Dispose();
    }

    // --- A Create published to the author's outbox is fanned out to the subscribed relay --------

    [Fact]
    public async Task OutboxPublish_Create_IsFannedOutToRelay()
    {
        var create = new Create
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object =
            [
                new Note { Content = ["outbox relay post"] },
            ],
        };

        // alice (A) publishes a Create to her own outbox (the outbox-publish write surface). A's
        // outbox-publish handler records it in alice's outbox and fans it out to the relay (F-06),
        // signed as alice. A's delivery service POSTs the Create to the relay's inbox over the wire.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
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

    [Fact]
    public async Task OutboxPublish_Announce_IsFannedOutToRelay()
    {
        var noteIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        var announce = new Announce
        {
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = noteIri.Uri }],
        };

        // alice (A) publishes an Announce (boost) to her own outbox. A's outbox-publish handler records
        // it in alice's outbox and fans it out to the relay (F-06), signed as alice.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
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
            relayServer: () => _relay, selfServer: () => bobServer!,
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

    private static TestServer StartAuthorServer(
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

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, KeyPair key,
        IActorDocumentFetcher fetcher)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
        });
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, TestServer targetServer)
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
            targetServer.CreateHandler());

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that delegates the relay's actor IRI to one fetcher (the
    /// relay's document, on the relay's instance) and every other actor to another (the local actor's
    /// document, on the author's own instance).
    /// </summary>
    private sealed class DelegatingFetcher(
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
