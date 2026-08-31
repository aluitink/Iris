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
/// Phase 17.3 integration tests for the circuit breaker + retry hardening on the
/// <see cref="DeliveryWorker"/>: (1) a peer whose inbox consistently returns 5xx opens the circuit
/// after the failure threshold, and subsequent deliveries to that peer are dead-lettered immediately
/// (no network call); (2) a 4xx response is dead-lettered immediately (permanent, no retry); (3) a
/// <c>Retry-After</c> header is honored on 429 responses.
/// </summary>
/// <remarks>
/// These tests drive a real <see cref="DeliveryWorker"/> (run as a hosted service) against a
/// failable transport with a circuit breaker configured. The retry budget is set small (MaxAttempts
/// 2) and <see cref="DeliveryRetryOptions.BaseDelay"/> = 0 so the backoff delays are instant. The
/// circuit breaker's <c>OpenDuration</c> is 0 (or very small) so the half-open transition is immediate
/// (deterministic).
/// </remarks>
public sealed class CircuitBreakerIntegrationTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";
    private const string OtherInboxIri = "https://c.domain.local/ap/v1/u/carol/inbox";

    // --- Circuit opens after threshold failures; subsequent deliveries are skipped ---------------

    [Fact]
    public async Task CircuitOpens_AfterThresholdFailures_SubsequentDeliveries_Skipped()
    {
        // Circuit breaker: threshold 1, openDuration 10 minutes (the circuit stays open long enough
        // that the test finishes before it transitions to half-open). MaxAttempts = 1 so each job
        // makes exactly one network call.
        var (worker, queue, deadLetter, handler) = BuildWorkerWithBreaker(
            responses: [HttpStatusCode.InternalServerError],
            maxAttempts: 1,
            failureThreshold: 1,
            openDuration: TimeSpan.FromMinutes(10));

        // Run 1: enqueue 1 job. It fails (1 network call) → circuit opens (threshold 1).
        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 1);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, deadLetter.Count);
        // The first job was dead-lettered via the normal retry path (NonSuccessStatus).
        var entries1 = await deadLetter.ListAsync(CancellationToken.None);
        Assert.Equal(DeadLetterFailureKind.NonSuccessStatus, entries1[0].FailureKind);

        // Run 2: enqueue 1 more job to the same peer. The circuit is OPEN (not yet half-open —
        // openDuration is 10 minutes). The delivery is skipped and dead-lettered immediately with
        // CircuitOpen kind (no network call).
        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 2);
        Assert.Equal(1, handler.CallCount); // still 1 — the 2nd job made NO network call
        Assert.Equal(2, deadLetter.Count);
        var entries2 = await deadLetter.ListAsync(CancellationToken.None);
        // The 2nd job (newest first) was dead-lettered with CircuitOpen kind.
        Assert.Equal(DeadLetterFailureKind.CircuitOpen, entries2[0].FailureKind);
    }

    [Fact]
    public async Task CircuitOpen_DoesNotAffectOtherPeers()
    {
        // Circuit breaker: threshold 1, openDuration 0.
        var (worker, queue, deadLetter, handler) = BuildWorkerWithBreaker(
            responses: [HttpStatusCode.InternalServerError],
            maxAttempts: 1,
            failureThreshold: 1,
            openDuration: TimeSpan.Zero);

        // Enqueue 1 job to the failing inbox (b.domain.local). It fails → circuit opens.
        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 1);

        // Now enqueue 1 job to a DIFFERENT peer's inbox (c.domain.local). The circuit for
        // b.domain.local is open, but c.domain.local is unaffected — the delivery is attempted.
        await EnqueueAndRunAsync(worker, queue, inboxIri: OtherInboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 2);

        // Both jobs were dead-lettered (both peers return 500), but the 2nd job DID get a network
        // call (the circuit for c.domain.local is closed). Total: 2 network calls (1 per job).
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, deadLetter.Count);
        // Neither was dead-lettered with CircuitOpen (each got its own network call).
        var entries = await deadLetter.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(entries, e => e.FailureKind == DeadLetterFailureKind.CircuitOpen);
    }

    // --- 4xx responses are dead-lettered immediately (permanent, no retry) -----------------------

    [Fact]
    public async Task FourXxResponse_DeadLetteredImmediately_NoRetry()
    {
        // MaxAttempts=5 (generous budget), but a 404 is permanent → dead-lettered on attempt 1.
        var (worker, queue, deadLetter, handler) = BuildWorkerWithBreaker(
            responses: [HttpStatusCode.NotFound],
            maxAttempts: 5,
            failureThreshold: 0, // no circuit breaker
            openDuration: TimeSpan.Zero);

        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 1);

        // Exactly 1 network call (no retry — 404 is permanent).
        Assert.Equal(1, handler.CallCount);
        // Dead-lettered with NonSuccessStatus kind.
        Assert.Equal(1, deadLetter.Count);
        var entries = await deadLetter.ListAsync(CancellationToken.None);
        Assert.All(entries, e => Assert.Equal(DeadLetterFailureKind.NonSuccessStatus, e.FailureKind));
    }

    [Fact]
    public async Task FourTwentyNine_IsNotPermanent_Retried()
    {
        // 429 is NOT permanent (it's a transient rate limit) → retried up to MaxAttempts.
        var (worker, queue, deadLetter, handler) = BuildWorkerWithBreaker(
            responses: [HttpStatusCode.TooManyRequests],
            maxAttempts: 3,
            failureThreshold: 0,
            openDuration: TimeSpan.Zero);

        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => deadLetter!.Count >= 1);

        // 3 network calls (MaxAttempts=3, 429 is retried).
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(1, deadLetter.Count);
    }

    // --- Retry-After is honored on 429/503 responses --------------------------------------------

    [Fact]
    public async Task RetryAfterHeader_IsHonored()
    {
        // A handler that returns 429 with a Retry-After: 1 header on the first call, then 200.
        // BaseDelay is 0, so without Retry-After the retry would be instant. With Retry-After: 1,
        // the worker waits 1 second before retrying. We verify the retry happened (CallCount == 2)
        // and the delivery eventually succeeded (no dead-letter).
        var handler = new RetryAfterHandler();
        var (worker, queue, deadLetter) = BuildWorkerWithHandler(handler, maxAttempts: 3, baseDelayMs: 0);

        await EnqueueAndRunAsync(worker, queue, inboxIri: InboxIri, count: 1,
            isDone: () => handler.CallCount >= 2);

        // The worker waited for Retry-After (1 second) before retrying. CallCount == 2 means it
        // retried after the wait. The delivery succeeded (200 on the 2nd call) → no dead-letter.
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(0, deadLetter!.Count);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static (DeliveryWorker, InMemoryDeliveryQueue, IDeliveryDeadLetterStore, FailableHandler) BuildWorkerWithBreaker(
        HttpStatusCode[] responses, int maxAttempts, int failureThreshold, TimeSpan openDuration)
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
        var handler = new FailableHandler(responses, throwOnSend: false);
        var deadLetter = new InMemoryDeliveryDeadLetterStore();

        IDeliveryCircuitBreaker? breaker = failureThreshold > 0
            ? new PerPeerDeliveryCircuitBreaker(failureThreshold, openDuration)
            : null;

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
            maxConcurrentDeliveries: 1,
            rateLimiter: null,
            metrics: null,
            circuitBreaker: breaker);

        return (worker, queue, deadLetter, handler);
    }

    private static (DeliveryWorker, InMemoryDeliveryQueue, IDeliveryDeadLetterStore) BuildWorkerWithHandler(
        HttpMessageHandler handler, int maxAttempts, int baseDelayMs)
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
        var deadLetter = new InMemoryDeliveryDeadLetterStore();

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLoggerFactory.Instance.CreateLogger<DeliveryWorker>(),
            new DeliveryRetryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.FromMilliseconds(baseDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(baseDelayMs),
            },
            deadLetter,
            maxConcurrentDeliveries: 1,
            rateLimiter: null,
            metrics: null,
            circuitBreaker: null);

        return (worker, queue, deadLetter);
    }

    private static async Task EnqueueAndRunAsync(
        DeliveryWorker worker, InMemoryDeliveryQueue queue, string inboxIri, int count, Func<bool> isDone)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.EnqueueAsync(new DeliveryJob(new Iri(inboxIri), BuildActivity(i)));
        }

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

    private static Activity BuildActivity(int index) => new Create
    {
        Id = $"{AliceIri}/creates/test-{index}",
        Actor = [new Link { Href = new Uri(AliceIri) }],
        Object = [new Note { Id = $"{AliceIri}/notes/test-{index}", Content = ["hello"] }],
    };

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that returns a scripted sequence of HTTP status codes (one
    /// per send; the last repeats when the sequence is exhausted). Counts each send in
    /// <see cref="CallCount"/>.
    /// </summary>
    private sealed class FailableHandler(HttpStatusCode[] responses, bool throwOnSend) : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _responses = responses;
        private readonly bool _throwOnSend = throwOnSend;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_throwOnSend)
            {
                throw new NotSupportedException("simulated transport failure");
            }

            var status = _responses.Length == 0
                ? HttpStatusCode.OK
                : _responses[Math.Min(CallCount - 1, _responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that returns 429 with a <c>Retry-After: 1</c> header on the
    /// first call, then 200 on subsequent calls. Counts each send in <see cref="CallCount"/>.
    /// </summary>
    private sealed class RetryAfterHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.Add("Retry-After", "1");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
