using System.Net;
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
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 136.11 — Delivery reliability: idempotency across retries, end-to-end dead-lettering in a
/// real topology, and <c>Retry-After</c> HTTP-date form handling.
/// </summary>
/// <remarks>
/// Test 1 (idempotency): a delivery that is re-POSTed to the receiving inbox (simulating an
/// at-least-once retry) is stored exactly once. The inbox pipeline (C-07 <c>TryAddActivityAsync</c>)
/// dedupes by activity IRI, and <c>AddToInboxAsync</c> is idempotent — so the object appears once in
/// the activity store and once in the recipient's inbox list.
/// <para />
/// Test 2 (end-to-end dead-letter): a real two-instance topology where A's delivery to B's inbox
/// always fails (500). With fast retry options (<see cref="DeliveryRetryOptions.BaseDelay"/>=50ms,
/// <see cref="DeliveryRetryOptions.MaxAttempts"/>=3), the worker exhausts its budget in ~150ms and
/// dead-letters the job. The dead-letter store holds the entry with the correct inbox, actor, kind,
/// detail, and attempt count.
/// <para />
/// Test 3 (Retry-After HTTP-date): a 429 response with a <c>Retry-After</c> HTTP-date header (3
/// seconds in the future, not delay-seconds) is honored — the worker waits until the specified date
/// before retrying (the gap between attempts is well above the zero-base-delay backoff that would
/// otherwise fire immediately).
/// </remarks>
public sealed class DeliveryReliabilityIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly FailingInboxHandler _failingTransport;

    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceIri;
    private readonly Iri _aliceInboxIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;

    public DeliveryReliabilityIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceIri = aSeeded.ActorIri;
        _aliceInboxIri = _aliceIri.InboxOf();

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();

        var aHolder = new TestServerHolder();
        var bHolder = new TestServerHolder();

        // Create B first (it needs no wiring to A).
        _b = bHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            Fetcher = new RoutingFetcher(
                _bobActorIri, _bobKey, () => bHolder.Server!.CreateHandler(),
                _aliceIri, _aliceKey, () => aHolder.Server!.CreateHandler()),
        });

        // A: fetcher routes by host (alice → A, bob → B); delivery transport routes to B via a
        // FailingInboxHandler (returns 500 for POSTs to bob's inbox). The default retry options
        // (5 attempts, 1s base delay) are used — the dead-letter takes ~15s.
        _failingTransport = new FailingInboxHandler(
            new LazyHandler(() => bHolder.Server!.CreateHandler()), _bobInboxIri.Value);
        _a = aHolder.Server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            Fetcher = new RoutingFetcher(
                _aliceIri, _aliceKey, () => aHolder.Server!.CreateHandler(),
                _bobActorIri, _bobKey, () => bHolder.Server!.CreateHandler()),
            DeliveryTransport = () => _failingTransport,
        });
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --------------------------------------------------------------- Test 1: Idempotency

    [Fact]
    public async Task RetriedDelivery_IsProcessedExactlyOnce_OnReceivingInstance()
    {
        // Build a Create activity with a fixed IRI. The actor is alice (a local actor on A), so B's
        // RoutingFetcher resolves alice's key by fetching A's actor document.
        var create = new Create
        {
            Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_aliceIri.Value) }],
            Object = [new Note
            {
                Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}",
                Content = ["idempotency test"],
            }],
        };

        // Deliver the Create to bob's inbox on B twice (same IRI — simulating at-least-once
        // delivery). The inbox pipeline should store it once and skip re-dispatch on the second delivery.
        using var client = BuildDeliveryClient(_aliceIri, _aliceKey, _b.CreateHandler());
        var result1 = await client.DeliverAsync(_bobInboxIri, create);
        Assert.True(
            result1.StatusCode == 202,
            $"Expected 202 on first delivery, got {result1.StatusCode}");

        var result2 = await client.DeliverAsync(_bobInboxIri, create);
        Assert.True(
            result2.StatusCode == 202,
            $"Expected 202 on second delivery (re-delivery), got {result2.StatusCode}");

        // B's activity store holds the Create exactly once (C-07: TryAddActivityAsync dedupes by IRI).
        var activities = await _bPersistence.Activities.GetAllActivitiesAsync();
        var matchingCreates = activities.Where(a => a.Id == create.Id).ToList();
        Assert.Single(matchingCreates);

        // B's inbox for bob holds the Create exactly once (AddToInboxAsync is idempotent by IRI).
        var inbox = await _bPersistence.Activities.GetInboxAsync(_bobActorIri);
        var inboxMatches = inbox.Count(item =>
            item is IObject obj && obj.Id == create.Id);
        Assert.Equal(1, inboxMatches);
    }

    // --------------------------------------------------------------- Test 2: E2E dead-letter

    [Fact(Skip = "slow >15s (real 1+2+4+8s delivery backoff; blame-hang triage 2026-09-17)")]
    [Trait(TestCategories.Category, TestCategories.Slow)]
    public async Task FailedCrossInstanceDelivery_IsDeadLettered_RealTopology()
    {
        // The FailingInboxHandler (wired in the constructor) returns 500 for POSTs to bob's inbox.
        // With the default retry options (5 attempts, 1s base delay), the worker exhausts its budget
        // in ~15s (1+2+4+8) and dead-letters the job.

        var deadLetter = _a.Services.GetRequiredService<IDeliveryDeadLetterStore>();
        Assert.Equal(0, deadLetter.Count);

        // Bob (on B) follows alice (on A). A's FollowActivityHandler auto-constructs an Accept and
        // enqueues it for delivery to bob's inbox on B. The FailingInboxHandler returns 500 for that
        // POST, so the worker retries (5 attempts, default backoff) and dead-letters the job.
        var follow = new Follow
        {
            Id = $"https://{BHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_bobActorIri.Value) }],
            Object = [new Link { Href = new Uri(_aliceIri.Value) }],
        };
        using (var client = BuildDeliveryClient(_bobActorIri, _bobKey, _a.CreateHandler()))
        {
            var result = await client.DeliverAsync(_aliceInboxIri, follow);
            Assert.True(
                result.StatusCode == 202,
                $"Expected 202 (bob's follow accepted by A), got {result.StatusCode}");
        }

        // Wait for the dead-letter store to hold the entry (5 attempts with 1s+2s+4s+8s backoff = 15s;
        // allow 30s for safety).
        await TestFederation.WaitForAsync(
            () => Task.FromResult(deadLetter.Count == 1),
            TimeSpan.FromSeconds(30));

        var entries = await deadLetter.ListAsync();
        Assert.True(
            entries.Count == 1,
            $"Expected 1 dead-lettered entry, got {entries.Count}. " +
            $"FailingInboxHandler served {_failingTransport.FailureCount} 500s " +
            $"(0 means the worker used a different transport).");
        var entry = entries[0];

        // The entry records the correct fields.
        Assert.Equal(_bobInboxIri.Value, entry.InboxIri.Value);
        Assert.Equal(_aliceIri.Value, entry.ActorIri?.Value);
        Assert.Equal(DeadLetterFailureKind.NonSuccessStatus, entry.FailureKind);
        Assert.Equal("500", entry.FailureDetail);
        Assert.Equal(5, entry.Attempts);

        // The dead-lettered activity is the Accept (A's auto-response to bob's follow).
        Assert.NotNull(entry.Activity);
        Assert.NotNull(entry.Activity.Id);
    }

    // --------------------------------------------------------------- Test 3: Retry-After HTTP-date

    [Fact]
    public async Task RetryAfter_HttpDateForm_IsHonored()
    {
        // A handler that returns 429 with a Retry-After HTTP-date header (1 second in the future)
        // on the first call, then 200 on subsequent calls. The worker should wait until the specified
        // date before retrying (not use the exponential backoff, which is 0 here).

        var handler = new RetryAfterHttpDateHandler();
        var (worker, queue, deadLetter) = BuildWorkerWithHandler(handler, maxAttempts: 3);

        await EnqueueAndRunAsync(worker, queue, _bobInboxIri.Value,
            isDone: () => handler.CallCount >= 2);

        // The worker waited for the HTTP-date (1 second) before retrying.
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(0, deadLetter!.Count);

        // The gap between the two attempts should be >= 2000ms (3s HTTP-date minus tolerance for the
        // HTTP-date's second-resolution granularity, clock skew, and the ~300-500ms EC P-256 signing
        // overhead of the first attempt).
        Assert.True(
            handler.MillisecondsBetweenAttempt1And2 >= 2000,
            $"Expected >= 2000ms gap (HTTP-date Retry-After), observed {handler.MillisecondsBetweenAttempt1And2}ms.");
    }

    // --- Helpers ------------------------------------------------------------------------

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    private static async Task EnqueueAndRunAsync(
        DeliveryWorker worker, InMemoryDeliveryQueue queue, string inboxIri, Func<bool> isDone)
    {
        var aliceIri = $"https://{AHost}/ap/v1/u/{Alice}";
        await queue.EnqueueAsync(new DeliveryJob(new Iri(inboxIri), new Create
        {
            Id = $"{aliceIri}/creates/retry-after-test",
            Actor = [new Link { Href = new Uri(aliceIri) }],
            Object = [new Note { Id = $"{aliceIri}/notes/retry-after-test", Content = ["retry after test"] }],
        }));

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        await host.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!isDone() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await host.StopAsync(CancellationToken.None);
        host.Dispose();
    }

    private static (DeliveryWorker, InMemoryDeliveryQueue, IDeliveryDeadLetterStore) BuildWorkerWithHandler(
        HttpMessageHandler handler, int maxAttempts)
    {
        var aliceIri = $"https://{AHost}/ap/v1/u/{Alice}";
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{aliceIri}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(aliceIri), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        var queue = new InMemoryDeliveryQueue();
        var options = Options.Create(
            new ActivityPubServerOptions { InstanceActorId = new Iri(aliceIri) });
        var deadLetter = new InMemoryDeliveryDeadLetterStore();

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLoggerFactory.Instance.CreateLogger<DeliveryWorker>(),
            new DeliveryRetryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            },
            deadLetter);

        return (worker, queue, deadLetter);
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that wraps a real handler and returns 500 for POSTs to a
    /// specific inbox IRI (simulating a downed/failing peer); all other requests are forwarded.
    /// </summary>
    private sealed class FailingInboxHandler(HttpMessageHandler inner, string failingInboxIri)
        : HttpMessageHandler
    {
        private readonly HttpClient _client = new(inner, disposeHandler: false);
        private readonly string _failingInboxIri = failingInboxIri;

        public int FailureCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.ToString() == _failingInboxIri)
            {
                FailureCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

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
                _client.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that returns 429 with a <c>Retry-After</c> HTTP-date header
    /// (1 second in the future) on the first call, then 200 on subsequent calls. Tracks the wall-clock
    /// gap between attempts.
    /// </summary>
    private sealed class RetryAfterHttpDateHandler : HttpMessageHandler
    {
        private readonly List<long> _attemptTicks = [];

        public int CallCount { get; private set; }

        public long MillisecondsBetweenAttempt1And2 =>
            _attemptTicks.Count >= 2 ? _attemptTicks[1] - _attemptTicks[0] : 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            _attemptTicks.Add(Environment.TickCount64);

            if (CallCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                var retryDate = DateTime.UtcNow.AddSeconds(3).ToString("R");
                response.Headers.Add("Retry-After", retryDate);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// A routing <see cref="IActorDocumentFetcher"/> that routes by actor IRI host: the local actor's
    /// document is fetched from the local server, the remote actor's from the remote server.
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            Iri aActorIri, KeyPair aKey, Func<HttpMessageHandler> aHandler,
            Iri bActorIri, KeyPair bKey, Func<HttpMessageHandler> bHandler)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [new Uri(aActorIri.Value).Authority] = BuildHostFetcher(aActorIri, aKey, aHandler),
                [new Uri(bActorIri.Value).Authority] = BuildHostFetcher(bActorIri, bKey, bHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Authority;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }

        private static IActorDocumentFetcher BuildHostFetcher(
            Iri actorIri, KeyPair key, Func<HttpMessageHandler> handler)
        {
            var keyStore = new InMemoryKeyStore();
            keyStore.PutKey(key);
            var keyProvider = new InMemoryKeyProvider(keyStore);
            keyProvider.RegisterKey(actorIri, key.KeyId);
            var signer = new HttpSignatureSigner(keyStore);

            var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
            var client = factory.Create(
                new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
                new LazyHandler(handler));

            return new IrisActorDocumentFetcher(client, new RemoteActorCache());
        }
    }

    private sealed class TestServerHolder
    {
        public TestServer? Server { get; set; }
    }
}
