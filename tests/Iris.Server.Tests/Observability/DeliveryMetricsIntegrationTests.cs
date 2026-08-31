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
/// Phase 17.2 integration tests: the <see cref="IrisDeliveryMetrics"/> accrues as the
/// <see cref="DeliveryWorker"/> delivers. A real worker (run as a hosted service) drives a
/// failable transport; the test asserts the <see cref="IrisDeliveryMetrics.Snapshot"/> reflects the
/// enqueued / delivered / attempt-failed / dead-lettered counts after the job is fully processed.
/// </summary>
public sealed class DeliveryMetricsIntegrationTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";

    [Fact]
    public async Task SuccessfulDelivery_AccruesDelivered()
    {
        var metrics = new IrisDeliveryMetrics();
        var (worker, queue, _) = BuildWorker(
            responses: [HttpStatusCode.OK], maxAttempts: 5, metrics);

        // Wait until the worker has delivered the job (Delivered > 0), not just dequeued it.
        await EnqueueAndRunAsync(worker, queue, isDone: () => metrics.Snapshot.Delivered > 0);

        var snapshot = metrics.Snapshot;
        Assert.Equal(0, snapshot.Enqueued); // recorded by DeliveryService, not the worker
        Assert.Equal(1, snapshot.Delivered);
        Assert.Equal(0, snapshot.AttemptFailed);
        Assert.Equal(0, snapshot.DeadLettered);
        Assert.Equal(1, snapshot.ByActivityType["Create"].Delivered);
    }

    [Fact]
    public async Task TransientFailureThenSuccess_AccruesAttemptFailedAndDelivered()
    {
        var metrics = new IrisDeliveryMetrics();
        var (worker, queue, _) = BuildWorker(
            responses: [HttpStatusCode.InternalServerError, HttpStatusCode.OK],
            maxAttempts: 5, metrics);

        // Wait until the worker has delivered the job (Delivered > 0), not just dequeued it.
        await EnqueueAndRunAsync(worker, queue, isDone: () => metrics.Snapshot.Delivered > 0);

        var snapshot = metrics.Snapshot;
        Assert.Equal(1, snapshot.Delivered);
        Assert.Equal(1, snapshot.AttemptFailed); // the first attempt (500) failed
        Assert.Equal(0, snapshot.DeadLettered);
        Assert.Equal(1, snapshot.ByFailureKind["NonSuccessStatus"]);
    }

    [Fact]
    public async Task PermanentFailure_AccruesDeadLettered()
    {
        var metrics = new IrisDeliveryMetrics();
        var (worker, queue, deadLetter) = BuildWorker(
            responses: [HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError],
            maxAttempts: 3, metrics);

        await EnqueueAndRunAsync(worker, queue, isDone: () => deadLetter!.Count == 1);

        var snapshot = metrics.Snapshot;
        Assert.Equal(0, snapshot.Delivered);
        Assert.Equal(3, snapshot.AttemptFailed); // all 3 attempts failed
        Assert.Equal(1, snapshot.DeadLettered);
        // ByFailureKind aggregates BOTH attempt-failures AND dead-letters: 3 attempts + 1 dead-letter = 4.
        Assert.Equal(4, snapshot.ByFailureKind["NonSuccessStatus"]);
        Assert.Equal(1, snapshot.ByActivityType["Create"].DeadLettered);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static async Task EnqueueAndRunAsync(DeliveryWorker worker, InMemoryDeliveryQueue queue, Func<bool> isDone)
    {
        await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity()));

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

    private static (DeliveryWorker, InMemoryDeliveryQueue, IDeliveryDeadLetterStore?) BuildWorker(
        HttpStatusCode[] responses, int maxAttempts, IrisDeliveryMetrics metrics)
    {
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{AliceIri}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(AliceIri), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        var queue = new InMemoryDeliveryQueue();
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri(AliceIri) });
        var handler = new FailableHandler(responses);
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
            deadLetter,
            1,
            null,
            metrics);

        return (worker, queue, deadLetter);
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
