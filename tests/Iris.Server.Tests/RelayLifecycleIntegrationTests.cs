using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 28.3 integration test: the full <strong>relay subscription lifecycle</strong>. A local author
/// <em>subscribes</em> to a relay (the F-06 edge is recorded in <c>IRelayStore</c>), publishes a
/// <see cref="Create"/> to their outbox (which is fanned out to the relay), then <em>unsubscribes</em>
/// from the relay (the F-06 edge is removed), and a subsequent <see cref="Create"/> is <strong>not</strong>
/// fanned out to the de-subscribed relay.
/// </summary>
/// <remarks>
/// <para>
/// Topology: instance B (relay-lifecycle-b.domain.local, <c>bob</c>) hosts the local author <c>bob</c>,
/// who can authenticate with Basic auth (the relay subscription is an Iris-specific local decision,
/// recorded from an authenticated local request at
/// <c>POST /local/v1/u/{handle}/relays/{target}</c>). Instance R (relay-lifecycle-r.example.com) hosts
/// the relay <c>relay</c>. B's outbound delivery routes to R's <c>TestServer</c> (so the fanned-out
/// <see cref="Create"/> reaches the relay's inbox), signed as bob; B's fetcher resolves the relay's
/// document from R and bob's document from B.
/// </para>
/// <para>
/// Flow:
/// <list type="number">
/// <item>bob subscribes to the relay (<c>POST /local/v1/u/bob/relays/{relay}</c>, Basic auth). B records
/// the F-06 edge in its relay store.</item>
/// <item>bob publishes a <see cref="Create"/> to his outbox. The 28.1 fan-out delivers it to the relay;
/// R's <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores the <see cref="Create"/>.</item>
/// <item>bob unsubscribes from the relay (<c>POST /local/v1/u/bob/relays/{relay}?unsubscribe=true</c>,
/// Basic auth). B removes the F-06 edge.</item>
/// <item>bob publishes a second <see cref="Create"/> to his outbox. The relay store is now empty, so no
/// relay fan-out occurs — R stores nothing for the second <see cref="Create"/>.</item>
/// </list>
/// The under-test invariant is that relay fan-out is driven by the live state of the F-06 subscription
/// edge in <c>IRelayStore</c>: while subscribed, content reaches the relay; once de-subscribed, it does
/// not.
/// </para>
/// </remarks>
[Collection("RelayLifecycle")]
public sealed class RelayLifecycleIntegrationTests : IAsyncLifetime
{
    internal const string BHost = "relay-lifecycle-b.domain.local";
    internal const string RelayHost = "relay-lifecycle-r.example.com";
    internal const string Bob = "bob";
    internal const string Relay = "relay";
    internal const string Password = "s3cret";

    private readonly RelayLifecycleSharedHost _fixture;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _relayPersistence;
    private KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _relayActorIri;

    public RelayLifecycleIntegrationTests(RelayLifecycleSharedHost fixture)
    {
        _fixture = fixture;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _relayPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _relayActorIri = new Iri($"https://{RelayHost}/ap/v1/u/{Relay}");
        _bobKey = null!;
        _bHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_bPersistence, _relayPersistence);

        _bPersistence.Keys.TryGetKey(new Iri($"{_bobActorIri.Value}#key-1"), out var bobKey);
        _bobKey = (KeyPair)bobKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores bob (on B) and the relay (on R) with their existing keys.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider bPersistence, InMemoryPersistenceProvider relayPersistence)
    {
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var relayIri = new Iri($"https://{RelayHost}/ap/v1/u/{Relay}");
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(relayPersistence, RelayHost, Relay, new Iri($"{relayIri.Value}#key-1"));
    }

    [Fact]
    public async Task RelayLifecycle_Subscribe_Post_Unsubscribe_Post()
    {
        // Step 1: bob subscribes to the relay (F-06 edge recorded in B's relay store).
        await SubscribeRelayAsync(_relayActorIri, unsubscribe: false);
        Assert.True(
            (await _bPersistence.Relays.GetRelaysAsync(_bobActorIri)).Contains(_relayActorIri),
            "B should have recorded the F-06 relay subscription edge for bob");

        // Step 2: bob publishes a Create to his outbox. The 28.1 fan-out delivers it to the relay; R
        // stores the Create.
        var firstCreate = BuildCreate(_bobActorIri, "bob's first post (while subscribed)");
        using (var request = SignedRequest(_bobActorIri, _bobKey, firstCreate, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var firstCreateIri = await LearnMintedIdAsync(response);

            // Wait for R to store the first Create (the fan-out reached the relay).
            await TestFederation.WaitForAsync(
                async () => await _relayPersistence.Activities.TryGetActivityAsync(firstCreateIri, out _),
                timeout: TimeSpan.FromSeconds(10));

            Assert.True(
                await _relayPersistence.Activities.TryGetActivityAsync(firstCreateIri, out var storedFirst),
                "R should have stored the first Create (fanned out while bob was subscribed)");
            Assert.NotNull(storedFirst);
            Assert.IsType<Create>(storedFirst);
        }

        // Step 3: bob unsubscribes from the relay (F-06 edge removed from B's relay store).
        await SubscribeRelayAsync(_relayActorIri, unsubscribe: true);
        Assert.False(
            (await _bPersistence.Relays.GetRelaysAsync(_bobActorIri)).Contains(_relayActorIri),
            "B should have removed the F-06 relay subscription edge for bob");

        // Step 4: bob publishes a second Create to his outbox. The relay store is now empty, so no relay
        // fan-out occurs — R stores nothing for the second Create.
        var secondCreate = BuildCreate(_bobActorIri, "bob's second post (after unsubscribing)");
        using (var request = SignedRequest(_bobActorIri, _bobKey, secondCreate, BHost, $"/ap/v1/u/{Bob}/outbox"))
        {
            using var response = await _bHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var secondCreateIri = await LearnMintedIdAsync(response);

            // Wait for B to record the second Create in bob's outbox (the handler ran). This gives the
            // (now-absent) relay fan-out a chance to (incorrectly) deliver, so the subsequent absence
            // assertion is not a race.
            await TestFederation.WaitForAsync(
                async () => await _bPersistence.Activities.TryGetActivityAsync(secondCreateIri, out _),
                timeout: TimeSpan.FromSeconds(10));

            // Give the delivery worker a brief window to (incorrectly) fan out — then assert R stored
            // nothing for the second Create.
            await Task.Delay(500);

            Assert.False(
                await _relayPersistence.Activities.TryGetActivityAsync(secondCreateIri, out _),
                "R should NOT have stored the second Create (bob unsubscribed from the relay)");
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Subscribes to (or unsubscribes from, when <paramref name="unsubscribe"/> is true) the relay via
    /// the local relay endpoint (<c>POST /local/v1/u/bob/relays/{target}</c>), authenticated with Basic
    /// auth. Returns the raw response for assertion.
    /// </summary>
    private async Task SubscribeRelayAsync(Iri relayIri, bool unsubscribe)
    {
        var path = $"/local/v1/u/{Bob}/relays/{relayIri.Value.TrimStart('/')}"
            + (unsubscribe ? "?unsubscribe=true" : "");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Bob}:{Password}")));
        using var response = await _bHttp.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Relay subscribe/unsubscribe should return 204, but got {(int)response.StatusCode}: {body}");
    }

    private static Create BuildCreate(Iri actorIri, string content) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        return new Iri(id!);
    }

    private HttpRequestMessage SignedRequest(Iri actorIri, KeyPair key, Activity activity, string host, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();

        using (var client = BuildClient(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
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

    internal static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    internal static IActorDocumentFetcher BuildFetcherFor(
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

    internal sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string bHost, HttpMessageHandler bHandler,
            string relayHost, HttpMessageHandler relayHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
                [relayHost] = BuildFetcherFor(relayHost, "local", signingKey, relayHandler),
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
}

/// <summary>
/// Shared two-host fixture for <see cref="RelayLifecycleIntegrationTests"/> (B:
/// relay-lifecycle-b.domain.local bob, R: relay-lifecycle-r.example.com relay). Seeds bob + the relay
/// with keys ONCE; wires B's Basic-auth credential validator + cross-wired delivery + routing fetchers
/// via <see cref="SharedHostFixture.ServerRefFor"/>.
/// </summary>
public sealed class RelayLifecycleSharedHost : SharedTwoHostFixture
{
    public RelayLifecycleSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var bPersistence = new InMemoryPersistenceProvider();
        var relayPersistence = new InMemoryPersistenceProvider();
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, RelayLifecycleIntegrationTests.BHost, RelayLifecycleIntegrationTests.Bob);
        var relaySeeded = TestSeeder.SeedPersonWithKey(relayPersistence, RelayLifecycleIntegrationTests.RelayHost, RelayLifecycleIntegrationTests.Relay);

        var serverARef = SharedHostFixture.ServerRefFor(bPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(relayPersistence);
        var bobIri = new Iri($"https://{RelayLifecycleIntegrationTests.BHost}/ap/v1/u/{RelayLifecycleIntegrationTests.Bob}");
        var relayIri = new Iri($"https://{RelayLifecycleIntegrationTests.RelayHost}/ap/v1/u/{RelayLifecycleIntegrationTests.Relay}");

        var optionsA = new ActivityPubHostOptions
        {
            Host = RelayLifecycleIntegrationTests.BHost,
            Handle = RelayLifecycleIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = RelayLifecycleIntegrationTests.BuildIdentity(bSeeded.Key, bobIri),
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) =>
                {
                    var valid = iri == bobIri
                        && username == RelayLifecycleIntegrationTests.Bob
                        && CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(password),
                            Encoding.UTF8.GetBytes(RelayLifecycleIntegrationTests.Password));
                    return new ValueTask<bool>(valid);
                }),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new RelayLifecycleIntegrationTests.RoutingFetcher(
                RelayLifecycleIntegrationTests.BHost, new LazyHandler(() => serverARef().CreateHandler()),
                RelayLifecycleIntegrationTests.RelayHost, new LazyHandler(() => serverBRef().CreateHandler()),
                bSeeded.Key, bobIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = RelayLifecycleIntegrationTests.RelayHost,
            Handle = RelayLifecycleIntegrationTests.Relay,
            Persistence = relayPersistence,
            IdentityKeys = RelayLifecycleIntegrationTests.BuildIdentity(relaySeeded.Key, relayIri),
            Fetcher = RelayLifecycleIntegrationTests.BuildFetcherFor(
                RelayLifecycleIntegrationTests.RelayHost,
                RelayLifecycleIntegrationTests.Relay,
                relaySeeded.Key,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the relay-lifecycle shared two-host fixture.
/// </summary>
[CollectionDefinition("RelayLifecycle")]
public sealed class RelayLifecycleCollection : ICollectionFixture<RelayLifecycleSharedHost>
{
}
