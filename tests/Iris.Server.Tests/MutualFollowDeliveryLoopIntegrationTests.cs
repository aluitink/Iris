using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.3.1 / 19.3.2 integration test: the two-instance mutual-follow re-delivery loop. When both
/// instances host a local copy of the same actor (alice-a ↔ alice-b) and each follows the other, a
/// <see cref="Create"/> posted on A is federated to B; B's <see cref="Iris.Server.Inbox.CreateActivityHandler"/>
/// (the person branch) records it in B's local alice outbox and — because the mutual follow edge
/// alice-a→alice-b is recorded on B — re-federates the <em>same</em> activity back to A. Without an
/// activity-Id de-duplication guard in the inbox pipeline, each echo is re-fan-out again, producing an
/// <em>unbounded</em> delivery storm (observed live: a single post enqueued 140k+ deliveries to one
/// inbox).
/// </summary>
/// <remarks>
/// The fix is in the <see cref="Iris.Server.Inbox.IInboxProcessor"/>: it stores an inbound activity
/// add-if-absent by IRI (C-07) and, when the activity was already stored (a re-delivery), does NOT
/// re-dispatch it to a handler — so the echo is stored as a no-op and never re-fan-out. This test proves
/// the loop is bounded: after the post settles, the number of deliveries of the single Create to the
/// peer's inbox is a small constant, not unbounded (pre-fix it grows without bound while the workers
/// run).
/// </remarks>
/// <para>
/// Topology: instance A (a.domain.local) and B (b.domain.local) each host a local <c>alice</c>
/// (alice-a, alice-b). The mutual follow edges are recorded on each side (alice-b follows alice-a on A;
/// alice-a follows alice-b on B) so each instance's fan-out targets the peer's local copy of the author.
/// alice-a posts a signed <c>Create</c> to her own outbox (A's outbox-publish path); A federates it to
/// alice-b on B; B records it (where the loop would start); the inbox-Id guard stops the re-delivery.
/// </para>
public sealed class MutualFollowDeliveryLoopIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceAKey;
    private readonly KeyPair _aliceBKey;
    private readonly Iri _aliceAActorIri;
    private readonly Iri _aliceBActorIri;
    private readonly DeliveryCounter _toB = new();
    private readonly DeliveryCounter _toA = new();

    public MutualFollowDeliveryLoopIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceAKey = aSeeded.Key;
        _aliceAActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Alice);
        _aliceBKey = bSeeded.Key;
        _aliceBActorIri = bSeeded.ActorIri;

        // The mutual follow edges, each recorded on the instance that owns the follower set: on A,
        // alice-b (the remote follower) follows alice-a (A's local author); on B, alice-a follows alice-b.
        _aPersistence.Follows.RecordFollowAsync(_aliceBActorIri, _aliceAActorIri).GetAwaiter().GetResult();
        _bPersistence.Follows.RecordFollowAsync(_aliceAActorIri, _aliceBActorIri).GetAwaiter().GetResult();

        // A's delivery transport routes to B (counted by _toB). B's routes to A (counted by _toA). Each
        // instance's fetcher routes by actor-IRI host so it can validate the peer's signature by fetching
        // the peer's actor document (where the public key lives).
        _a = StartServer(
            AHost, Alice, _aPersistence, _aliceAKey, _aliceAActorIri,
            peer: () => _b!, counter: _toB, self: () => _a!);
        _b = StartServer(
            BHost, Alice, _bPersistence, _aliceBKey, _aliceBActorIri,
            peer: () => _a!, counter: _toA, self: () => _b!);
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A post in a mutual-follow network is delivered to the peer a bounded number of times ----

    [Fact]
    public async Task MutualFollow_Post_FederatesToPeer_BoundedNotUnbounded()
    {
        var create = BuildCreate(_aliceAActorIri);

        // alice-a posts the Create to her own outbox (the UI compose / PostNoteAsync path), signed as
        // alice-a. A's outbox-publish handler records it locally and federates it to alice-a's remote
        // followers (alice-b on B) — the single intended delivery.
        using var request = SignedRequest(_aliceAActorIri, _aliceAKey, create, $"/ap/v1/u/{Alice}/outbox");
        using var response = await _aHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Decision 055: A minted the Create's id (and the embedded Note's id); learn the Create's minted
        // id from the 2xx body. Inbound federation keeps the originator's id, so B stores it under this
        // same id.
        var mintedIdNullable = await LearnMintedIdAsync(response);
        Assert.True(mintedIdNullable != null, "A should have returned the minted Create id in the 2xx body.");
        Iri mintedId = mintedIdNullable.Value;

        // A recorded the Create in alice-a's outbox (the local-surfacing half), under the MINTED id.
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceAActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);

        // Wait for the post to reach B (the intended federation hop), then let the (absent) loop run a
        // while so any unbounded re-delivery would accumulate.
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out _),
            timeout: TimeSpan.FromSeconds(30));
        // Let the (absent) mutual-follow echo loop run: poll until the outbound delivery counter is
        // stable for 500ms (a healthy bounded system settles in well under the original fixed 8s),
        // bounded by the original 8s budget so an unbounded re-delivery loop still accumulates before
        // the boundedness assertion below (pre-fix it grew to 140k+).
        await TestFederation.WaitForStableAsync(() => Task.FromResult(_toB.Total),
            settleWindow: TimeSpan.FromMilliseconds(500), timeout: TimeSpan.FromSeconds(8));

        // The Create must have landed on B (the federation worked) ...
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(mintedId, out var storedB),
            "B should have stored the Create federated from A (the post federated to the peer)");
        Assert.IsType<Create>(storedB);

        // ... and B's alice outbox surfaces it exactly once (F-1911-2 outbox dedup).
        var bOutbox = await _bPersistence.Activities.GetOutboxAsync(_aliceBActorIri);
        var bOutboxCount = bOutbox.Count(o => o is IObject { Id: { Length: > 0 } id } && id == mintedId.Value);
        Assert.True(
            bOutboxCount == 1,
            $"B's alice outbox should list the Create exactly once (no echo duplication); got {bOutboxCount}");

        // THE LOOP-SAFETY ASSERTION: the number of deliveries of this single Create to B's alice inbox is
        // bounded. Pre-fix the mutual-follow echo re-fan-outs forever, so this count grows unboundedly
        // while the workers run (observed live: 140k+). With the inbox-Id dedup guard, it is a small
        // constant: A delivers the Create to B once; B's echo back to A (if any) is delivered to A's
        // alice inbox, where the guard dedups it — and A does NOT re-fan-out the echo, so the storm never
        // starts.
        var toB = _toB.Total;
        Assert.True(
            toB <= 4,
            $"the post should reach B's inbox a bounded number of times (got {toB} deliveries); a " +
            "mutual-follow re-delivery loop is unbounded (the 19.3.1/19.3.2 echo defect).");
    }

    // --- A repeated re-delivery of the same Create is not re-fan-out (bounded deliveries) --------

    [Fact]
    public async Task RedeliveredCreate_IsRecordedOnce_NotReFannedOut()
    {
        // This test simulates a re-delivery of a Create INBOUND to B (a federated echo), so the Create
        // carries the originator's id verbatim (inbound federation keeps the originator's id — decision
        // 055). Unlike the outbox-publish path (id-less, server-minted), a directly-delivered inbound
        // Create must carry an id (the inbox handler requires one).
        var create = new Create
        {
            Id = $"https://{AHost}/activities/recreate-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_aliceAActorIri.Value) }],
            Object = [new Note { Id = $"https://{AHost}/objects/recreate-note-{Guid.NewGuid():N}", Content = ["re-delivery post"] }],
        };

        // Re-deliver the same Create to B's alice inbox a second time (simulating a retry / duplicate
        // delivery of an activity already stored on B). B records it as a no-op and does NOT re-fan-out
        // the echo — so the number of deliveries B makes to A for this activity stays bounded (no storm).
        var inbox = InboxOf(_aliceBActorIri);
        await DeliverDirectly(_aliceAActorIri, _aliceAKey, inbox, create, target: () => _b!);
        await DeliverDirectly(_aliceAActorIri, _aliceAKey, inbox, create, target: () => _b!);
        // Let any (absent) re-fan-out echo settle: poll until B's outbound delivery counter is stable
        // for 500ms (a healthy bounded system settles in well under the original fixed 6s), bounded by
        // the original 6s budget so an unbounded re-fan-out loop still accumulates before the assertion.
        await TestFederation.WaitForStableAsync(() => Task.FromResult(_toA.Total),
            settleWindow: TimeSpan.FromMilliseconds(500), timeout: TimeSpan.FromSeconds(6));

        // B stored the Create exactly once.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var storedB),
            "B should have stored the Create");
        Assert.IsType<Create>(storedB);
        var bOutbox = await _bPersistence.Activities.GetOutboxAsync(_aliceBActorIri);
        var bOutboxCount = bOutbox.Count(o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        Assert.True(
            bOutboxCount == 1,
            $"B's alice outbox should list the Create exactly once (a re-delivery is a no-op); got {bOutboxCount}");

        // B's outbound re-fan-out for this activity is bounded (not an unbounded re-delivery loop).
        var toA = _toA.Total;
        Assert.True(
            toA <= 4,
            $"a re-delivered Create must not be re-fan-out unboundedly (got {toA} deliveries to A); the " +
            "inbox-Id dedup guard (19.3.1/19.3.2) must bound the loop.");
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// The inbox IRI for a person actor (the convention <c>{actorIri}/inbox</c>).
    /// </summary>
    private static Iri InboxOf(Iri actorIri) => new($"{actorIri.Value.TrimEnd('/')}/inbox");

    /// <summary>
    /// Starts one instance of the mutual-follow pair: the local <c>alice</c> host whose outbound delivery
    /// worker routes (counted) to the peer's <c>TestServer</c> and signs as the local alice, and whose
    /// fetcher routes by actor-IRI host (self → self, peer → peer) so it can validate the peer's
    /// signature by fetching the peer's actor document.
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair key, Iri actorIri,
        Func<TestServer> peer, DeliveryCounter counter, Func<TestServer> self)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var peerHost = host == AHost ? BHost : AHost;
        var selfHandler = new LazyHandler(() => self().CreateHandler());
        var peerHandler = new CountingHandler(() => peer().CreateHandler(), counter);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => peerHandler,
            Fetcher = new RoutingFetcher(
                host, selfHandler, peerHost, peerHandler, key, actorIri),
        });
    }

    /// <summary>
    /// Delivers <paramref name="activity"/> directly to <paramref name="inbox"/> (signed as
    /// <paramref name="actorIri"/>) through a hosted delivery worker routing to <paramref name="target"/>.
    /// Used to simulate a duplicate delivery of an activity already stored on the target instance.
    /// </summary>
    private static async Task DeliverDirectly(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var loggerFactory = new NullLoggerFactory();

        var queue = new InMemoryDeliveryQueue();
        var service = new Iris.Server.Delivery.DeliveryService(
            queue, loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryService>());
        var worker = new Iris.Server.Delivery.DeliveryWorker(
            queue, factory,
            () => target().CreateHandler(),
            Microsoft.Extensions.Options.Options.Create(
                new ActivityPubServerOptions { InstanceActorId = actorIri }),
            loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryWorker>());

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddHostedService(_ => worker))
            .Build();

        await host.StartAsync(CancellationToken.None);
        try
        {
            await service.DeliverAsync(inbox, activity);
            // Let the (single) delivery settle before returning.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

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

    private static Create BuildCreate(Iri actorIri) => new()
    {
        // Decision 055: the client sends the Create's shape (no id on the activity or the embedded Note);
        // the server mints both and returns the created object in the 2xx body.
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Content = ["loop-safety post"] },
        ],
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

    // --- Private test doubles --------------------------------------------------------------

    /// <summary>
    /// Counts outbound deliveries (total) so a test can assert how many times a single activity is
    /// delivered — bounded = no re-delivery loop; unbounded = the 19.3.1/19.3.2 echo defect — then
    /// forwards to a (deferred) inner handler.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
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
    /// on the actor IRI's host (each instance's fetcher needs to reach both itself and the peer to
    /// validate signatures and resolve inboxes).
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

    /// <summary>
    /// Counts outbound deliveries (a simple total), so a test can assert the number of deliveries of a
    /// single activity is bounded (no re-delivery loop).
    /// </summary>
    private sealed class DeliveryCounter
    {
        private int _count;

        public void Record() => System.Threading.Interlocked.Increment(ref _count);

        public int Total => System.Threading.Volatile.Read(ref _count);
    }
}
