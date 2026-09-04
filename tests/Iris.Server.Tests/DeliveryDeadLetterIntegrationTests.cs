using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 26 slice 26.5 end-to-end test (F-22 dead-letter store wiring): a <em>failed</em> cross-instance
/// delivery in a real 2-instance topology is dead-lettered in the receiving instance's
/// <see cref="IDeliveryDeadLetterStore"/> with the correct inbox IRI, acting-actor IRI, attempt count,
/// and failure kind.
/// </summary>
/// <remarks>
/// Two live in-process <see cref="TestServer"/> instances (A and B). Bob (on B) follows alice (on A); A's
/// <see cref="Iris.Server.Inbox.FollowActivityHandler"/> auto-constructs an <c>Accept</c> and A's
/// <see cref="Iris.Server.Delivery.DeliveryWorker"/> delivers it to bob's inbox on B. B's inbox returns
/// <c>500</c> (a failable transport wrapping B's <see cref="TestServer"/> handler), so A's worker retries
/// the delivery up to its configured budget (<see cref="Iris.Server.Delivery.DeliveryRetryOptions.MaxAttempts"/>)
/// and then moves the exhausted job to the <see cref="IDeliveryDeadLetterStore"/> — the same singleton the
/// worker was constructed with (the DI registration the host's <c>AddActivityPubServer</c> makes).
/// </remarks>
/// <remarks>
/// The non-vacuous signal is the dead-letter store's contents: after the failed delivery, A's
/// <see cref="IDeliveryDeadLetterStore"/> holds exactly one entry whose <see cref="DeadLetterEntry.InboxIri"/>
/// is bob's inbox, whose <see cref="DeadLetterEntry.ActorIri"/> is alice (the acting actor of the
/// auto-<c>Accept</c>), whose <see cref="DeadLetterEntry.FailureKind"/> is
/// <see cref="DeadLetterFailureKind.NonSuccessStatus"/>, whose <see cref="DeadLetterEntry.FailureDetail"/>
/// is <c>"500"</c>, and whose <see cref="DeadLetterEntry.Attempts"/> equals the configured retry budget.
/// Without the dead-letter store wiring (the worker's <c>DeadLetterAsync</c> path or the shared
/// singleton), the store would remain empty and the wait for <c>Count == 1</c> would time out.
/// </remarks>
public sealed class DeliveryDeadLetterIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    // The retry budget A's DeliveryWorker uses (the default DeliveryRetryOptions): 5 attempts, 1s base
    // delay, 60s max delay. A 500 (non-2xx, non-4xx) is transient, so all 5 attempts fail before the job
    // is dead-lettered. Total backoff: 1+2+4+8 = 15s.
    private const int MaxAttempts = 5;

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;

    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceIri;
    private readonly Iri _aliceInboxIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;
    private readonly FailingInboxHandler _failingTransport;

    public DeliveryDeadLetterIntegrationTests()
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

        // B is created first (it needs no wiring to A). A's outbound DeliveryWorker routes to B's
        // TestServer, but a failable transport wraps the real handler and returns 500 for POSTs to bob's
        // inbox (simulating a downed/failing peer). A's inbound key resolution must fetch B's actor
        // documents (to validate bob's Follow signature), also routed to B's TestServer.
        _b = StartServer(BHost, Bob, _bPersistence);
        _failingTransport = new FailingInboxHandler(_b.CreateHandler(), _bobInboxIri.Value);
        var aFetcher = TestFederation.BuildFetcherFor(AHost, Alice, _aliceKey, _b);

        _a = StartServer(
            AHost, Alice, _aPersistence,
            fetcher: aFetcher.Fetcher,
            deliveryTransport: () => _failingTransport);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public async Task FailedCrossInstanceDelivery_IsDeadLettered_InRealTopology()
    {
        // A's dead-letter store: the same singleton the DeliveryWorker was constructed with (the DI
        // registration from AddActivityPubServer). Grab it before triggering the delivery.
        var deadLetter = _a.Services.GetRequiredService<IDeliveryDeadLetterStore>();
        Assert.Equal(0, deadLetter.Count); // empty at the start (nothing dead-lettered yet)

        // Diagnostic: confirm the DeliveryWorker is registered as a hosted service and that the
        // IDeliveryDeadLetterStore is the same instance the worker will use.
        var hostedServices = _a.Services.GetServices<IHostedService>().ToList();
        var workerRegistered = hostedServices.OfType<DeliveryWorker>().Any();
        var storeInstances = _a.Services.GetServices<IDeliveryDeadLetterStore>().ToList();
        var isSameInstance = storeInstances.Any(s => ReferenceEquals(s, deadLetter));
        Assert.True(workerRegistered, $"DeliveryWorker not registered as a hosted service. Hosted services: {string.Join(", ", hostedServices.Select(h => h.GetType().Name))}");
        Assert.True(isSameInstance, $"IDeliveryDeadLetterStore has {storeInstances.Count} registrations, and the resolved instance is not among them (should be a singleton).");

        // Bob (on B) follows alice (on A): bob posts a signed Follow to alice's inbox on A. A validates
        // bob's key (fetching B's actor doc) and records the follow edge, then auto-constructs an Accept
        // (actor = alice, object = the follow) and enqueues it for delivery to bob's inbox on B.
        var follow = BuildFollow(_bobActorIri, _aliceIri);
        using (var client = BuildDeliveryClient(_bobActorIri, _bobKey, _a.CreateHandler()))
        {
            var result = await client.DeliverAsync(_aliceInboxIri, follow);
            Assert.True(
                result.StatusCode == 202,
                $"Expected 202 (bob's follow accepted by A), got {result.StatusCode}");
        }

        // A's DeliveryWorker delivers the auto-Accept to bob's inbox on B. B's inbox returns 500 (the
        // failable transport), so the worker retries up to MaxAttempts (5, with 1s/2s/4s/8s backoff)
        // and then dead-letters the exhausted job. Wait for the dead-letter store to hold the entry
        // (the backoff delays total ~15s, so allow 30s for the full retry cycle).
        await TestFederation.WaitForAsync(
            () => Task.FromResult(deadLetter.Count == 1),
            TimeSpan.FromSeconds(30));

        // The store holds exactly one entry (the failed Accept delivery).
        var entries = await deadLetter.ListAsync();
        var queue = _a.Services.GetRequiredService<IDeliveryQueue>();
        Assert.True(
            entries.Count == 1,
            $"Expected 1 dead-lettered entry, got {entries.Count}. FailingInboxHandler served {_failingTransport.FailureCount} 500s (0 means the worker used a different transport). DeliveryQueue count={queue.Count}.");
        var entry = entries[0];

        // The entry records the correct target inbox (bob's inbox on B), the acting actor (alice, the
        // auto-Accept's actor), the failure kind (non-2xx), the last status (500), and the attempt count
        // (the configured retry budget).
        Assert.Equal(_bobInboxIri.Value, entry.InboxIri.Value);
        Assert.Equal(_aliceIri.Value, entry.ActorIri?.Value);
        Assert.Equal(DeadLetterFailureKind.NonSuccessStatus, entry.FailureKind);
        Assert.Equal("500", entry.FailureDetail);
        Assert.Equal(MaxAttempts, entry.Attempts);

        // The dead-lettered activity is the Accept (A's auto-response to bob's follow).
        Assert.NotNull(entry.Activity);
        Assert.NotNull(entry.Activity.Id);
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that wraps a real handler (routing to the other instance's
    /// <see cref="TestServer"/>) and returns <c>500</c> for POSTs to <paramref name="failingInboxIri"/>
    /// (simulating a downed/failing peer's inbox); all other requests are forwarded to the real handler.
    /// Follows the <see cref="LazyHandler"/> pattern: the inner handler is wrapped in an
    /// <see cref="HttpClient"/> (whose <c>SendAsync</c> is public) and the request is cloned before
    /// forwarding, because the in-process transport does not clone between sends and
    /// <see cref="HttpClient"/> forbids sending the same request message more than once (a retry
    /// pipeline may attempt to).
    /// </summary>
    private sealed class FailingInboxHandler(HttpMessageHandler inner, string failingInboxIri)
        : HttpMessageHandler
    {
        private readonly HttpClient _client = new(inner, disposeHandler: false);
        private readonly string _failingInboxIri = failingInboxIri;

        /// <summary>The number of 500 responses served (diagnostics; the test does not assert on it).</summary>
        public int FailureCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.ToString() == _failingInboxIri)
            {
                FailureCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            // Clone the request (the inner pipeline may retry; HttpClient forbids resending a message).
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

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null,
        Action<IServiceCollection>? extraServices = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            DeliveryTransport = deliveryTransport,
            ExtraServices = extraServices,
        });

    private static Follow BuildFollow(Iri actorIri, Iri objectIri) => new()
    {
        Id = $"https://{BHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };
}
