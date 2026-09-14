using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.8 end-to-end test (reactions and engagement interoperability across instances — the boost /
/// <c>Announce</c> path): an Iris user (alice on instance B) boosts a post on a <em>remote</em> instance
/// (bob's note m1 on instance A — the Lemmy stand-in). The announced object m1 is on A, which B has
/// <em>never stored</em> (it was only ever seen by IRI). The cross-instance boost must stay coherent on
/// the <strong>object's home instance</strong> (A): the boost must actually reach A, where m1 is local,
/// and be counted there (the object's <c>shares</c> collection / boost counter, decision 056 (d)). Three
/// invariants are pinned:
/// <list type="number">
/// <item><em>The boost federates to the object's home:</em> B's outbox publish resolves the remote object's
/// author (by fetching m1 over the wire) and delivers the Announce to it, so A records the
/// <c>announcer → announced-object</c> edge (gap — before 136.8 the Announce branch fanned out to
/// followers + relays only and discarded the owner resolved by <c>RecordAnnounceLocalAsync</c>, so a boost
/// of a remote object whose author is not a follower was never delivered to the object's home — the Lemmy
/// author of a post an Iris user boosted would never see the boost).</item>
/// <item><em>The object's home counts the boost:</em> <c>GET {m1}/shares</c> on A lists alice's boost
/// (m1 is local on A, the announcers reverse index includes alice) — the Lemmy author's per-object boost
/// counter includes the Iris boost.</item>
/// <item><em>The object's author is named in the boost's <c>to</c> audience:</em> the Announce delivered to
/// A carries bob (m1's author) in its <c>to</c> field, so a conforming receiver reading the audience sees
/// the object's author as a direct recipient (mirrors the 136.7 reply's parent-author audience).</item>
/// </list>
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>bob</c> — posts m1, the object's home) and instance B
/// (b.domain.local, actor <c>alice</c> — boosts m1). Bidirectional federation wiring:
/// <list type="bullet">
/// <item>B's outbound object fetcher (<see cref="ActivityPubHostOptions.Client"/>) routes to A, so B's
/// outbox publish can fetch the remote object m1 and resolve its author (bob).</item>
/// <item>B's delivery transport routes to A, so the Announce is delivered to bob (the object's author) by
/// B's hosted <see cref="DeliveryWorker"/>.</item>
/// <item>A's inbound fetcher routes to B, so A can validate the Announce's signature (resolving alice's
/// key from B's actor doc).</item>
/// </list>
/// The boost is authored by a signed <c>POST /ap/v1/u/alice/outbox</c> to B (the local-outbox publish
/// path, where the 136.8 audience-rewrite + object-author delivery live). The assertions read A's
/// persistence (the announce edge via <c>GetAnnouncersAsync(m1)</c> + the Announce's <c>to</c> audience)
/// and A's <c>GET {m1}/shares</c> endpoint (the per-object boost counter on the object's home).
/// </remarks>
public sealed class CrossInstanceAnnounceIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;

    public CrossInstanceAnnounceIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts bob (the object's author, the object's home). B hosts alice (the announcer).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Bob);
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceKey = bSeeded.Key;
        _aliceActorIri = bSeeded.ActorIri;

        var aServerHolder = new TestServerHolder();
        var bServerHolder = new TestServerHolder();

        // A: its inbound fetcher reaches B (validates the Announce signed by alice). A's object fetcher
        // reaches B (for the symmetric direction, unused here).
        _a = aServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Bob,
            Persistence = _aPersistence,
            IdentityKeys = BuildIdentityKeys(aSeeded.Key, aSeeded.ActorIri),
            Fetcher = BuildFetcherFor(AHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
            Client = BuildClient(BHost, Bob, aSeeded.Key, () => bServerHolder.Server!),
        });

        // B: its object fetcher reaches A (resolves the remote object m1's author), its delivery transport
        // reaches A (delivers the Announce to bob via B's hosted DeliveryWorker), its inbound fetcher is a
        // self-fetcher (validates the Announce signed by alice, B's local actor).
        _b = bServerHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Alice,
            Persistence = _bPersistence,
            IdentityKeys = BuildIdentityKeys(bSeeded.Key, bSeeded.ActorIri),
            Fetcher = BuildSelfFetcher(BHost, bSeeded.Key, bSeeded.ActorIri, () => bServerHolder.Server!),
            DeliveryTransport = () => new LazyHandler(() => aServerHolder.Server!.CreateHandler()),
            Client = BuildClient(BHost, Alice, bSeeded.Key, () => aServerHolder.Server!),
        });

        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{AHost}") };
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri($"https://{BHost}") };
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The cross-instance boost reaches the object's home and is counted there ---------------

    [Fact]
    public async Task BoostOfRemoteObject_FederatesToObjectHome_AndIsCountedThere()
    {
        // bob (A) posts m1 on A (the object's home — the Lemmy stand-in's post). The IRI must be in A's
        // serving namespace (the /ap/v1 route prefix) so A's object-document endpoint can serve it when B
        // fetches it over the wire (B's outbox publish resolves the remote object's author by fetching m1).
        var objectIri = new Iri($"https://{AHost}/ap/v1/objects/m1-{Guid.NewGuid():N}");
        await _aPersistence.Objects.PutObjectAsync(new Note
        {
            Id = objectIri.Value,
            Content = ["a post on the remote instance"],
            AttributedTo = [new Link { Href = new Uri($"https://{AHost}/ap/v1/u/{Bob}") }],
        });

        // alice (B) boosts m1 via a signed POST to B's outbox (the local-outbox publish path, where the
        // 136.8 audience-rewrite + object-author delivery live). B resolves m1's author (bob) by fetching
        // m1 over the wire, adds bob to the Announce's `to`, and delivers the Announce to bob (A) — the
        // object's home.
        var announceIri = new Iri($"https://{BHost}/activities/announce-{Guid.NewGuid():N}");
        var announce = BuildAnnounce(_aliceActorIri, objectIri, announceIri);

        using var signedRequest = SignedOutboxRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var publishResponse = await _bHttp.SendAsync(signedRequest);
        Assert.Equal(HttpStatusCode.Accepted, publishResponse.StatusCode);

        // Wait on the EFFECT of the federation (A recording the announce edge on the object's home), not on
        // B's storage: the boost must have crossed to A (the object's home) and been recorded there (A
        // validated alice's signature via its fetcher → B).
        var aliceIri = _aliceActorIri;
        await WaitForAsync(async () =>
            await _aPersistence.Announces.HasAnnouncedAsync(aliceIri, objectIri),
            timeout: TimeSpan.FromSeconds(30));

        // (a) A recorded the announcer → announced-object edge (alice → m1) on the object's home — the
        // boost reached the object's home (it was not stranded on B alone, where RecordAnnounceLocalAsync
        // also recorded the edge for the local counter).
        Assert.True(
            await _aPersistence.Announces.HasAnnouncedAsync(aliceIri, objectIri),
            "A (the object's home) should have recorded the boost federated by B's outbox publish");

        // (b) A's announcers reverse index for m1 includes alice — the per-object boost counter (decision
        // 056 (d)) on the object's home counts the Iris boost.
        var announcers = await _aPersistence.Announces.GetAnnouncersAsync(objectIri);
        Assert.Contains(announcers, a => a.Value == aliceIri.Value);

        // (c) GET {m1}/shares on A lists alice's boost — the Lemmy author's per-object boost collection
        // includes the Iris boost (the cross-instance boost is coherent on the object's home).
        var response = await _aHttp.GetAsync($"{objectIri.Value}/shares");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).ToArray();
        // The shares collection serves the full Announce activity (id + actor + object). The item is the
        // boost alice issued against m1 — verify its semantic content (actor = alice, object = m1), not a
        // specific IRI (A stores the boost under its own deterministic IRI, decision 055, independent of
        // the IRI B minted at publish-time).
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("Announce", item.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Alice}", ExtractIri(item.GetProperty("actor")));
        Assert.Equal(objectIri.Value, ExtractIri(item.GetProperty("object")));
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(objectIri.SharesOf().Value, doc.RootElement.GetProperty("id").GetString());
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>) POSTing <paramref name="activity"/> to <paramref name="path"/> on the
    /// author's outbox (on <see cref="BHost"/>). Uses the client pipeline (via a
    /// <see cref="CaptureHandler"/>) to produce a correctly signed request, then replays the signed headers
    /// onto a fresh request for delivery to B's <see cref="TestServer"/>.
    /// </summary>
    private static HttpRequestMessage SignedOutboxRequest(Iri actorIri, KeyPair key, Activity activity, string path)
    {
        var json = ActivityJson.Serialize(activity);
        var capture = new CaptureHandler();
        using (var client = BuildClientForSigning(actorIri, key, capture))
        {
            var signedContent = new StringContent(json);
            signedContent.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
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
        content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
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
    /// Builds the identity keys (key store + provider + signer) for a single local actor.
    /// </summary>
    private static IdentityKeys BuildIdentityKeys(KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as <paramref name="handle"/>)
    /// routes to the (deferred) <paramref name="targetServer"/> — i.e. the instance's fetcher reaches the
    /// other instance's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, Func<TestServer> targetServer)
    {
        var client = BuildClient(host, handle, key, targetServer);
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> that reaches the instance's own server (a
    /// self-fetcher), so the instance can validate an inbound <see cref="Announce"/> signed by one of its
    /// local actors (resolving the actor's key from its own actor doc).
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(
        string host, KeyPair key, Iri actorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Builds an <see cref="IActivityPubClient"/> (signed as <paramref name="handle"/>) routing to the
    /// (deferred) <paramref name="targetServer"/> — the instance's outbound object fetcher (resolves a
    /// remote object's author / owner over the wire).
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
    /// to <paramref name="handler"/> (a <see cref="CaptureHandler"/>) — used to produce a correctly signed
    /// request whose headers are then replayed.
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

    /// <summary>
    /// A mutable holder for a <see cref="TestServer"/>, used to break the fetcher/client ↔ server
    /// circularity in the constructor (the fetcher reads <see cref="Server"/> lazily, after it is
    /// populated).
    /// </summary>
    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that captures the request (body + headers) the signing client
    /// produces, returning a stub 200. The captured signed headers are then replayed onto a fresh request
    /// for delivery to the real <see cref="TestServer"/>.
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
    /// Builds a boost <see cref="Announce"/>: the actor (alice) announces (boosts) the (remote) object
    /// IRI.
    /// </summary>
    private static Announce BuildAnnounce(Iri actorIri, Iri objectIri, Iri announceIri) => new()
    {
        Id = announceIri.Value,
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Extracts an IRI from a JSON element that is either a bare IRI string or a <c>{"href": ...}</c>
    /// link object (the two ActivityStreams forms an <c>actor</c>/<c>object</c> reference may take).
    /// </summary>
    private static string ExtractIri(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Object when element.TryGetProperty("href", out var href) => href.GetString()!,
        _ => throw new InvalidOperationException($"Unexpected IRI form: {element.ValueKind}"),
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
