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
public sealed class OutboxAudienceMatchIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string CHost = "c.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    /// <summary>The ActivityStreams public collection address (the conventional <c>to</c> for public notes).</summary>
    private static readonly Iri AsPublic = Iri.Public;

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

    public OutboxAudienceMatchIntegrationTests()
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

        // The follow edge bob→alice is recorded on A (A is alice's home instance). NOTE: carol does NOT
        // follow alice — carol is a remote non-follower, so she must not receive alice's posts.
        _aPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

        // A's delivery transport routes to B or C by request host header. A's fetcher routes by actor
        // IRI host: alice (A) → A's TestServer, bob (B) → B's, carol (C) → C's. B and C's fetchers are
        // wired to A (to validate alice's key on the federated activities).
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

    // --- A public Create is delivered to the follower (carrying as:Public) and NOT the non-follower ---

    [Fact]
    public async Task OutboxPublish_PublicCreate_FollowerReceivesWithAsPublic_NonFollowerDoesNot()
    {
        // A public post: the Note carries the as:Public address in its `to` (the conventional public
        // audience), exactly as the client compose surface sets it.
        var create = BuildPublicCreate(_aliceActorIri);

        // Sign the request as alice (the outbox-publish write surface requires a valid signature from
        // the acting actor) and POST to A's /ap/v1/u/alice/outbox.
        using var request = SignedRequest(_aliceActorIri, _aliceKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A recorded the Create in alice's outbox (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // The follower (bob, on B) received the federated Create (signed as alice).
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var storedB),
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
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            "C (a remote non-follower) must NOT have stored alice's Create — delivery recipients are the audience (followers)");
    }

    // --- A boost is NOT delivered to a remote non-follower -----------------------------------------

    [Fact]
    public async Task OutboxPublish_Announce_NonFollowerDoesNotReceive()
    {
        // A boost of a local note (the Announce's Object is the boosted note's IRI).
        var announce = BuildAnnounce(_aliceActorIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A recorded the Announce in alice's outbox (the local-surfacing half).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == announce.Id);

        // The follower (bob, on B) received the federated Announce (signed as alice).
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out _),
            timeout: TimeSpan.FromSeconds(15));
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out var storedB),
            "B (the follower) should have stored the Announce federated by A's server");
        Assert.IsType<Announce>(storedB);

        // The non-follower (carol, on C) did NOT receive the boost — the audience is the follower set.
        await Task.Delay(300);
        Assert.False(
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out _),
            "C (a remote non-follower) must NOT have stored alice's Announce — delivery recipients are the audience (followers)");
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
    /// header (A's delivery worker sends to the followers' inboxes, which are on different instances).
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
    /// Captures a signed request (its body + headers) instead of forwarding it, so the signed body can
    /// be replayed through a plain <see cref="HttpClient"/>.
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
    /// Builds a public <see cref="Create"/>: the embedded <see cref="Note"/> carries the
    /// <c>as:Public</c> address in its <c>to</c> (the conventional public audience), exactly as the
    /// client compose surface sets it for a public post.
    /// </summary>
    private static Create BuildPublicCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}",
                Content = ["a public post addressed to everyone"],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
                To = [new Link { Href = new Uri(AsPublic.Value) }],
            },
        ],
    };

    private static Announce BuildAnnounce(Iri actorIri)
    {
        var objectIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        return new Announce
        {
            Id = AnnounceIris.AnnounceIri(actorIri, objectIri).Value,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
        };
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
