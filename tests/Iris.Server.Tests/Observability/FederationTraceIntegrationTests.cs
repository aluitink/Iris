using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 136.1 integration tests: the <see cref="IFederationTraceCollector"/> captures outbound
/// deliveries (via the <see cref="DeliveryWorker"/>) and the collector itself behaves as a bounded,
/// ordered, clearable buffer. The outbound tests drive a real worker (run as a hosted service) against
/// a failable transport — mirroring <c>DeliveryMetricsIntegrationTests</c> — and assert the trace
/// snapshot reflects the recipient inbox, the acting actor, the activity type, and the outcome status.
/// </summary>
public sealed class FederationTraceIntegrationTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";

    [Fact]
    public async Task SuccessfulDelivery_CapturesOutboundTraceEntry()
    {
        var trace = new InMemoryFederationTraceCollector();
        var (worker, queue) = BuildWorker(responses: [HttpStatusCode.OK], maxAttempts: 5, trace);

        await EnqueueAndRunAsync(worker, queue, isDone: () => trace.Snapshot().Count > 0);

        var snapshot = trace.Snapshot();
        Assert.Single(snapshot);
        var entry = snapshot[0];
        Assert.Equal(FederationDirection.Outbound, entry.Direction);
        Assert.Equal("POST", entry.Method);
        Assert.Equal(InboxIri, entry.Url);
        Assert.Equal(200, entry.Status);
        Assert.Equal(InboxIri, entry.PeerIri);
        Assert.Equal(AliceIri, entry.ActorIri); // signed as the instance actor (no acting actor on the job)
        Assert.Equal("Create", entry.ActivityType);
    }

    [Fact]
    public async Task DeliveryWithActingActor_TraceUsesActingActorNotInstanceActor()
    {
        var actingActor = new Iri("https://a.domain.local/ap/v1/u/carol");
        var trace = new InMemoryFederationTraceCollector();
        var (worker, queue) = BuildWorker(responses: [HttpStatusCode.OK], maxAttempts: 5, trace, actingActor);

        await EnqueueAndRunAsync(worker, queue, isDone: () => trace.Snapshot().Count > 0, actingActor);

        var entry = Assert.Single(trace.Snapshot());
        Assert.Equal(actingActor.Value, entry.ActorIri);
        Assert.Equal(200, entry.Status);
    }

    [Fact]
    public async Task PermanentFailure_TraceCapturesNonSuccessStatus()
    {
        var trace = new InMemoryFederationTraceCollector();
        // One attempt, a 400 (permanent) — the worker dead-letters immediately and the trace records
        // the 400 status (the per-attempt capture, including the final failed attempt).
        var (worker, queue) = BuildWorker(responses: [HttpStatusCode.BadRequest], maxAttempts: 1, trace);

        await EnqueueAndRunAsync(worker, queue, isDone: () => trace.Snapshot().Count > 0);

        var entry = Assert.Single(trace.Snapshot());
        Assert.Equal(400, entry.Status);
        Assert.Equal(FederationDirection.Outbound, entry.Direction);
        Assert.Equal("Create", entry.ActivityType);
    }

    [Fact]
    public async Task RetriedDelivery_TraceCapturesEachAttempt()
    {
        var trace = new InMemoryFederationTraceCollector();
        // 500 then 200: two attempts, two trace entries (one per attempt) — the retry sequence is
        // visible in the trace, which is the per-attempt granularity an operator needs.
        var (worker, queue) = BuildWorker(
            responses: [HttpStatusCode.InternalServerError, HttpStatusCode.OK], maxAttempts: 5, trace);

        // Wait until the delivery succeeds (a 2xx attempt), which means both attempts have been recorded.
        await EnqueueAndRunAsync(worker, queue, isDone: () => trace.Snapshot().Count == 2);

        var snapshot = trace.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(500, snapshot[0].Status);
        Assert.Equal(200, snapshot[1].Status);
        Assert.All(snapshot, e => Assert.Equal(FederationDirection.Outbound, e.Direction));
    }

    // --- Collector unit behavior ---------------------------------------------------------

    [Fact]
    public void Collector_BoundedBuffer_DropsOldestWhenFull()
    {
        var collector = new InMemoryFederationTraceCollector(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            collector.Record(Entry(i));
        }

        var snapshot = collector.Snapshot();
        Assert.Equal(3, snapshot.Count);
        // The two oldest (1, 2) were dropped; 3, 4, 5 remain in order.
        Assert.Equal(3, snapshot[0].Status);
        Assert.Equal(4, snapshot[1].Status);
        Assert.Equal(5, snapshot[2].Status);
    }

    [Fact]
    public void Collector_ZeroCapacity_DisablesCapture()
    {
        var collector = new InMemoryFederationTraceCollector(capacity: 0);
        collector.Record(Entry(1));
        collector.Record(Entry(2));
        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public void Collector_Clear_RemovesAllEntries()
    {
        var collector = new InMemoryFederationTraceCollector();
        collector.Record(Entry(1));
        collector.Record(Entry(2));
        collector.Clear();
        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public void Collector_Snapshot_IsAStableCopy()
    {
        var collector = new InMemoryFederationTraceCollector();
        collector.Record(Entry(1));
        var first = collector.Snapshot();
        collector.Record(Entry(2));
        // The earlier snapshot is unaffected by the later Record.
        Assert.Single(first);
        Assert.Equal(1, first[0].Status);
        Assert.Equal(2, collector.Snapshot().Count);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static FederationTraceEntry Entry(int status) => new(
        DateTimeOffset.UtcNow,
        FederationDirection.Inbound,
        "POST",
        "/ap/v1/u/alice/inbox",
        status,
        AliceIri,
        null,
        "Create");

    private static async Task EnqueueAndRunAsync(
        DeliveryWorker worker, InMemoryDeliveryQueue queue, Func<bool> isDone, Iri? actingActor = null)
    {
        await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity(), actingActor));

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

    private static (DeliveryWorker, InMemoryDeliveryQueue) BuildWorker(
        HttpStatusCode[] responses,
        int maxAttempts,
        IFederationTraceCollector trace,
        Iri? actingActor = null)
    {
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{AliceIri}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(AliceIri), key.KeyId);

        // When the delivery acts on behalf of another actor (the X-Iris-Actor override), the signing
        // identity for THAT actor must be resolvable — register a key for it in the fixture.
        if (actingActor is { } actor)
        {
            var actorKey = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{actor}#key-1"));
            keyStore.PutKey(actorKey);
            keyProvider.RegisterKey(actor, actorKey.KeyId);
        }

        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        var queue = new InMemoryDeliveryQueue();
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri(AliceIri) });
        var handler = new FailableHandler(responses);
        var deadLetter = new InMemoryDeliveryDeadLetterStore();

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLogger<DeliveryWorker>.Instance,
            new DeliveryRetryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            },
            deadLetter,
            1,
            rateLimiter: null,
            trace: trace);

        return (worker, queue);
    }

    private static Activity BuildActivity() => new Create
    {
        Id = $"{AliceIri}/creates/test",
        Actor = [new Link { Href = new Uri(AliceIri) }],
        Object = [new Note { Id = $"{AliceIri}/notes/test", Content = ["hello"] }],
    };

    private sealed class FailableHandler(HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _responses = responses;
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _callCount++;
            var status = _responses.Length == 0
                ? HttpStatusCode.OK
                : _responses[Math.Min(_callCount - 1, _responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
