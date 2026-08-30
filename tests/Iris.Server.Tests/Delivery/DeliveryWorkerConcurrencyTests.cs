using System.Collections.Concurrent;
using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 16.1 tests for the <see cref="DeliveryWorker"/> bounded-concurrency pump
/// (<see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/>): a burst of deliveries is delivered in
/// parallel (overlapping the per-delivery network round-trips) but never exceeds the configured
/// concurrency cap, and the worker still drains completely and stops cleanly (no deadlock). A value of
/// 1 (the default) preserves the original serial behavior.
/// </summary>
/// <remarks>
/// These tests drive a real <see cref="DeliveryWorker"/> (run as a hosted service) against a
/// <em>delaying</em> transport: a stub <see cref="HttpMessageHandler"/> that tracks how many sends are
/// in flight at once (via a <c>ConcurrentDictionary</c> + a running max) and holds each send open for a
/// short delay so concurrent deliveries overlap in time. The retry budget is 1 and the backoff is 0 so
/// each job makes exactly one attempt and the test completes quickly.
/// </remarks>
public sealed class DeliveryWorkerConcurrencyTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";

    // --- A burst with concurrency > 1 is delivered in parallel (overlapping) -------------

    [Fact]
    public async Task Burst_WithConcurrencyGreaterThanOne_IsDeliveredInParallel()
    {
        const int jobs = 4;
        const int concurrency = 4;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: concurrency, delayMs: 80);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs && handler.MaxInFlight > 1);

        Assert.Equal(jobs, handler.CallCount); // every job delivered
        Assert.True(handler.MaxInFlight > 1,
            $"expected overlapping deliveries (MaxInFlight > 1) but saw {handler.MaxInFlight}");
        Assert.Equal(0, queue.Count); // drained
    }

    // --- Concurrency is bounded by MaxConcurrentDeliveries (never exceeds the cap) -------

    [Fact]
    public async Task Burst_NeverExceedsMaxConcurrentDeliveries()
    {
        const int jobs = 12;
        const int concurrency = 3;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: concurrency, delayMs: 60);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs);

        Assert.Equal(jobs, handler.CallCount);
        Assert.True(handler.MaxInFlight <= concurrency,
            $"expected at most {concurrency} in flight but saw {handler.MaxInFlight}");
        Assert.Equal(0, queue.Count);
    }

    // --- The default (concurrency = 1) is serial (MaxInFlight == 1) ----------------------

    [Fact]
    public async Task DefaultConcurrencyOne_IsSerial_MaxInFlightIsOne()
    {
        const int jobs = 4;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: 1, delayMs: 40);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs);

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(1, handler.MaxInFlight); // serial: at most one in flight
        Assert.Equal(0, queue.Count);
    }

    // --- The worker drains completely and stops (no semaphore deadlock) ------------------

    [Fact]
    public async Task Worker_DrainsAndStops_NoDeadlock()
    {
        // A burst larger than the concurrency cap, with a tight overall deadline. If the semaphore
        // logic deadlocked (e.g. the dequeuer blocked behind a slot it never released), this would hang
        // past the deadline and EnqueueAndRunAsync would throw.
        const int jobs = 10;
        const int concurrency = 3;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: concurrency, delayMs: 30);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs,
            deadline: TimeSpan.FromSeconds(10));

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count);
        Assert.True(handler.MaxInFlight <= concurrency);
    }

    // --- A burst that completes mid-burst still drains (queue completes with in-flight) ---

    [Fact]
    public async Task QueueCompletedWithInFlight_StillDrainsAllJobs()
    {
        const int jobs = 6;
        const int concurrency = 2;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: concurrency, delayMs: 50);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs,
            completeAfterEnqueue: true);

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count);
        Assert.True(handler.MaxInFlight <= concurrency);
    }

    // --- A sub-1 concurrency is clamped to 1 (serial) ------------------------------------

    [Fact]
    public async Task ConcurrencyBelowOne_IsClampedToSerial()
    {
        const int jobs = 3;
        var (worker, queue, handler) = BuildWorker(maxConcurrentDeliveries: 0, delayMs: 40);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs);

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(1, handler.MaxInFlight); // clamped to 1 → serial
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="DeliveryWorker"/> over a fresh in-memory queue + key store, configured with
    /// the given concurrency, a 1-attempt / 0-backoff retry policy (so each job makes exactly one
    /// attempt and no dead-lettering occurs), and a <see cref="DelayingHandler"/> transport that holds
    /// each send open for <paramref name="delayMs"/> and tracks the max in-flight count.
    /// </summary>
    private static (DeliveryWorker, InMemoryDeliveryQueue, DelayingHandler) BuildWorker(
        int maxConcurrentDeliveries, int delayMs)
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
        var handler = new DelayingHandler(delayMs);

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLoggerFactory.Instance.CreateLogger<DeliveryWorker>(),
            new DeliveryRetryOptions { MaxAttempts = 1, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
            new InMemoryDeliveryDeadLetterStore(),
            maxConcurrentDeliveries);

        return (worker, queue, handler);
    }

    /// <summary>
    /// Enqueues <paramref name="count"/> jobs, runs the worker as a hosted service, waits until
    /// <paramref name="isDone"/> reports the burst has been fully processed (delivered — not merely
    /// dequeued), then stops the host. When <paramref name="completeAfterEnqueue"/> is set the queue is
    /// completed right after enqueueing (so the worker must drain the in-flight jobs before stopping).
    /// Throws <see cref="TimeoutException"/> if the worker does not finish before <paramref name="deadline"/>
    /// (a hang / deadlock).
    /// </summary>
    private static async Task EnqueueAndRunAsync(
        DeliveryWorker worker,
        InMemoryDeliveryQueue queue,
        int count,
        Func<bool> isDone,
        DelayingHandler handler,
        bool completeAfterEnqueue = false,
        TimeSpan? deadline = null)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxIri), BuildActivity($"note-{i}")));
        }

        if (completeAfterEnqueue)
        {
            await queue.CompleteAsync(CancellationToken.None);
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        try
        {
            await host.StartAsync(CancellationToken.None);

            var limit = DateTime.UtcNow + (deadline ?? TimeSpan.FromSeconds(10));
            while (!isDone() && DateTime.UtcNow < limit)
            {
                await Task.Delay(20);
            }

            if (!isDone())
            {
                throw new TimeoutException(
                    $"worker did not finish in time (CallCount={handler.CallCount}/{count}); possible deadlock");
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    private static Activity BuildActivity(string noteId) => new Create
    {
        Id = $"{AliceIri}/creates/{noteId}",
        Actor = [new Link { Href = new Uri(AliceIri) }],
        Object = [new Note { Id = $"{AliceIri}/notes/{noteId}", Content = ["hello"] }],
    };

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that holds each send open for a delay (so concurrent
    /// deliveries overlap in time) and records <see cref="MaxInFlight"/> — the peak number of sends in
    /// flight simultaneously — plus <see cref="CallCount"/>. All sends return 200 OK.
    /// </summary>
    private sealed class DelayingHandler(int delayMs) : HttpMessageHandler
    {
        private readonly int _delayMs = delayMs;
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
            Interlocked.Increment(ref _callCount);

            // Record the peak in-flight count (only ever increases while a send is in flight).
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
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                _inFlight.TryRemove(token, out _);
            }
        }
    }
}
