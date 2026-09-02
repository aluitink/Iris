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
/// Phase 12 integration test for <strong>F-15</strong>: the outbox-publish <c>Announce</c> path
/// (<c>POST /ap/v1/u/{handle}/outbox</c>) must federate the boost to <em>every</em> remote, non-blocked
/// follower, mirroring the <c>Create</c> fan-out. Previously an <c>Announce</c> published to the
/// outbox was recorded locally but never delivered to any remote follower (the <c>OutboxPublishHandler</c>
/// had no <c>Announce</c> branch — it fell through to the single-recipient path where
/// <c>recipientIri</c> was <see langword="null"/>, so no delivery was scheduled).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts author <c>alice</c>. Instance B (b.domain.local,
/// <c>bob</c>) hosts a remote follower. The follow edge bob→alice is recorded on A. Alice publishes
/// an <see cref="Announce"/> to her own outbox (signed as alice); A's server records it and
/// server-delivers the signed <c>Announce</c> to bob's inbox. B validates the signature (fetching A's
/// actor doc for alice's key) and stores the <c>Announce</c> in bob's inbox.
/// </remarks>
public sealed class OutboxAnnounceFanOutIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public OutboxAnnounceFanOutIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        // The follow edge bob→alice is recorded on A (A is alice's home instance).
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

        _a = StartAuthorServer(_aPersistence, _aliceKey, _aliceActorIri,
            bServer: () => _b!, selfServer: () => _a!);
        _b = StartServer(BHost, Bob, _bPersistence, bSeeded.Key,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A boost published to the author's outbox is federated to the remote follower --------

    [Fact]
    public async Task OutboxPublish_AnnounceWithRemoteFollower_FederatesToFollower()
    {
        var announce = BuildAnnounce(_aliceActorIri);

        // Sign the request as alice (the outbox-publish write surface requires a valid signature from
        // the acting actor) and POST to A's /ap/v1/u/alice/outbox. Decision 055: the announce is id-less;
        // the server mints the id and returns the created activity in the 202 body.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // A recorded the Announce in alice's outbox under its server-minted id (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // B validated the federated Announce (resolving alice's key from A's actor doc) and stored it
        // — the boost reached the remote follower's instance (F-15: outbound Announce federation).
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(15));

        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B should have stored the Announce federated by A's server (signed as alice)");
        Assert.IsType<Announce>(storedB);
    }

    // --- A boost with no remote followers is surfaced locally but not federated --------------

    [Fact]
    public async Task OutboxPublish_AnnounceWithNoRemoteFollowers_SurfacesLocallyOnly()
    {
        // A fresh author (dave) with no followers posts an Announce to his outbox; it is recorded in
        // dave's outbox but nothing is scheduled for delivery (no remote followers).
        var daveSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, "dave");
        var daveActorIri = daveSeeded.ActorIri;

        var announce = BuildAnnounce(daveActorIri);
        using var request = SignedRequest(daveActorIri, daveSeeded.Key, announce, "/ap/v1/u/dave/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // Surfaced in dave's outbox under its server-minted id ...
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(daveActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // ... and nothing was federated (no followers) — B stored nothing.
        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _));
    }

    // --- A local outbox Announce records the per-object boost edge (decision 056 (d)) --------

    [Fact]
    public async Task OutboxPublish_Announce_RecordsLocalBoostEdge()
    {
        // Regression: the outbox-publish Announce path (OutboxPublishHandler) must record the local
        // announcer → object edge in the announce store (the per-object boost counter, decision 056 (d)),
        // exactly like the outbox Like path records the like edge (RecordLikeLocalAsync). Before the
        // fix, the Announce branch only fanned out to remote followers and never recorded the edge, so
        // the object's /shares collection (totalItems) stayed 0 even after a local boost.
        var announce = BuildAnnounce(_aliceActorIri);
        var announcedObjectIri = announce.Object!.FirstOrDefault().ResolveObjectIri()
            ?? throw new InvalidOperationException("test announce must carry a resolvable object");

        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The local boost edge is recorded: alice → announced object, and the reverse index surfaces
        // alice as the announcer (the object's shares counter).
        Assert.True(
            await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, announcedObjectIri),
            "the outbox Announce must record the local announcer → object edge");
        var announcers = await _aPersistence.Announces.GetAnnouncersAsync(announcedObjectIri);
        Assert.True(
            announcers.Any(i => i.Value == _aliceActorIri.Value),
            "the object's shares reverse index must list the announcer");
    }

    // --- An outbox Undo(Announce) removes the local boost edge --------------------------------

    [Fact]
    public async Task OutboxPublish_UndoAnnounce_RemovesLocalBoostEdge()
    {
        // Regression: an Undo of an Announce published to the actor's own outbox must remove the local
        // boost edge (RecordUndoLocalAsync → RemoveAnnounceLocalAsync), mirroring the Undo(Like) path.
        var announce = BuildAnnounce(_aliceActorIri);
        var announcedObjectIri = announce.Object!.FirstOrDefault().ResolveObjectIri()
            ?? throw new InvalidOperationException("test announce must carry a resolvable object");

        // First boost: records the local edge.
        using (var req1 = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox"))
        using (var res1 = await _aHttp.SendAsync(req1))
        {
            Assert.Equal(HttpStatusCode.Accepted, res1.StatusCode);
            var mintedBoost = await LearnMintedIdAsync(res1);

            Assert.True(await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, announcedObjectIri));

            // Now undo the boost (an Undo referencing the minted boost activity) via the same outbox.
            var undo = new Undo
            {
                Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
                Object = [new Link { Href = new Uri(mintedBoost.Value) }],
            };
            using var req2 = SignedRequest(_aliceActorIri, _aliceKey, undo, $"/ap/v1/u/{Alice}/outbox");
            using var res2 = await _aHttp.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Accepted, res2.StatusCode);

            // The local boost edge is removed (the object's shares counter drops back to 0).
            Assert.False(await _aPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, announcedObjectIri));
            var afterUndo = await _aPersistence.Announces.GetAnnouncersAsync(announcedObjectIri);
            Assert.True(
                !afterUndo.Any(i => i.Value == _aliceActorIri.Value),
                "the object's shares reverse index must not list the announcer after Undo(Announce)");
        }
    }

    // --- A blocked remote follower does not receive the federated Announce -------------------

    [Fact]
    public async Task OutboxPublish_AnnounceWithBlockedRemoteFollower_SkipsBlocked()
    {
        // Bob blocks alice (recorded on A). Alice publishes an Announce; bob (blocked) does not
        // receive it.
        await _aPersistence.Moderation.RecordBlockAsync(_bobActorIri, _aliceActorIri);

        var announce = BuildAnnounce(_aliceActorIri);
        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // Alice's outbox recorded the Announce under its server-minted id (local surfacing is
        // independent of block).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // Bob (blocked) did NOT receive the federated Announce.
        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _));
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
        Func<TestServer> bServer, Func<TestServer> selfServer)
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
            DeliveryTransport = () => new LazyHandler(bServer),
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => selfServer().CreateHandler()),
                BHost, new LazyHandler(() => bServer().CreateHandler()),
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
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents
    /// based on the actor IRI's host (A's fetcher needs to reach A and B to validate alice's own
    /// signature and resolve bob's inbox).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
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

    private static Announce BuildAnnounce(Iri actorIri)
    {
        // Decision 055: the client sends the activity shape WITHOUT an id; the server mints the id and
        // returns the created activity (with its minted id) in the 202 body. The test learns the id from
        // the response (LearnMintedIdAsync), mirroring the real client's DeliveryResult.MintedId flow.
        var objectIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        return new Announce
        {
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
        };
    }

    /// <summary>
    /// Learns the server-minted id of an activity from the 202 outbox-publish response body (decision
    /// 055): the server is the sole id authority and returns the created activity (with its minted id)
    /// in the 202 body. This mirrors the real client's <c>DeliveryResult.MintedId</c> flow.
    /// </summary>
    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
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
}
