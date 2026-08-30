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
/// Phase 16.3 tests for the <see cref="DeliveryWorker"/> per-peer outbound-delivery rate limit
/// (<see cref="DeliveryRateLimitOptions.PerPeerMaxRequestsPerMinute"/> /
/// <see cref="SlidingWindowDeliveryRateLimiter"/>): a burst of deliveries to a single peer is throttled
/// to the configured per-peer rate, different peers are rate-limited independently, a disabled limiter
/// (0, the default) passes through immediately, and the worker still drains completely and stops
/// cleanly (no deadlock) even when the limiter blocks a delivery.
/// </summary>
/// <remarks>
/// <strong>Limiter-level tests</strong> (the first four) drive a <see cref="SlidingWindowDeliveryRateLimiter"/>
/// directly with a short window so the sliding-window math is observable in milliseconds. <strong>Worker-level
/// tests</strong> (the last two) drive a real <see cref="DeliveryWorker"/> against a stub
/// <see cref="HttpMessageHandler"/> (mirroring the Phase 16.1 concurrency tests) to prove the gate is
/// wired in and that a rate-limited peer's blocking wait does not deadlock the pump.
/// </remarks>
public sealed class DeliveryWorkerRateLimitTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxBob = "https://b.domain.local/ap/v1/u/bob/inbox";
    private const string InboxCarol = "https://c.domain.local/ap/v1/u/carol/inbox";
    private const string InboxDave = "https://d.domain.local/ap/v1/u/dave/inbox";

    // --- Limiter: disabled (maxRequests == 0) returns immediately -----------------------

    [Fact]
    public async Task DisabledLimiter_ReturnsImmediately()
    {
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 0);
        var inbox = new Iri(InboxBob);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        }

        sw.Stop();
        // 100 calls with no waiting must complete in well under a second.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"disabled limiter should not block, but took {sw.ElapsedMilliseconds}ms");
    }

    // --- Limiter: a peer is throttled to maxRequests per window --------------------------

    [Fact]
    public async Task EnabledLimiter_ThrottlesPeerToMaxRequestsPerWindow()
    {
        // maxRequests = 2 per 200ms window. The first 2 calls should pass immediately; the 3rd must
        // wait until the earliest timestamp falls out of the window (≈200ms).
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 2, window: TimeSpan.FromMilliseconds(200));
        var inbox = new Iri(InboxBob);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // First two calls: immediate.
        await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        var afterTwo = sw.ElapsedMilliseconds;
        Assert.True(afterTwo < 150,
            $"first two calls should be immediate, but took {afterTwo}ms");

        // Third call: must wait for a slot to free (≈200ms).
        await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        var afterThree = sw.ElapsedMilliseconds;
        Assert.True(afterThree >= 180,
            $"third call should have waited ≈200ms, but total was only {afterThree}ms");
    }

    // --- Limiter: different peers are rate-limited independently -------------------------

    [Fact]
    public async Task DifferentPeers_AreRateLimitedIndependently()
    {
        // maxRequests = 1 per 200ms window. Two peers each make 1 call (both immediate). A second call
        // to peer A must wait, but a call to peer B (already used) must also wait independently.
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 1, window: TimeSpan.FromMilliseconds(200));
        var inboxA = new Iri(InboxBob);
        var inboxB = new Iri(InboxCarol);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // First call to A and first call to B: both immediate (independent budgets).
        await limiter.WaitUntilPermittedAsync(inboxA, CancellationToken.None);
        await limiter.WaitUntilPermittedAsync(inboxB, CancellationToken.None);
        var afterFirstRound = sw.ElapsedMilliseconds;
        Assert.True(afterFirstRound < 150,
            $"first call to each peer should be immediate, but took {afterFirstRound}ms");

        // Second call to A: must wait (A's budget is used).
        await limiter.WaitUntilPermittedAsync(inboxA, CancellationToken.None);
        var afterSecondA = sw.ElapsedMilliseconds;
        Assert.True(afterSecondA >= 180,
            $"second call to A should have waited ≈200ms, but total was only {afterSecondA}ms");
    }

    // --- Limiter: a peer's budget refills after the window elapses -----------------------

    [Fact]
    public async Task PeerBudget_RefillsAfterWindowElapses()
    {
        // maxRequests = 1 per 100ms window. Call A (immediate), wait >100ms, call A again (immediate).
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 1, window: TimeSpan.FromMilliseconds(100));
        var inbox = new Iri(InboxBob);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        await Task.Delay(150); // let the window slide past the first timestamp

        sw.Restart();
        await limiter.WaitUntilPermittedAsync(inbox, CancellationToken.None);
        sw.Stop();

        // The second call should be immediate (the first timestamp has expired).
        Assert.True(sw.ElapsedMilliseconds < 80,
            $"second call after window elapsed should be immediate, but took {sw.ElapsedMilliseconds}ms");
    }

    // --- Worker: disabled limiter (default) passes through without throttling ------------

    [Fact]
    public async Task Worker_DisabledRateLimit_DeliversBurstUnthrottled()
    {
        const int jobs = 6;
        const int concurrency = 4;
        var (worker, queue, handler) = BuildWorker(
            maxConcurrentDeliveries: concurrency,
            delayMs: 20,
            rateLimiter: null); // disabled (null)

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs);

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count);
    }

    // --- Worker: enabled limiter throttles a burst to a single peer ----------------------

    [Fact]
    public async Task Worker_EnabledRateLimit_ThrottlesBurstToSinglePeer()
    {
        const int jobs = 4;
        const int concurrency = 4;
        // 2 requests per 120ms window. The burst of 4 to the same peer must take ≥120ms (at least
        // one window must elapse for the 3rd/4th deliveries).
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 2, window: TimeSpan.FromMilliseconds(120));
        var (worker, queue, handler) = BuildWorker(
            maxConcurrentDeliveries: concurrency,
            delayMs: 10,
            rateLimiter: limiter);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs);
        sw.Stop();

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count);
        // Without the limiter, 4 deliveries with 10ms delay + 4-way concurrency would finish in ≈30ms.
        // With the limiter (2 per 120ms), the burst must take ≥120ms (the 3rd delivery waits for the
        // first to fall out of the window).
        Assert.True(sw.ElapsedMilliseconds >= 110,
            $"rate-limited burst should take ≥120ms, but took {sw.ElapsedMilliseconds}ms");
    }

    // --- Worker: rate-limited peer does not deadlock the pump ----------------------------

    [Fact]
    public async Task Worker_RateLimitedPeer_DrainsAndStops_NoDeadlock()
    {
        // A burst larger than the per-peer budget, with a tight overall deadline. If the limiter's
        // blocking wait deadlocked the pump (e.g. the dequeuer blocked behind a slot it never released),
        // this would hang past the deadline and EnqueueAndRunAsync would throw.
        const int jobs = 6;
        const int concurrency = 2;
        var limiter = new SlidingWindowDeliveryRateLimiter(maxRequests: 2, window: TimeSpan.FromMilliseconds(100));
        var (worker, queue, handler) = BuildWorker(
            maxConcurrentDeliveries: concurrency,
            delayMs: 5,
            rateLimiter: limiter);

        await EnqueueAndRunAsync(worker, queue, count: jobs, handler: handler,
            isDone: () => handler.CallCount == jobs,
            deadline: TimeSpan.FromSeconds(10));

        Assert.Equal(jobs, handler.CallCount);
        Assert.Equal(0, queue.Count);
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="DeliveryWorker"/> over a fresh in-memory queue + key store, configured with
    /// the given concurrency, a 1-attempt / 0-backoff retry policy (so each job makes exactly one
    /// attempt and no dead-lettering occurs), an optional rate limiter, and a
    /// <see cref="DelayingHandler"/> transport that holds each send open for <paramref name="delayMs"/>.
    /// </summary>
    private static (DeliveryWorker, InMemoryDeliveryQueue, DelayingHandler) BuildWorker(
        int maxConcurrentDeliveries, int delayMs, IDeliveryRateLimiter? rateLimiter)
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
            NullLogger<DeliveryWorker>.Instance,
            new DeliveryRetryOptions { MaxAttempts = 1, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
            new InMemoryDeliveryDeadLetterStore(),
            maxConcurrentDeliveries,
            rateLimiter);

        return (worker, queue, handler);
    }

    /// <summary>
    /// Enqueues <paramref name="count"/> jobs (all to <see cref="InboxBob"/>, the same peer), runs the
    /// worker as a hosted service, waits until <paramref name="isDone"/> reports the burst has been fully
    /// processed (delivered — not merely dequeued), then stops the host. Throws <see cref="TimeoutException"/>
    /// if the worker does not finish before <paramref name="deadline"/> (a hang / deadlock).
    /// </summary>
    private static async Task EnqueueAndRunAsync(
        DeliveryWorker worker,
        InMemoryDeliveryQueue queue,
        int count,
        Func<bool> isDone,
        DelayingHandler handler,
        TimeSpan? deadline = null)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.EnqueueAsync(new DeliveryJob(new Iri(InboxBob), BuildActivity($"note-{i}")));
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
    /// deliveries overlap in time) and records <see cref="CallCount"/>. All sends return 200 OK.
    /// </summary>
    private sealed class DelayingHandler(int delayMs) : HttpMessageHandler
    {
        private readonly int _delayMs = delayMs;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
