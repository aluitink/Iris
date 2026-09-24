using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// S66 regression test: a federated boost's count must be correct on the RECEIVING instance (the
/// instance that records the inbound <c>Announce</c> edge for a remote object). The receiving instance
/// pre-computes the per-object counters (<c>iris:sharedCount</c> / <c>iris:likedCount</c> / ...) from its
/// own reverse indexes and persists them onto the stored object document
/// (<see cref="Iris.Server.Stores.ObjectInteractionCountRefreshService"/>), and the read paths serve the
/// pre-computed counters when present (falling back to a per-read reverse-index sweep only when they are
/// absent).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The bug.</strong> A remote object is stored in the receiving instance's object store on a
/// proxied fetch (the 75.3 sync). When a local actor later boosts the remote object, the inbound
/// <c>Announce</c> handler records the announce edge in the local reverse index and immediately refreshes
/// the stored document's <c>iris:sharedCount</c> to 1. But the NEXT proxied read of the object re-fetches
/// the remote's copy (a stale <c>iris:fetchedAt</c> freshness mark, or a content update) and re-stores it
/// (the 75.3 sync) — and a plain re-store CLOBBERS the stored document: the server-local
/// <c>iris:sharedCount</c> of 1 disappears, so the next read of the object document serves the boost count
/// as 0 (S66) instead of the 1 the instance recorded — until the next 30 s interval pass re-derives it
/// (or permanently, when the instance disables the periodic pass).
/// </para>
/// <para>
/// <strong>The fix.</strong> The proxy re-store path now carries the server-local interaction counters
/// (the <c>iris:</c> extensions persisted by the count-refresh service) from the previously stored copy
/// onto the freshly fetched remote document before re-storing it. The counters are server-LOCAL state
/// (computed from this instance's own reverse indexes — the inbound Like / Announce / Reply edges — and
/// re-derived on every edge change by the inbox handlers), NOT part of the remote object's canonical
/// document, so they must survive a re-fetch of the remote's copy.
/// </para>
/// <para>
/// <strong>Topology.</strong> A (a66-a.domain.local, <c>bob</c>) hosts the remote note (the object's home).
/// B (a66-b.domain.local, <c>alice</c>) is the receiving instance. The flow:
/// <list type="number">
/// <item>alice follows bob (federated), so bob's posts reach B;</item>
/// <item>bob posts the note (a <c>Create</c> published to A's outbox, delivered to alice's inbox on B);
/// B's <see cref="Iris.Server.Inbox.CreateActivityHandler"/> stores the embedded note in B's object store
/// (the receiving instance's copy, the 75.3 sync);</item>
/// <item>alice boosts the note (an <c>Announce</c> published to B's outbox, delivered to bob's inbox on
/// A); B's <see cref="Iris.Server.Inbox.AnnounceActivityHandler"/> records the announce edge in B's
/// reverse index and immediately refreshes the stored note's <c>iris:sharedCount</c> to 1;</item>
/// </list>
/// The test then re-reads the note document on B and asserts <c>iris:sharedCount</c> is still 1 (not 0)
/// — i.e. the server-local counter survives a proxied re-store of the remote's copy.
/// </para>
/// </remarks>
public sealed class S66FederatedBoostCountRegressionTests : IDisposable
{
    private const string AHost = "a66-a.domain.local";
    private const string BHost = "a66-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string IrisNamespace = "https://iris.example/ns#";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public S66FederatedBoostCountRegressionTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;
        _bobKey = aSeeded.Key;
        _bobActorIri = aSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: the object's home. Serves bob's actor doc (B fetches it to resolve bob's key when validating
        // the follow and the announce) and bob's note (B fetches it to resolve the note's author when
        // publishing the boost). No IdentityKeys override: the factory binds A's IKeyStore to
        // _aPersistence.Keys (where bob's key is seeded), so A's DeliveryWorker can sign as bob for the
        // inbox delivery (and the actor-document route serves bob's document with the seeded publicKey).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            // A's inbound fetcher routes by actor-IRI host: bob (A) → A, alice (B) → B. This is what
            // lets A's RemoteInboundKeyResolver validate a signature signed by alice (B) when B
            // delivers the follow/announce to bob's inbox (A).
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
                BHost, new LazyHandler(() => bServerHolder.Server!.CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
            // A's DeliveryWorker delivers bob's Create to alice's inbox on B (bob's remote follower).
            DeliveryTransport = () => new LazyHandler(() => bServerHolder.Server!.CreateHandler()),
        });

        // B: the receiving instance. Its inbound fetcher routes by actor-IRI host: alice (B) → B,
        // bob (A) → A. The alice → B leg is the one that resolves alice's key when B's
        // OutboxPublishHandler validates the signed outbox POST (a LOCAL signer's key, resolvable
        // without a cross-instance hop because the fetcher reaches B itself). The bob → A leg resolves
        // bob's key when B stores the note and validates the announce.
        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Alice,
            Persistence = _bPersistence,
            Fetcher = new RoutingFetcher(
                AHost, new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
                BHost, new LazyHandler(() => bServerHolder.Server!.CreateHandler()),
                bSeeded.Key, bSeeded.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
            Client = BuildClient(BHost, Alice, bSeeded.Key, () => aServerHolder.Server!),
        });

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{AHost}") };
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{BHost}") };

        // Follow bob (A) from alice (B) so the note federates to B's outbox (and B stores the note).
        var follow = new Follow
        {
            Id = $"https://{BHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_bobActorIri.Value) }],
        };
        using var followRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, follow, $"/ap/v1/u/{Alice}/outbox");
        using var followResponse = _bHttp.SendAsync(followRequest).GetAwaiter().GetResult();
        Assert.True(followResponse.StatusCode == HttpStatusCode.Accepted,
            "the follow POST to B's outbox should be 202 (signature validated as alice), got "
            + followResponse.StatusCode);

        // Wait on the EFFECT of the follow: BOTH the local edge on B (B's FollowActivityHandler) AND the
        // remote edge on A (A's FollowActivityHandler, when B's signed follow delivery arrives). The edge
        // on A is the prerequisite for the Create federation (bob's CreateActivityHandler delivers the
        // post to alice, bob's remote follower, only when A records the follow edge), so waiting on it
        // here makes the post step in the test deterministic (the Create delivery cannot race the follow).
        WaitForAsync(async () =>
            await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri)
            && await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The S66 regression: a federated boost's count stays correct on the receiving instance ---

    [Fact]
    public async Task FederatedBoost_ReceiverObjectDoc_SharedCountStaysOne_AcrossProxyRestores()
    {
        // The follow was published in the constructor (it federates alice→bob so bob's posts reach B).

        // Step 1: bob (A) posts the note. A's outbox publish records the Create in bob's outbox and
        // delivers it to alice (bob's remote follower) on B. B's CreateActivityHandler stores the embedded
        // note in B's object store (the receiving instance's copy — the 75.3 sync).
        var objectIri = new Iri($"https://{AHost}/ap/v1/objects/m1-{Guid.NewGuid():N}");
        var create = new Create
        {
            Actor = [new Link { Href = new Uri(_bobActorIri.Value) }],
            Object = [new Note
            {
                Id = objectIri.Value,
                Content = ["a post on the remote instance (A)"],
                AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
                // Public: the note's audience includes the public collection, so B's visibility gate
                // serves the note document to an unsigned read (a non-public note would 404).
                To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
            }],
        };
        using var createRequest = SignedOutboxRequest(_bobActorIri, _bobKey, create, $"/ap/v1/u/{Bob}/outbox");
        using var createResponse = await _aHttp.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);

        // Wait on the EFFECT of the post on B (B storing the note): the Create's embedded note is stored
        // in B's object store by B's CreateActivityHandler when the federated Create arrives.
        await WaitForAsync(async () =>
            await _bPersistence.Objects.TryGetObjectAsync(objectIri, out _),
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(objectIri, out _),
            "B (the receiving instance) should have stored the federated note (the 75.3 sync)");

        // Step 2: alice (B) boosts the note via a signed POST to B's outbox (the local-outbox publish
        // path). B resolves the note's author (bob) by reading the stored note's attributedTo, delivers
        // the Announce to bob (A) — the object's home. B's AnnounceActivityHandler records the announce
        // edge in its local reverse index and immediately refreshes the stored note's iris:sharedCount to
        // 1 (the S28/S37 immediate refresh).
        var announce = BuildAnnounce(_aliceActorIri, objectIri,
            new Iri($"https://{BHost}/activities/announce-{Guid.NewGuid():N}"));
        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait on the EFFECT of the boost on B (B recording the announce edge for the note): the inbound
        // Announce handler's immediate count refresh (which sets the stored note's iris:sharedCount to 1)
        // runs in the same handler, right after the edge is recorded, so by the time the edge is
        // observable the count is persisted.
        await WaitForAsync(async () =>
            await _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, objectIri),
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(
            await _bPersistence.Announces.HasAnnouncedAsync(_aliceActorIri, objectIri),
            "B (the receiving instance) should have recorded the federated boost");

        // The S66 trigger: a proxied re-read of the note (the 75.3 sync). The note was stored by B's
        // CreateActivityHandler (the federated post), which does NOT stamp the server-internal
        // iris:fetchedAt freshness mark (only the proxy does), so the proxy's cache-first read (step 3c)
        // treats it as STALE and live-fetches A's copy — then re-stores it. WITHOUT the fix, that
        // re-store CLOBBERS the server-local iris:sharedCount (which the boost's immediate refresh set to
        // 1): the remote's copy carries no iris: extensions, so the stored document's sharedCount is
        // dropped. WITH the fix, the re-store carries the counter over, so the stored document keeps it.
        //
        // The anonymous proxy seam (S2/S14) relays an unsigned GET read for a signed-out visitor: it is
        // enabled by default (AllowAnonymousReads = true) and the target policy allows all hosts
        // (AllowedHosts empty), so a cookie-less GET to /ap/v1/proxy/{target} is accepted and relayed.
        var proxyResponse = await _bHttp.GetAsync($"/ap/v1/proxy/{Uri.EscapeDataString(objectIri.Value)}");
        Assert.True(proxyResponse.StatusCode == HttpStatusCode.OK,
            "the proxied re-read of the note on B should be 200, got " + (int)proxyResponse.StatusCode);
        await proxyResponse.Content.ReadAsStringAsync();

        // Now read the note document on B (the object-document endpoint, via ?iri= because the note's IRI
        // is on A's host — foreign to B). The S66 assertion: the server-local iris:sharedCount is still 1
        // (not 0) after the proxied re-store.
        var response = await _bHttp.GetAsync($"/ap/v1/object?iri={Uri.EscapeDataString(objectIri.Value)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"the read of the note on B should be 200, got {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(objectIri.Value, doc.RootElement.GetProperty("id").GetString());
        Assert.True(
            doc.RootElement.TryGetProperty(IrisNamespace + "sharedCount", out var sharedCount),
            "the note document on B should carry the server-local iris:sharedCount extension");
        Assert.Equal(1, sharedCount.GetInt32());
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on B's
    /// outbox. Uses the client pipeline (via a <see cref="CaptureHandler"/>) to produce a correctly
    /// signed request, then replays the signed headers onto a fresh request for delivery to B's
    /// <see cref="TestServer"/>.
    /// </summary>
    private static HttpRequestMessage SignedOutboxRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClientForSigning(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                ActivityJson.ActivityJsonContentType);
            var response = client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
                    {
                        Content = signedContent,
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            response.Dispose();
        }

        var captured = capture.Captured!;
        var content = new StringContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            ActivityJson.ActivityJsonContentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{BHost}{path}")
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

    /// <summary>
    /// Builds an <see cref="IActivityPubClient"/> (signed as <paramref name="handle"/>) routing to the
    /// (deferred) <paramref name="targetServer"/> — the instance's outbound object fetcher.
    /// </summary>
    private static IActivityPubClient BuildClient(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => targetServer().CreateHandler()));
    }

    /// <summary>
    /// Builds a signing <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>) routing
    /// to <paramref name="handler"/> (a <see cref="CaptureHandler"/>) — used to produce a correctly
    /// signed request whose headers are then replayed.
    /// </summary>
    private static IActivityPubClient BuildClientForSigning(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    /// <summary>A mutable holder for a <see cref="TestServer"/> (breaks the fetcher/client ↔ server
    /// circularity in the constructor).</summary>
    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes an actor-document fetch to the correct
    /// instance's <see cref="TestServer"/> based on the actor IRI's host (A's fetcher reaches A and B;
    /// B's fetcher reaches B and A). Each leg is signed with the instance's own key, mirroring the
    /// production server→server signed fetch. This is what lets a host's <c>RemoteInboundKeyResolver</c>
    /// resolve a LOCAL signer's key (the fetcher reaches the host's own TestServer, so no
    /// cross-instance hop is needed) AND a remote signer's key (the fetcher reaches the peer's
    /// TestServer).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey, Iri signingActor)
        {
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
    }

    /// <summary>An <see cref="HttpMessageHandler"/> that captures the request (body + headers) the
    /// signing client produces, returning a stub 200.</summary>
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

    /// <summary>Builds a boost <see cref="Announce"/>: the actor (alice) announces (boosts) the
    /// (remote) object IRI.</summary>
    private static Announce BuildAnnounce(Iri actorIri, Iri objectIri, Iri announceIri) => new()
    {
        Id = announceIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
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
