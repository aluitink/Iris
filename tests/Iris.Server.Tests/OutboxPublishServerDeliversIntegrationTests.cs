using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.3 integration test: <em>post-interact, server-delivers</em>. The client publishes an
/// activity to the author's own outbox (<c>POST /ap/v1/u/{handle}/outbox</c>) — a single signed POST to
/// its <em>own</em> instance — and the <strong>server</strong> performs the cross-instance delivery to the
/// recipient's inbox (signed as the acting actor), not the client. The peer validates the signature
/// (fetching the acting actor's document from the author's instance) and records the activity's effect;
/// an activity the peer did <em>not</em> validate (a bad or wrong-actor signature) would be rejected and
/// never recorded.
/// </summary>
/// <remarks>
/// This pins the single-recipient delivery types that were not previously covered end-to-end (Follow,
/// Create, and Announce fan-out are pinned by their own tests). A <see cref="Block"/> is the cleanest
/// single-recipient case: the recipient is the blocked actor's IRI directly (no owner-resolution), so
/// alice (instance A) blocking bob (instance B) is delivered to bob's inbox by A's server, signed as
/// alice. B validates the signature against alice's actor document (fetched from A) and records the
/// alice → bob block edge — which only happens if the signature validated as alice.
/// </remarks>
/// <para>
/// Topology: instance A (block-a.domain.local, <c>alice</c>) and instance B (block-b.domain.local,
/// <c>bob</c>). A's outbound delivery worker routes to B; A's fetcher routes by actor-IRI host (alice →
/// A, bob → B). B's fetcher routes to A (to fetch alice's document and validate the signature of the
/// federated Block). The client's write is a single signed POST to A's own outbox; the cross-instance
/// hop (A → B) is made by A's server, not the client.
/// </para>
[Collection("OutboxPublishServerDelivers")]
public sealed class OutboxPublishServerDeliversIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "block-a.domain.local";
    internal const string BHost = "block-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";

    private readonly OutboxPublishServerDeliversSharedHost _fixture;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public OutboxPublishServerDeliversIntegrationTests(OutboxPublishServerDeliversSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _aliceKey = null!;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

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
    /// Restores alice (on A) + bob (on B) with their existing keys.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
    }

    // --- A Block published to the author's outbox is server-delivered to the blocked actor ----
    //
    // The client makes a single signed POST to A's own outbox. A's server (not the client) delivers the
    // Block to bob's inbox, signed as alice. B validates the signature against alice's actor document
    // (fetched from A) and records the alice → bob block edge — the edge is only recorded if the
    // signature validated as alice (a wrong-actor or invalid signature would be rejected by B's
    // signature gate and never recorded).

    [Fact]
    public async Task OutboxPublish_Block_ServerDeliversToBlockedActorInbox_SignedAsActingActor()
    {
        var block = BuildBlock(_aliceActorIri, _bobActorIri);

        // The client's write: a single signed POST to A's own outbox (the client never addresses bob's
        // inbox directly — that cross-instance hop is the server's job).
        using var request = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Decision 055: A minted the Block's id; learn it from the 2xx body.
        var mintedIdNullable = await LearnMintedIdAsync(response);
        Assert.True(mintedIdNullable != null, "A should have returned the minted Block id in the 2xx body.");
        Iri mintedId = mintedIdNullable.Value;

        // A recorded the Block in alice's outbox (the local-surfacing half), under the MINTED id.
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // B recorded the block edge (alice → bob): the cross-instance half. B only records it after
        // validating the Block's signature as alice (resolving alice's key from A's actor document) —
        // the proof that A's server delivered the activity, signed as the acting actor.
        await WaitForAsync(
            () => _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            "B should record the alice → bob block edge after A's server delivered the signed Block");
    }

    // --- The client's write never made the cross-instance POST -------------------------------
    //
    // The same Block is published again, and we verify A's delivery queue drained it to B (the
    // server-side hop) while the client's request only ever reached A. A fresh bob2 (on B) who is not
    // the blocked target receives nothing — confirming delivery was directed to the blocked actor by the
    // server, not broadcast by the client.

    [Fact]
    public async Task OutboxPublish_Block_DeliveredOnlyToTheBlockedActor_NotBroadcast()
    {
        // A second local actor on B (carol) who is NOT the blocked target: she must not receive the
        // Block (the server delivers to the single blocked recipient, not every actor on the peer).
        var (_, carolIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, "carol");

        var block = BuildBlock(_aliceActorIri, _bobActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, block, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The blocked actor (bob) receives it (the edge is recorded on B).
        await WaitForAsync(
            () => _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, _bobActorIri));

        // The non-target actor (carol) does NOT: the server delivered to bob's inbox only, not to
        // carol's (no broadcast — a single directed delivery, as the recipient resolution dictates).
        Assert.False(
            await _bPersistence.Moderation.IsBlockedAsync(_aliceActorIri, carolIri),
            "the Block must not be recorded for carol (she is not the blocked target)");
    }

    // --- Helpers --------------------------------------------------------------------------

    internal static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

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

    private static Block BuildBlock(Iri blockerIri, Iri blockedIri) => new()
    {
        // Decision 055: the client sends the Block's shape (no id); the server mints the id and returns
        // it in the 2xx body.
        Actor = [new Link { Href = new Uri(blockerIri.Value) }],
        Object = [new Link { Href = new Uri(blockedIri.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id from a 2xx response body (decision 055: the server returns the
    /// created object in the 2xx body). Returns null when the body is empty or carries no id.
    /// </summary>
    private static async Task<Iri?> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var activity = ActivityJson.Deserialize<IObjectOrLink>(body) as Activity;
        var id = activity?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : new Iri(id);
    }

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
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (A's fetcher needs to reach A and B).
    /// </summary>
    internal sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
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
}

/// <summary>
/// Shared two-host fixture for <see cref="OutboxPublishServerDeliversIntegrationTests"/>
/// (A: block-a.domain.local alice, B: block-b.domain.local bob). Seeds alice + bob with keys ONCE; A has
/// identity + a routing fetcher (fetches A's and B's actor docs) + delivery transport to B; B has a
/// fetcher to A (validates the federated Block signed as alice).
/// </summary>
public sealed class OutboxPublishServerDeliversSharedHost : SharedTwoHostFixture
{
    public OutboxPublishServerDeliversSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, OutboxPublishServerDeliversIntegrationTests.AHost, OutboxPublishServerDeliversIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, OutboxPublishServerDeliversIntegrationTests.BHost, OutboxPublishServerDeliversIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = OutboxPublishServerDeliversIntegrationTests.AHost,
            Handle = OutboxPublishServerDeliversIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = OutboxPublishServerDeliversIntegrationTests.BuildIdentity(aSeeded.Key, aSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new OutboxPublishServerDeliversIntegrationTests.RoutingFetcher(
                OutboxPublishServerDeliversIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                OutboxPublishServerDeliversIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = OutboxPublishServerDeliversIntegrationTests.BHost,
            Handle = OutboxPublishServerDeliversIntegrationTests.Bob,
            Persistence = bPersistence,
            Fetcher = OutboxPublishServerDeliversIntegrationTests.BuildFetcherFor(
                OutboxPublishServerDeliversIntegrationTests.AHost,
                OutboxPublishServerDeliversIntegrationTests.Alice,
                aSeeded.Key,
                new LazyHandler(() => serverARef().CreateHandler())),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the outbox-publish-server-delivers shared two-host fixture.
/// </summary>
[CollectionDefinition("OutboxPublishServerDelivers")]
public sealed class OutboxPublishServerDeliversCollection : ICollectionFixture<OutboxPublishServerDeliversSharedHost>
{
}
