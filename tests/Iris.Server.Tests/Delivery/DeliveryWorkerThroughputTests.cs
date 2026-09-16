using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 147.3 (139.5 scenario 3) — delivery-worker throughput under burst. Drives a large burst of
/// outbound deliveries to many distinct peers through a real <see cref="DeliveryWorker"/> and verifies
/// the three pass criteria: (1) bounded concurrency (Phase 16.1) holds under the burst, (2) no unbounded
/// queue growth (the queue's peak depth never exceeds its capacity and it drains to zero), and (3)
/// throughput is reasonable and scales with the concurrency cap (a parallel burst drains faster than a
/// serial one of equal size). A per-peer transport delay models the fixed per-delivery network
/// round-trip so overlapping deliveries is what buys the throughput gain.
/// </summary>
/// <remarks>
/// Unlike the Phase 16.1 <c>DeliveryWorkerConcurrencyTests</c> (which isolate the concurrency bound on a
/// small burst to a single peer), these tests measure the <em>throughput</em> axis: a large burst to many
/// peers, timing the drain, and asserting the queue never balloons. The metrics surface
/// (<see cref="IrisDeliveryMetrics"/>) is wired in so the test can confirm the worker reports
/// <c>Delivered == Enqueued == burst size</c> (nothing dropped, nothing dead-lettered on a healthy peer
/// set).
/// </remarks>
public sealed class DeliveryWorkerThroughputTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";

    // --- A large burst to many peers drains completely (all delivered, none lost) ----------

    [Fact]
    public async Task LargeBurst_ToManyPeers_DrainsCompletely_NothingLost()
    {
        const int peers = 10;
        const int jobsPerPeer = 20; // 200 total
        const int jobs = peers * jobsPerPeer;
        const int concurrency = 8;
        const int delayMs = 20;
        const int capacity = InMemoryDeliveryQueue.DefaultCapacity;

        var (worker, queue, handler, metrics) = BuildWorker(
            maxConcurrentDeliveries: concurrency, delayMs: delayMs, capacity: capacity);

        // Enqueue the burst, then complete the queue so the worker drains it naturally (the pump exits
        // only once the queue is complete AND empty AND every in-flight delivery has finished). Waiting
        // for natural completion avoids the race where a host stop cancels a send that has started but
        // not yet returned 200 (which would under-count Delivered).
        for (var p = 0; p < peers; p++)
        {
            for (var j = 0; j < jobsPerPeer; j++)
            {
                await queue.EnqueueAsync(BuildJob(p, j));
            }
        }

        await queue.CompleteAsync(CancellationToken.None);
        await RunWorkerAsync(worker, handler, done: () => handler.CallCount == jobs, deadline: TimeSpan.FromSeconds(30));

        Assert.Equal(jobs, handler.CallCount); // every delivery made
        Assert.Equal(0, queue.Count); // drained to zero
        Assert.True(handler.MaxInFlight <= concurrency,
            $"bounded concurrency violated: expected <= {concurrency} in flight, saw {handler.MaxInFlight}");

        // The metrics surface reports the full burst as delivered (nothing dropped / dead-lettered).
        // (Enqueued is recorded by the DeliveryService, not the queue/worker — this test enqueues
        // directly, so only the worker-side counters (Delivered / DeadLettered) are asserted.)
        var snap = metrics.Snapshot;
        Assert.Equal(jobs, snap.Delivered);
        Assert.Equal(0, snap.DeadLettered);
    }

    // --- No unbounded queue growth: peak depth never exceeds capacity, drains to zero -------

    [Fact]
    public async Task Burst_QueueDepth_BoundedByCapacity_AndDrains()
    {
        // A capacity well below the burst size forces the enqueue side to apply back-pressure (the
        // channel fills, EnqueueAsync awaits space) — the realistic "burst larger than the queue" case.
        // The queue must never exceed its capacity and must drain to zero.
        const int peers = 8;
        const int jobsPerPeer = 15; // 120 total
        const int jobs = peers * jobsPerPeer;
        const int concurrency = 4;
        const int delayMs = 15;
        const int capacity = 32; // < jobs → back-pressure engages

        var (worker, queue, handler, _) = BuildWorker(
            maxConcurrentDeliveries: concurrency, delayMs: delayMs, capacity: capacity);

        // Enqueue in the background (the burst is larger than the capacity, so this awaits space as the
        // worker drains). Track the peak observed queue depth while enqueuing. When the enqueue side
        // finishes, complete the queue so the worker drains the remainder naturally (avoiding the host-
        // stop-cancel race that would under-count Delivered).
        var peakDepth = 0;
        var enqueue = Task.Run(async () =>
        {
            for (var i = 0; i < jobs; i++)
            {
                await queue.EnqueueAsync(BuildJob(i));
                var d = queue.Count;
                if (d > peakDepth)
                {
                    peakDepth = d;
                }
            }

            await queue.CompleteAsync(CancellationToken.None);
        });

        await RunWorkerAsync(worker, handler, done: () => handler.CallCount == jobs, deadline: TimeSpan.FromSeconds(30));
        await enqueue;

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count); // fully drained
        Assert.True(peakDepth <= capacity,
            $"unbounded queue growth: peak depth {peakDepth} exceeded capacity {capacity}");
    }

    // --- Throughput scales with concurrency: parallel burst drains faster than serial -------

    [Fact]
    public async Task Throughput_ScalesWithConcurrency_ParallelFasterThanSerial()
    {
        const int jobs = 16;
        const int delayMs = 50;

        // Serial (concurrency 1): each delivery waits for the previous, so total ≈ jobs * delay.
        var (serialWorker, serialQueue, serialHandler, _) = BuildWorker(
            maxConcurrentDeliveries: 1, delayMs: delayMs, capacity: InMemoryDeliveryQueue.DefaultCapacity);
        var serialElapsed = await TimeDrainAsync(serialWorker, serialQueue, jobs, serialHandler, delayMs);

        // Parallel (concurrency = jobs): every delivery overlaps, so total ≈ delay (one round-trip).
        var (parallelWorker, parallelQueue, parallelHandler, _) = BuildWorker(
            maxConcurrentDeliveries: jobs, delayMs: delayMs, capacity: InMemoryDeliveryQueue.DefaultCapacity);
        var parallelElapsed = await TimeDrainAsync(parallelWorker, parallelQueue, jobs, parallelHandler, delayMs);

        // Both deliver everything...
        Assert.Equal(jobs, serialHandler.CallCount);
        Assert.Equal(jobs, parallelHandler.CallCount);
        // ...and the parallel burst is meaningfully faster (at least ~2x, given full overlap vs. serial).
        Assert.True(parallelElapsed < serialElapsed / 2,
            $"expected parallel ({parallelElapsed.TotalMilliseconds:0} ms) to be < serial/2 " +
            $"({serialElapsed.TotalMilliseconds / 2:0} ms); concurrency is not overlapping deliveries");
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="DeliveryWorker"/> over a fresh bounded in-memory queue (the given
    /// <paramref name="capacity"/>), a 1-attempt / 0-backoff retry policy, and a
    /// <see cref="DelayingHandler"/> that holds each send for <paramref name="delayMs"/> and tracks the
    /// peak in-flight count. Returns the worker, the queue (for depth observation), the handler (for
    /// call/throughput observation), and a wired-in <see cref="IrisDeliveryMetrics"/> instance.
    /// </summary>
    private static (DeliveryWorker, InMemoryDeliveryQueue, DelayingHandler, IrisDeliveryMetrics) BuildWorker(
        int maxConcurrentDeliveries, int delayMs, int capacity)
    {
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.Generate(KeyAlgorithm.EcP256, new Iri($"{AliceIri}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(AliceIri), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        var queue = new InMemoryDeliveryQueue(capacity);
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = new Iri(AliceIri) });
        var handler = new DelayingHandler(delayMs);
        var metrics = new IrisDeliveryMetrics();

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLoggerFactory.Instance.CreateLogger<DeliveryWorker>(),
            new DeliveryRetryOptions { MaxAttempts = 1, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
            new InMemoryDeliveryDeadLetterStore(),
            maxConcurrentDeliveries,
            null, // rate limiter
            metrics,
            null, // circuit breaker
            null, // shutdown drain budget (default)
            null); // trace

        return (worker, queue, handler, metrics);
    }

    /// <summary>
    /// Runs the worker as a hosted service and waits (up to <paramref name="deadline"/>) until
    /// <paramref name="done"/> reports the burst is fully delivered. Throws <see cref="TimeoutException"/>
    /// on a hang.
    /// </summary>
    private static async Task RunWorkerAsync(
        DeliveryWorker worker,
        DelayingHandler handler,
        Func<bool> done,
        TimeSpan? deadline = null)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        try
        {
            await host.StartAsync(CancellationToken.None);

            var limit = DateTime.UtcNow + (deadline ?? TimeSpan.FromSeconds(30));
            while (!done() && DateTime.UtcNow < limit)
            {
                await Task.Delay(20);
            }

            if (!done())
            {
                throw new TimeoutException(
                    $"worker did not finish in time (CallCount={handler.CallCount}); possible deadlock");
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    /// <summary>
    /// Enqueues <paramref name="jobs"/> jobs to a single peer, completes the queue, runs the worker until
    /// fully drained, and returns the elapsed wall-clock time from enqueue-start to full drain. Used to
    /// compare serial vs. parallel throughput (the queue is completed so the worker drains naturally,
    /// avoiding the host-stop-cancel race).
    /// </summary>
    private static async Task<TimeSpan> TimeDrainAsync(
        DeliveryWorker worker,
        InMemoryDeliveryQueue queue,
        int jobs,
        DelayingHandler handler,
        int delayMs)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < jobs; i++)
        {
            await queue.EnqueueAsync(BuildJob(i));
        }

        await queue.CompleteAsync(CancellationToken.None);

        await RunWorkerAsync(worker, handler, done: () => handler.CallCount == jobs,
            deadline: TimeSpan.FromSeconds(30));
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>
    /// Builds a <see cref="DeliveryJob"/> targeting the <c>peer</c>-th peer's inbox. A single-argument
    /// overload targets peer 0 (used by the single-peer serial/parallel throughput comparison).
    /// </summary>
    private static DeliveryJob BuildJob(int jobIndex) => BuildJob(peer: 0, jobIndex);

    /// <summary>
    /// Builds a <see cref="DeliveryJob"/> targeting <c>https://p{peer}.domain.local/.../inbox</c> carrying a
    /// <c>Create</c> activity signed by the local actor.
    /// </summary>
    private static DeliveryJob BuildJob(int peer, int jobIndex)
    {
        var inboxIri = new Iri($"https://p{peer}.domain.local/ap/v1/inbox");
        return new DeliveryJob(inboxIri, new Create
        {
            Id = $"{AliceIri}/creates/p{peer}-{jobIndex}",
            Actor = [new Link { Href = new Uri(AliceIri) }],
            Object = [new Note { Id = $"{AliceIri}/notes/p{peer}-{jobIndex}", Content = ["burst"] }],
        });
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that holds each send open for a fixed delay (so concurrent
    /// deliveries overlap in time) and records <see cref="MaxInFlight"/> (the peak number of sends in
    /// flight simultaneously) plus <see cref="CallCount"/>. All sends return 200 OK.
    /// </summary>
    private sealed class DelayingHandler(int delayMs) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<object, byte> _inFlight = new();
        private int _callCount;
        private int _maxInFlight;

        public int CallCount => Volatile.Read(ref _callCount);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = new object();
            _inFlight[token] = 0;

            var current = _inFlight.Count;
            while (true)
            {
                var observed = Volatile.Read(ref _maxInFlight);
                if (current <= observed)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _maxInFlight, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

                // Count the send as delivered only once it has completed (returned 200), so a caller
                // waiting for CallCount == N is guaranteed every send finished (not merely started).
                Interlocked.Increment(ref _callCount);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                _inFlight.TryRemove(token, out _);
            }
        }
    }
}
