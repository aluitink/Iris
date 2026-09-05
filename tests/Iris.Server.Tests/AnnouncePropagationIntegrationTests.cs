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
/// Phase 19.3.3 integration test: <strong>Announce (boost) propagation</strong> across a two-instance
/// network. Two concerns are verified:
/// </summary>
/// <list type="number">
/// <item>
/// <strong>A boost of a local note reaches the peer's local follower exactly once.</strong> Instance A
/// (a.domain.local) hosts <c>alice</c>; instance B (b.domain.local) hosts <c>bob</c> and <c>carol</c>,
/// and bob follows alice. alice boosts her own local note (a signed <see cref="Announce"/> to her
/// outbox); A federates it to bob (the remote follower). B's <see cref="AnnounceActivityHandler"/>
/// records it and propagates it to bob's local follower (carol). The boost must reach carol's inbox
/// <em>once</em> — a bounded, single propagation, not an amplification.
/// </item>
/// <item>
/// <strong>A boost of a note *from the peer* (remote content) carries the correct <c>object</c> link
/// and produces no infinite announce chain.</strong> alice boosts <em>bob's</em> note (an object IRI on
/// B). The propagated <c>Announce</c> must reference the remote object by <em>link</em> (not an embedded
/// copy that could double-attribute), and the peer must not re-announce the boost (which would chain
/// forever — the classic boost loop).
/// </item>
/// </list>
/// <remarks>
/// Decision 055: the boost's id is <em>server-minted</em> (an unguessable ULID under
/// <c>{announcer}/announces/{ulid}</c>) — the local instance is the sole authority for the id of the
/// activity it authors, and it is minted once at record-time and reused for every propagated copy, so
/// A's and B's copies of the same boost share the one id (a follower that stores by IRI dedupes the
/// boost rather than seeing one copy per delivery). The 19.3.1/19.3.2 inbox-Id dedup guard (an activity
/// is interpreted only on first delivery) is what bounds the propagation: the peer's handling of the
/// boost records it once and does not re-fan-out the same activity.
/// </remarks>
[Collection("AnnouncePropagation")]
public sealed class AnnouncePropagationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    private readonly AnnouncePropagationSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _aHttp;
    private readonly DeliveryCounter _toB;
    private readonly DeliveryCounter _toA;
    private KeyPair _aliceKey;
    private KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;

    public AnnouncePropagationIntegrationTests(AnnouncePropagationSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aHttp = new HttpClient(_fixture.ServerA.CreateHandler(), disposeHandler: false);
        _toB = fixture.ToB;
        _toA = fixture.ToA;
        _aliceKey = null!;
        _bobKey = null!;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _carolActorIri = new Iri($"https://{BHost}/ap/v1/u/{Carol}");
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        _toB.Reset();
        _toA.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        _bPersistence.Keys.TryGetKey(new Iri($"{_bobActorIri.Value}#key-1"), out var bobKey);
        _bobKey = (KeyPair)bobKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores alice (on A) + bob + carol (on B) with their existing keys and the bob→alice (on A)
    /// and carol→bob (on B) follow edges.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"https://{AHost}/ap/v1/u/{Alice}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"https://{BHost}/ap/v1/u/{Bob}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Carol, new Iri($"https://{BHost}/ap/v1/u/{Carol}#key-1"));

        aPersistence.Follows.RecordFollowAsync(
            new Iri($"https://{BHost}/ap/v1/u/{Bob}"),
            new Iri($"https://{AHost}/ap/v1/u/{Alice}")).GetAwaiter().GetResult();
        bPersistence.Follows.RecordFollowAsync(
            new Iri($"https://{BHost}/ap/v1/u/{Carol}"),
            new Iri($"https://{BHost}/ap/v1/u/{Bob}")).GetAwaiter().GetResult();
    }

    // --- A boost of a local note reaches the peer's local follower exactly once ----------------

    [Fact]
    public async Task Boost_LocalNote_ReachesPeerLocalFollower_Once()
    {
        var objectIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        var announce = BuildLocalAnnounce(_aliceActorIri, objectIri);

        // alice boosts her own local note (a signed id-less Announce to her outbox). A mints the id,
        // records it, and federates it to bob (alice's remote follower on B).
        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // A recorded the boost in alice's outbox under its server-minted id (local surfacing).
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // B validated the federated boost and its AnnounceActivityHandler propagated it to carol (bob's
        // local follower) — reusing the same minted id for the propagated copy. Wait for carol's outbox
        // to surface the boost.
        await TestFederation.WaitForAsync(async () =>
            (await _bPersistence.Activities.GetOutboxAsync(_carolActorIri))
                .Any(o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value),
            timeout: TimeSpan.FromSeconds(30));
        // Let any (absent) amplification settle: poll until the outbound delivery counter is stable for
        // 500ms (a healthy bounded system settles in well under the original fixed 3s), bounded by the
        // original 3s budget so an unbounded re-announce still accumulates before the assertion below.
        await TestFederation.WaitForStableAsync(() => Task.FromResult(_toA.Total),
            settleWindow: TimeSpan.FromMilliseconds(500), timeout: TimeSpan.FromSeconds(3));

        // The boost reached carol (bob's local follower) — the propagation happened.
        var carolOutbox = await _bPersistence.Activities.GetOutboxAsync(_carolActorIri);
        var carolCount = carolOutbox.Count(o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);
        Assert.True(
            carolCount == 1,
            $"carol (bob's local follower) should see the boost exactly once (no amplification); got {carolCount}");

        // THE LOOP-SAFETY ASSERTION: the total number of outbound deliveries B made (to A) for this boost
        // is bounded. If the peer re-announced the boost (re-fan-out), A would re-deliver it back to B and
        // the storm would grow unboundedly (the classic boost loop). The 19.3.1/19.3.2 inbox-Id dedup
        // guard keeps it at a small constant.
        Assert.True(
            _toA.Total <= 4,
            $"the boost must not be re-announced in a loop (got {_toA.Total} outbound deliveries from B); " +
            "an unbounded announce chain is the 19.3.3 boost-loop failure.");

        // The stored boost on B references the object by link (correct object link, not an embedded copy).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B should have stored the federated boost");
        var storedAnnounce = Assert.IsType<Announce>(storedB);
        SingleObjectLinkTo(storedAnnounce, objectIri);
    }

    // --- A boost of remote (peer) content carries the correct object link and no infinite chain -

    [Fact]
    public async Task Boost_RemotePeerNote_CarriesObjectLink_NoInfiniteChain()
    {
        // bob's note lives on B (remote from A's perspective). alice boosts it (boosting remote content).
        var remoteNoteIri = new Iri($"https://{BHost}/objects/bob-note-{Guid.NewGuid():N}");
        var announce = BuildLocalAnnounce(_aliceActorIri, remoteNoteIri);

        using var request = SignedRequest(_aliceActorIri, _aliceKey, announce, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mintedId = await LearnMintedIdAsync(response);

        // The boost federated to B (bob's instance) and was propagated to carol (bob's local follower),
        // reusing the same minted id.
        await TestFederation.WaitForAsync(async () =>
            (await _bPersistence.Activities.GetOutboxAsync(_carolActorIri))
                .Any(o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value),
            timeout: TimeSpan.FromSeconds(30));
        // Let any (absent) re-announce settle: poll until the outbound delivery counter is stable for
        // 500ms (a healthy bounded system settles in well under the original fixed 3s), bounded by the
        // original 3s budget so an unbounded re-announce chain still accumulates before the assertion below.
        await TestFederation.WaitForStableAsync(() => Task.FromResult(_toA.Total),
            settleWindow: TimeSpan.FromMilliseconds(500), timeout: TimeSpan.FromSeconds(3));

        // The stored boost references the REMOTE object (bob's note) by link — the correct object link,
        // not an embedded copy (which would double-attribute the boost to the wrong author).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B should have stored the boost of the remote note");
        var storedAnnounce = Assert.IsType<Announce>(storedB);
        SingleObjectLinkTo(storedAnnounce, remoteNoteIri);

        // The boost is attributed to alice (the announcer), not bob (the remote object's author) —
        // boosting remote content must not re-attribute the content.
        var actorLink = storedAnnounce.Actor?.FirstOrDefault() as ILink;
        Assert.NotNull(actorLink);
        Assert.Equal(new Uri(_aliceActorIri.Value), actorLink!.Href);

        // No infinite announce chain: B's outbound re-fan-out of the boost is bounded (the peer does not
        // re-announce the boost back to A, which would chain forever).
        Assert.True(
            _toA.Total <= 4,
            $"boosting remote content must not start an infinite announce chain (got {_toA.Total} outbound " +
            "deliveries from B); the 19.3.3 boost-loop failure.");
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an id-less <see cref="Announce"/> from <paramref name="actorIri"/> that re-shares
    /// <paramref name="objectIri"/> (a <c>Link</c>, never an embedded object). Decision 055: the client
    /// sends the activity shape WITHOUT an id; the server mints the id (minted once at record-time and
    /// reused for every propagated copy, so a follower that stores by IRI dedupes the boost) and returns
    /// the created activity in the 202 body. The test learns the id via <see cref="LearnMintedIdAsync"/>.
    /// </summary>
    private static Announce BuildLocalAnnounce(Iri actorIri, Iri objectIri) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Learns the server-minted id of an activity from the 202 outbox-publish response body (decision
    /// 055): the server is the sole id authority and returns the created activity (with its minted id) in
    /// the 202 body. This mirrors the real client's <c>DeliveryResult.MintedId</c> flow. The boost's id
    /// is minted once and reused for every propagated copy, so the same id is what A's outbox, B's
    /// activity store, and carol's outbox all carry.
    /// </summary>
    private static async Task<Iri> LearnMintedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var created = ActivityJson.Deserialize<Activity>(body);
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        return new Iri(created.Id);
    }

    /// <summary>
    /// Asserts the activity's <c>object</c> is a single <see cref="Link"/> to <paramref name="objectIri"/>
    /// (the correct object reference) — not an embedded object copy.
    /// </summary>
    private static void SingleObjectLinkTo(Announce activity, Iri objectIri)
    {
        var objects = activity.Object?.ToList();
        Assert.NotNull(objects);
        Assert.Single(objects!);
        var first = objects![0];
        // The object must be a Link (a reference), not an embedded object copy (which would
        // double-attribute the boost).
        Assert.True(first is ILink, $"the boost's object should be a Link to {objectIri}, not an embedded copy");
        var link = (ILink)first;
        Assert.Equal(objectIri.Value, link.Href!.AbsoluteUri);
    }

    /// <summary>
    /// Builds a signed <c>POST {actorIri}/outbox</c> request for <paramref name="activity"/>, signed as
    /// <paramref name="actorIri"/> (the outbox-publish write surface requires a valid signature from the
    /// acting actor). Uses the client pipeline (via a capture handler) to produce correctly-signed headers,
    /// then replays them onto a fresh request.
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

    // --- Private test doubles --------------------------------------------------------------

    /// <summary>
    /// Counts outbound deliveries (total) so a test can assert the number of deliveries of a single
    /// activity is bounded (no re-announce loop), then forwards to a (deferred) inner handler.
    /// </summary>
    internal sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory;
        private readonly DeliveryCounter _counter;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        public CountingHandler(Func<HttpMessageHandler> innerFactory, DeliveryCounter counter)
        {
            _innerFactory = innerFactory;
            _counter = counter;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            _counter.Record();

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

            return _client.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (each instance's fetcher reaches itself and the peer to validate
    /// signatures and resolve inboxes).
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
/// Counts outbound deliveries (a simple total), so a test can assert the number of deliveries of a
/// single activity is bounded (no re-announce loop).
/// </summary>
public sealed class DeliveryCounter
{
    private int _count;

    /// <summary>Records one outbound delivery.</summary>
    public void Record() => System.Threading.Interlocked.Increment(ref _count);

    /// <summary>The total number of outbound deliveries recorded so far.</summary>
    public int Total => System.Threading.Volatile.Read(ref _count);

    /// <summary>Resets the counter to zero (for per-method isolation in a shared fixture).</summary>
    public void Reset() => System.Threading.Interlocked.Exchange(ref _count, 0);
}

/// <summary>
/// Shared two-host fixture for <see cref="AnnouncePropagationIntegrationTests"/> (A: a.domain.local
/// alice, B: b.domain.local bob + carol). Seeds alice + bob + carol with keys ONCE; A's identity +
/// RoutingFetcher (A doc from A, B doc from B) + counting delivery to B; B's identity + fetcher
/// reaches A (validates the federated Announce) + counting delivery to A. The bob→alice follow edge
/// is on A; the carol→bob follow edge is on B.
/// </summary>
public sealed class AnnouncePropagationSharedHost : SharedTwoHostFixture
{
    private static readonly Lazy<DeliveryCounter> ToBRef = new(() => new DeliveryCounter());
    private static readonly Lazy<DeliveryCounter> ToARef = new(() => new DeliveryCounter());

    /// <summary>Counts deliveries from A to B (exposed so the test can assert boundedness).</summary>
    public DeliveryCounter ToB => ToBRef.Value;

    /// <summary>Counts deliveries from B to A (exposed so the test can assert boundedness).</summary>
    public DeliveryCounter ToA => ToARef.Value;

    public AnnouncePropagationSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AnnouncePropagationIntegrationTests.AHost, AnnouncePropagationIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, AnnouncePropagationIntegrationTests.BHost, AnnouncePropagationIntegrationTests.Bob);
        var cSeeded = TestSeeder.SeedPersonWithKey(bPersistence, AnnouncePropagationIntegrationTests.BHost, AnnouncePropagationIntegrationTests.Carol);

        // The bob→alice follow edge is on A (A is alice's home; it owns her follower set).
        // The carol→bob follow edge is on B (B is bob's home; it owns bob's follower set).
        aPersistence.Follows.RecordFollowAsync(bSeeded.ActorIri, aSeeded.ActorIri).GetAwaiter().GetResult();
        bPersistence.Follows.RecordFollowAsync(cSeeded.ActorIri, bSeeded.ActorIri).GetAwaiter().GetResult();

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

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

        var optionsA = new ActivityPubHostOptions
        {
            Host = AnnouncePropagationIntegrationTests.AHost,
            Handle = AnnouncePropagationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            DeliveryTransport = () => new AnnouncePropagationIntegrationTests.CountingHandler(
                () => serverBRef().CreateHandler(), ToBRef.Value),
            Fetcher = new AnnouncePropagationIntegrationTests.RoutingFetcher(
                AnnouncePropagationIntegrationTests.AHost, new LazyHandler(() => serverARef().CreateHandler()),
                AnnouncePropagationIntegrationTests.BHost, new LazyHandler(() => serverBRef().CreateHandler()),
                aSeeded.Key, aSeeded.ActorIri),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = AnnouncePropagationIntegrationTests.BHost,
            Handle = AnnouncePropagationIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = new IdentityKeys(bKeyStore, bKeyProvider, bSigner),
            DeliveryTransport = () => new AnnouncePropagationIntegrationTests.CountingHandler(
                () => serverARef().CreateHandler(), ToARef.Value),
            Fetcher = BuildFetcherForLazy(bSeeded.Key, bSeeded.ActorIri, serverARef),
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// Builds a fetcher whose client (signed as the actor) routes to the (deferred) target's
    /// <c>TestServer</c> — i.e. B's fetcher reaches A's actor documents (lazily).
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
/// xunit collection definition for the announce propagation shared two-host fixture.
/// </summary>
[CollectionDefinition("AnnouncePropagation")]
public sealed class AnnouncePropagationCollection : ICollectionFixture<AnnouncePropagationSharedHost>
{
}
