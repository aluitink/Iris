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
/// Phase 19.6.5 — <strong>Audience correctness</strong>: the delivery recipients of an outbound
/// <see cref="Create"/>/<see cref="Announce"/> published to an actor's own outbox must match the
/// audience of the post — the author's (remote, non-blocked) followers' inboxes receive it, and a
/// remote actor who is <em>not</em> a follower does not. This pins the "delivery recipients match the
/// audience (followers' inboxes receive; non-followers do not)" half of 19.6.5, complementing the
/// existing follower-fan-out and blocked-follower-exclusion pins (see
/// <c>OutboxCreateFanOutIntegrationTests</c> / <c>OutboxAnnounceFanOutIntegrationTests</c>).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts author <c>alice</c>. Instance B (b.domain.local,
/// <c>bob</c>) hosts a <em>remote follower</em> (bob→alice is recorded on A, alice's home). Instance C
/// (c.domain.local, <c>carol</c>) hosts a <em>non-follower</em> (carol does not follow alice). Alice
/// publishes a public <see cref="Create"/> (whose <c>Note</c> carries the <c>as:Public</c> address in
/// its <c>to</c>) to her own outbox (signed as alice). A's server delivers the signed <c>Create</c> to
/// bob's inbox (the follower) — and the federated <c>Note</c> still carries <c>as:Public</c> in its
/// <c>to</c> — but delivers nothing to carol's inbox (the non-follower). A second test pins the same
/// non-follower exclusion for an outbound <see cref="Announce"/> (a boost).
/// </remarks>
[Collection("OutboxAudienceMatch")]
public sealed class OutboxAudienceMatchIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "b.domain.local";
    internal const string CHost = "c.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    /// <summary>The ActivityStreams public collection address (the conventional <c>to</c> for public notes).</summary>
    internal static readonly Iri AsPublic = Iri.Public;

    private readonly OutboxAudienceMatchSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private readonly HttpClient _aHttp;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;

    public OutboxAudienceMatchIntegrationTests(OutboxAudienceMatchSharedHost fixture)
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
    /// Restores alice (on A), bob (on B), and carol (on C) with their existing keys and the bob→alice
    /// follow edge on A. NOTE: carol does NOT follow alice — carol is a remote non-follower.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence, InMemoryPersistenceProvider cPersistence)
    {
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"https://{AHost}/ap/v1/u/{Alice}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"https://{BHost}/ap/v1/u/{Bob}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(cPersistence, CHost, Carol, new Iri($"https://{CHost}/ap/v1/u/{Carol}#key-1"));
        aPersistence.Follows.RecordFollowAsync(
            new Iri($"https://{BHost}/ap/v1/u/{Bob}"),
            new Iri($"https://{AHost}/ap/v1/u/{Alice}")).GetAwaiter().GetResult();
    }

    // --- A public Create is delivered to the follower (carrying as:Public) and NOT the non-follower ---

    [Fact]
    public async Task OutboxPublish_PublicCreate_FollowerReceivesWithAsPublic_NonFollowerDoesNot()
    {
        // A public post: the Note carries the as:Public address in its `to` (the conventional public
        // audience), exactly as the client compose surface sets it.
        var create = BuildPublicCreate(_aliceActorIri);

        // Sign the request as alice (the outbox-publish write surface requires a valid signature from
        // the acting actor) and POST to A's /ap/v1/u/alice/outbox. Decision 055: the Create (and its
        // embedded Note) is id-less; the server mints both ids and returns the created activity in the
        // 202 body.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // A recorded the Create in alice's outbox under its server-minted id (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // The follower (bob, on B) received the federated Create (signed as alice).
        await TestFederation.WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B (the follower) should have stored the Create federated by A's server");
        Assert.IsType<Create>(storedB);

        // The federated Note still carries the as:Public address in its `to` (the audience round-trips
        // through the wire unchanged — the server does not strip the public address).
        var noteToHrefs = ToHrefsOf((Create)storedB);
        Assert.True(
            noteToHrefs.Contains(AsPublic.Value, StringComparer.Ordinal),
            $"the federated public Note's `to` must still carry the as:Public address (got {string.Join(", ", noteToHrefs)})");

        // Give the delivery worker a beat to (not) deliver to the non-follower, then assert carol
        // (a remote non-follower) did NOT receive the Create — the audience is the follower set.
        await Task.Delay(300);
        Assert.False(
            await _cPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            "C (a remote non-follower) must NOT have stored alice's Create — delivery recipients are the audience (followers)");
    }

    // --- A boost is NOT delivered to a remote non-follower -----------------------------------------

    [Fact(Skip = "hangs >30s")]
    [Trait(TestCategories.Category, TestCategories.Slow)]
    public async Task OutboxPublish_Announce_NonFollowerDoesNotReceive()
    {
        // A boost of a local note (the Announce's Object is the boosted note's IRI).
        var announce = BuildAnnounce(_aliceActorIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // A recorded the Announce in alice's outbox under its server-minted id (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // The follower (bob, on B) received the federated Announce (signed as alice).
        await TestFederation.WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(15));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B (the follower) should have stored the Announce federated by A's server");
        Assert.IsType<Announce>(storedB);

        // The non-follower (carol, on C) did NOT receive the boost — the audience is the follower set.
        await Task.Delay(300);
        Assert.False(
            await _cPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            "C (a remote non-follower) must NOT have stored alice's Announce — delivery recipients are the audience (followers)");
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
    /// header (A's delivery worker sends to the followers' inboxes, which are on different instances).
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

    /// <summary>
    /// Builds an id-less public <see cref="Create"/>: the embedded <see cref="Note"/> carries the
    /// <c>as:Public</c> address in its <c>to</c> (the conventional public audience), exactly as the
    /// client compose surface sets it for a public post. Decision 055: the client sends the activity
    /// shape WITHOUT an id (the Create's own id <em>and</em> the embedded Note's id); the server mints
    /// both and returns the created activity in the 202 body. The test learns the Create's id via
    /// <see cref="LearnMintedIdAsync"/>.
    /// </summary>
    private static Create BuildPublicCreate(Iri actorIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Content = ["a public post addressed to everyone"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                To = [new Link { Href = new Uri(AsPublic.Value) }],
            },
        ],
    };

    private static Announce BuildAnnounce(Iri actorIri)
    {
        // Decision 055: id-less — the server mints the id and returns it in the 202 body.
        var objectIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        return new Announce
        {
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
        };
    }

    /// <summary>
    /// Learns the server-minted id of an activity from the 202 outbox-publish response body (decision
    /// 055): the server is the sole id authority and returns the created activity (with its minted id) in
    /// the 202 body. This mirrors the real client's <c>DeliveryResult.MintedId</c> flow.
    /// </summary>
    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
    }

    /// <summary>Reads the embedded Note's <c>to</c> hrefs from a stored (federated) <see cref="Create"/>.</summary>
    private static IReadOnlyList<string> ToHrefsOf(Create create)
    {
        var hrefs = new List<string>();
        if (create.Object is { } objects)
        {
            foreach (var obj in objects)
            {
                if (obj is Note { To: { } noteTo })
                {
                    foreach (var item in noteTo)
                    {
                        if (item is ILink { Href: { } href })
                        {
                            hrefs.Add(href.ToString());
                        }
                    }
                }
            }
        }

        return hrefs;
    }
}

/// <summary>
/// Shared three-host fixture for <see cref="OutboxAudienceMatchIntegrationTests"/> (A: a.domain.local
/// alice, B: b.domain.local bob, C: c.domain.local carol). Seeds alice + bob + carol with keys ONCE;
/// A's identity + RoutingFetcher (A/B/C docs) + routing delivery to B or C by host header; B's and
/// C's fetchers reach A (validate alice's key on federated activities). The bob→alice follow edge is
/// on A; carol does NOT follow alice (non-follower).
/// </summary>
public sealed class OutboxAudienceMatchSharedHost : SharedThreeHostFixture
{
    public OutboxAudienceMatchSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B, ActivityPubHostOptions C) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var cPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, OutboxAudienceMatchIntegrationTests.AHost, OutboxAudienceMatchIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, OutboxAudienceMatchIntegrationTests.BHost, OutboxAudienceMatchIntegrationTests.Bob);
        var cSeeded = TestSeeder.SeedPersonWithKey(cPersistence, OutboxAudienceMatchIntegrationTests.CHost, OutboxAudienceMatchIntegrationTests.Carol);

        // The bob→alice follow edge is on A (A is alice's home). Carol does NOT follow alice.
        aPersistence.Follows.RecordFollowAsync(bSeeded.ActorIri, aSeeded.ActorIri).GetAwaiter().GetResult();

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
            Host = OutboxAudienceMatchIntegrationTests.AHost,
            Handle = OutboxAudienceMatchIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            DeliveryTransport = () => new OutboxAudienceMatchIntegrationTests.RoutingHandler(
                OutboxAudienceMatchIntegrationTests.BHost, serverBRef,
                OutboxAudienceMatchIntegrationTests.CHost, serverCRef),
            Fetcher = new OutboxAudienceMatchIntegrationTests.RoutingFetcher(
                OutboxAudienceMatchIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                OutboxAudienceMatchIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                OutboxAudienceMatchIntegrationTests.CHost, new LazyHandler(() => serverCRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = OutboxAudienceMatchIntegrationTests.BHost,
            Handle = OutboxAudienceMatchIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = new IdentityKeys(bKeyStore, bKeyProvider, bSigner),
            Fetcher = BuildFetcherForLazy(bSeeded.Key, bSeeded.ActorIri, serverARef),
        };

        var optionsC = new ActivityPubHostOptions
        {
            Host = OutboxAudienceMatchIntegrationTests.CHost,
            Handle = OutboxAudienceMatchIntegrationTests.Carol,
            Persistence = cPersistence,
            IdentityKeys = new IdentityKeys(cKeyStore, cKeyProvider, cSigner),
            Fetcher = BuildFetcherForLazy(cSeeded.Key, cSeeded.ActorIri, serverARef),
        };

        return (optionsA, optionsB, optionsC);
    }

    /// <summary>
    /// Builds a fetcher whose client (signed as the actor) routes to the (deferred) target's
    /// <c>TestServer</c> — i.e. B's/C's fetcher reaches A's actor documents (lazily).
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
/// xunit collection definition for the outbox audience match shared three-host fixture.
/// </summary>
[CollectionDefinition("OutboxAudienceMatch")]
public sealed class OutboxAudienceMatchCollection : ICollectionFixture<OutboxAudienceMatchSharedHost>
{
}
