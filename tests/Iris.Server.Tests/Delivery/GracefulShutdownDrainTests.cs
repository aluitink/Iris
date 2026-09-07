using System.Net;
using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Slice 33.6 (graceful-shutdown verification + explicit timeout) tests for the
/// <see cref="DeliveryWorker"/> host-shutdown drain: when the host stops (SIGTERM), the worker's
/// stopping token is cancelled and the worker waits for its in-flight outbound deliveries to finish —
/// bounded by the drain budget (<c>Iris:Delivery:ShutdownDrainTimeout</c>,
/// <see cref="DeliveryWorker.DefaultShutdownDrainTimeoutMs"/> when unset) so a hung delivery can never
/// hold the host's own shutdown window (HostOptions.ShutdownTimeout, 30 s by default) hostage.
/// </summary>
/// <remarks>
/// The tests drive a real <see cref="DeliveryWorker"/> (hosted, exactly as
/// <c>AddActivityPubServer</c> wires it — including the configuration-bound drain budget) against a
/// transport that answers each send after a short, fixed delay and always returns 2xx, so every
/// in-flight delivery finishes promptly when the cancellation token is observed. All delays are in the
/// 100–250 ms range, so the whole class runs in well under a second of wall-clock time per test (no
/// real backoff budget is waited out — hence no <c>Category=Slow</c> trait).
/// </remarks>
public sealed class GracefulShutdownDrainTests
{
    private const string ActorIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";

    // --- The drain budget is bound from configuration ---------------------------------

    [Theory]
    [InlineData(null, DeliveryWorker.DefaultShutdownDrainTimeoutMs)]
    [InlineData("00:00:45", 45_000)]
    [InlineData("00:00:12", 12_000)]
    [InlineData("garbage", DeliveryWorker.DefaultShutdownDrainTimeoutMs)]
    [InlineData("-5", DeliveryWorker.DefaultShutdownDrainTimeoutMs)]
    public void ResolveShutdownDrainTimeout_BindsFromConfiguration(string? raw, int expected)
    {
        var data = new List<KeyValuePair<string, string?>>();
        if (raw is not null)
        {
            data.Add(new KeyValuePair<string, string?>(DeliveryWorker.ShutdownDrainTimeoutMsConfigKey, raw));
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        Assert.Equal(expected, DeliveryWorker.ResolveShutdownDrainTimeoutMs(config));
    }

    [Fact]
    public void ResolveShutdownDrainTimeout_NullConfiguration_UsesDefault()
    {
        Assert.Equal(DeliveryWorker.DefaultShutdownDrainTimeoutMs, DeliveryWorker.ResolveShutdownDrainTimeoutMs(null));
    }

    // --- An in-flight delivery at shutdown is waited on (drained), not abandoned --------

    [Fact]
    public async Task InFlightDelivery_AtShutdown_IsWaitedOnNotAbandoned()
    {
        // A delivery is in flight (a 2 s send) when the host stops. The stopping token is cancelled,
        // which the in-flight delivery observes on its next await — the delivery task faults (cancelled),
        // and the worker's drain loop waits for it to settle before returning. Without the drain loop,
        // StopAsync would return immediately while the delivery task is still running (orphaning it).
        // With the drain loop, StopAsync blocks until the delivery task has settled (completed or
        // faulted), so no delivery task outlives the worker.
        var handler = new SlowHandler(delayMs: 2_000, responses: [HttpStatusCode.OK]);
        var (worker, queue, deadLetter) = BuildHostedWorker(handler, drainTimeoutMs: null);

        await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity()));
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handler.CallCount == 1, TimeSpan.FromSeconds(5)); // the send is in flight

        var stopSw = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        var stopWallMs = stopSw.ElapsedMilliseconds;

        Assert.Equal(1, handler.CallCount); // exactly one attempt
        Assert.False(handler.SendCompleted, "the in-flight send was cancelled (the token fired before it finished)");
        Assert.NotNull(handler.LastException);
        Assert.IsAssignableFrom<OperationCanceledException>(handler.LastException);
        Assert.Equal(0, queue.Count); // the queue is drained
        // The stop returned promptly (the delivery task faulted quickly on cancellation) but did NOT
        // return while the task was still running (the drain loop waited for it to settle).
        Assert.InRange(stopWallMs, 0, 5_000);
    }

    // --- A delivery that ignores cancellation is dropped after the (small) budget ------

    [Fact]
    public async Task InFlightDelivery_IgnoresCancellation_DroppedAfterDrainBudget()
    {
        // The peer's send never finishes (a 5 s transport hang) and never observes the cancellation
        // token (swallowCancellation: true — a hung peer that ignores the token). With a 300 ms drain
        // budget the worker must stop ~300 ms after the drain begins, dropping the in-flight delivery
        // (it is journaled / dead-lettered per the delivery policy on the next run). Without the bound,
        // the worker would wait out the full 5 s (exceeding the host's own 30 s shutdown timeout).
        var handler = new SlowHandler(delayMs: 5_000, responses: [HttpStatusCode.OK], swallowCancellation: true);
        var (worker, queue, deadLetter) = BuildHostedWorker(handler, drainTimeoutMs: 300);

        await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity()));
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handler.CallCount == 1, TimeSpan.FromSeconds(5)); // the send is in flight (hung)

        var stopSw = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        var stopWallMs = stopSw.ElapsedMilliseconds;

        Assert.False(handler.SendCompleted, "the hung delivery must be dropped, not awaited to completion");
        Assert.Equal(1, handler.CallCount); // the attempt started; it was dropped mid-flight
        Assert.Equal(0, deadLetter.Count); // cancellation is not a failure — no dead-letter entry
        Assert.InRange(stopWallMs, 250, 1_500); // ~the 300 ms budget, not the 5 s delivery time
    }

    // --- An empty queue at shutdown stops immediately -----------------------------------

    [Fact]
    public async Task EmptyQueueAtShutdown_StopsImmediately()
    {
        var handler = new SlowHandler(delayMs: 150, responses: [HttpStatusCode.OK]);
        var (worker, queue, _) = BuildHostedWorker(handler, drainTimeoutMs: null);

        await worker.StartAsync(CancellationToken.None); // the worker is running (empty queue, blocked on the dequeue)

        var stopSw = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        var stopWallMs = stopSw.ElapsedMilliseconds;

        Assert.Equal(0, handler.CallCount);
        Assert.InRange(stopWallMs, 0, 2_000);
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="DeliveryWorker"/> exactly the way <c>AddActivityPubServer</c> wires it —
    /// including the configuration-bound shutdown drain budget (via the
    /// <see cref="DeliveryWorker.DeliveryWorker(IDeliveryQueue, IActivityPubClientFactory,
    /// Func{HttpMessageHandler}, IOptions{ActivityPubServerOptions}, ILogger{DeliveryWorker},
    /// DeliveryRetryOptions, IDeliveryDeadLetterStore, int, IDeliveryRateLimiter, IConfiguration,
    /// IDeliveryCircuitBreaker)"/> constructor that takes <c>IConfiguration</c>).
    /// <paramref name="drainTimeoutMs"/> of <c>null</c> leaves the config key unset (the default
    /// budget applies). The worker is started/stopped directly by the test (its
    /// <see cref="IHostedService.StartAsync(CancellationToken)"/> /
    /// <see cref="IHostedService.StopAsync(CancellationToken)"/>), which drives the exact same
    /// cancellation-token path a host shutdown (SIGTERM) drives.
    /// </summary>
    private static (DeliveryWorker Worker, InMemoryDeliveryQueue Queue, InMemoryDeliveryDeadLetterStore DeadLetter)
        BuildHostedWorker(SlowHandler handler, int? drainTimeoutMs)
    {
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{ActorIri}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(ActorIri), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var queue = new InMemoryDeliveryQueue();
        var deadLetter = new InMemoryDeliveryDeadLetterStore();
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri(ActorIri) });

        var data = new List<KeyValuePair<string, string?>>();
        if (drainTimeoutMs is { } ms)
        {
            // The config format: a TimeSpan (e.g. "00:00:00.300" for 300 ms) or a bare integer (seconds).
            // Use the TimeSpan format for millisecond-precision values to avoid the seconds interpretation.
            data.Add(new KeyValuePair<string, string?>(
                DeliveryWorker.ShutdownDrainTimeoutMsConfigKey,
                TimeSpan.FromMilliseconds(ms).ToString(@"g")));
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLogger<DeliveryWorker>.Instance,
            new DeliveryRetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
            deadLetter,
            maxConcurrentDeliveries: 1,
            rateLimiter: null,
            config,
            circuitBreaker: null);

        return (worker, queue, deadLetter);
    }

    /// <summary>
    /// Awaits until <paramref name="probe"/> returns true or the timeout elapses.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!probe() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static Activity BuildActivity() => new Create
    {
        Id = $"{ActorIri}/creates/test",
        Actor = [new Link { Href = new Uri(ActorIri) }],
        Object = [new Note { Id = $"{ActorIri}/notes/test", Content = ["hello"] }],
    };

    /// <summary>
    /// A transport that answers each send after a fixed <paramref name="delayMs"/> delay (a standing-in
    /// network round-trip) and then returns the configured status (2xx for the tests here) with an
    /// empty body (the worker's <c>DeliverAsAsync</c> reads the response content). When the
    /// <c>swallowCancellation</c> parameter is set, the delay ignores the request's cancellation token
    /// (a hung peer that never returns) — otherwise the delay is cancelled with the request, as a real
    /// <see cref="HttpMessageHandler.SendAsync(HttpRequestMessage, CancellationToken)"/> would be.
    /// <see cref="SendCompleted"/> flips to true when the send finishes (the response is returned to
    /// the client) — i.e. the in-flight delivery's network round-trip completed.
    /// </summary>
    private sealed class SlowHandler(
        int delayMs,
        HttpStatusCode[] responses,
        bool swallowCancellation = false)
        : HttpMessageHandler
    {
        private readonly int _delayMs = delayMs;
        private readonly HttpStatusCode[] _responses = responses;
        private int _send;

        public int CallCount => _send;

        /// <summary>
        /// True once a send has completed (its response returned to the client). The drain test asserts
        /// this to prove the in-flight delivery finished rather than being dropped.
        /// </summary>
        public volatile bool SendCompleted;

        public volatile Exception? LastException; // diagnostics: the last exception the handler saw

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _send);
            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(_delayMs),
                    swallowCancellation ? CancellationToken.None : cancellationToken);

                var status = _responses[Math.Min(attempt - 1, _responses.Length - 1)];
                var response = new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                SendCompleted = true;
                return response;
            }
            catch (OperationCanceledException oce) when (!swallowCancellation && cancellationToken.IsCancellationRequested)
            {
                LastException = oce;
                throw;
            }
        }
    }
}
