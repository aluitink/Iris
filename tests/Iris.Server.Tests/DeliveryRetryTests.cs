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

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.12 unit tests for the F-22 delivery retry / dead-letter policy on the
/// <see cref="DeliveryWorker"/>: a failed delivery is retried up to <see cref="DeliveryRetryOptions.MaxAttempts"/>
/// total attempts (exponential backoff between them), and a job that exhausts its budget is moved to the
/// <see cref="IDeliveryDeadLetterStore"/> (not dropped silently). A successful (2xx) delivery is never
/// retried.
/// </summary>
/// <remarks>
/// These tests drive a real <see cref="DeliveryWorker"/> (run as a hosted service) against a
/// <em>failable</em> transport: a stub <see cref="HttpMessageHandler"/> that returns a configured
/// sequence of responses (e.g. 500, 500, 200) so the retry path is exercised deterministically. The
/// retry budget is set small (MaxAttempts 2–3) and <see cref="DeliveryRetryOptions.BaseDelay"/> = 0 so
/// the backoff delays are instant (no real waiting in the test).
/// </remarks>
public sealed class DeliveryRetryTests
{
    private const string AliceIri = "https://a.domain.local/ap/v1/u/alice";
    private const string InboxIri = "https://b.domain.local/ap/v1/u/bob/inbox";

    // --- A successful delivery is delivered on the first attempt (no retry) -------------

    [Fact]
    public async Task SuccessfulDelivery_IsDeliveredOnFirstAttempt_NoRetry_NoDeadLetter()
    {
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [HttpStatusCode.OK], maxAttempts: 5);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 1);

        Assert.Equal(1, handler.CallCount); // exactly one attempt (no retry)
        Assert.Equal(0, deadLetter!.Count);
        Assert.Equal(0, queue.Count);
    }

    // --- A transient failure is retried and eventually succeeds -------------------------

    [Fact]
    public async Task TransientFailure_IsRetried_UntilSuccess_NotDeadLettered()
    {
        // 500, 500, then 200: the first two attempts fail, the third succeeds. With MaxAttempts=5 the
        // delivery is eventually delivered and NOT dead-lettered.
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK],
            maxAttempts: 5);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 3);

        Assert.Equal(3, handler.CallCount); // 2 failures + 1 success
        Assert.Equal(0, deadLetter!.Count); // succeeded, so not dead-lettered
        Assert.Equal(0, queue.Count); // drained
    }

    // --- A permanent failure exhausts the budget and is dead-lettered --------------------

    [Fact]
    public async Task PermanentFailure_ExhaustsBudget_IsDeadLettered()
    {
        // Always 500, MaxAttempts=3: all 3 attempts fail, the job is dead-lettered with the attempt
        // count (3) and the failure kind (NonSuccessStatus) + the last status (500).
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest],
            maxAttempts: 3);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 3 && deadLetter!.Count == 1);

        Assert.Equal(3, handler.CallCount); // exactly MaxAttempts attempts
        Assert.Equal(1, deadLetter!.Count);

        var entry = (await deadLetter!.ListAsync()).Single();
        Assert.Equal(3, entry.Attempts);
        Assert.Equal(DeadLetterFailureKind.NonSuccessStatus, entry.FailureKind);
        Assert.Equal("400", entry.FailureDetail);
        Assert.Equal(InboxIri, entry.InboxIri.Value);
        Assert.Equal(0, queue.Count); // the job left the queue (dead-lettered, not re-queued forever)
    }

    // --- A transport error (exception) is dead-lettered with kind TransportError ---------

    [Fact]
    public async Task TransportError_ExhaustsBudget_IsDeadLettered_AsTransportError()
    {
        // The handler throws on every attempt (a network failure), MaxAttempts=2.
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [], // no scripted responses
            maxAttempts: 2,
            throwOnSend: true);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 2 && deadLetter!.Count == 1);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, deadLetter!.Count);

        var entry = (await deadLetter!.ListAsync()).Single();
        Assert.Equal(2, entry.Attempts);
        Assert.Equal(DeadLetterFailureKind.TransportError, entry.FailureKind);
        Assert.NotNull(entry.FailureDetail);
    }

    // --- MaxAttempts=1 is fail-fast (no retry) but still dead-letters --------------------

    [Fact]
    public async Task MaxAttemptsOne_IsFailFast_NoRetry_ButDeadLetters()
    {
        // Always 500, MaxAttempts=1: one attempt, then dead-lettered (no retry).
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [HttpStatusCode.BadRequest],
            maxAttempts: 1);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 1 && deadLetter!.Count == 1);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, deadLetter!.Count);
        Assert.Equal(1, (await deadLetter!.ListAsync()).Single().Attempts);
    }

    // --- Without a dead-letter store, an exhausted job is dropped (pre-F-22 behavior) ----

    [Fact]
    public async Task WithoutDeadLetterStore_ExhaustedJob_IsDropped_NotCrash()
    {
        // Always 500, MaxAttempts=2, but the worker is built with NO dead-letter store (the 5-arg
        // constructor path). The job is dropped after the budget (logged at Error), the worker does not
        // crash, and the queue drains.
        var (worker, queue, deadLetter, handler) = BuildWorker(
            responses: [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest],
            maxAttempts: 2,
            noDeadLetter: true);

        await EnqueueAndRunAsync(worker, queue, isDone: () => handler.CallCount == 2);

        Assert.Equal(2, handler.CallCount);
        Assert.Null(deadLetter); // no store was configured (the worker dead-letters nothing)
        Assert.Equal(0, queue.Count); // drained (the job was dropped, not re-queued)
    }

    // --- Dead-letter store bounds: the oldest entry is evicted beyond capacity -----------

    [Fact]
    public async Task DeadLetterStore_EvictsOldest_BeyondCapacity()
    {
        var store = new InMemoryDeliveryDeadLetterStore(capacity: 2);

        await store.AddAsync(MakeEntry("inbox-1", attempts: 1));
        await store.AddAsync(MakeEntry("inbox-2", attempts: 1));
        await store.AddAsync(MakeEntry("inbox-3", attempts: 1));

        // The oldest (inbox-1) was evicted; inbox-2 and inbox-3 remain, newest-first.
        var entries = await store.ListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("inbox-3", entries[0].InboxIri.Value);
        Assert.Equal("inbox-2", entries[1].InboxIri.Value);
    }

    // --- Backoff delay is exponential (unit test of the worker's policy) -----------------

    [Fact]
    public async Task BackoffDelay_GrowsExponentially_AndIsCapped()
    {
        // Drive the worker's BackoffDelay via reflection (it is private): BaseDelay=100ms, MaxDelay=500ms.
        // attempt 1 -> 100ms, attempt 2 -> 200ms, attempt 3 -> 400ms, attempt 4 -> 500ms (capped).
        var (worker, _, _, _) = BuildWorker(responses: [HttpStatusCode.OK], maxAttempts: 5, baseDelayMs: 100, maxDelayMs: 500);

        var backoffMethod = typeof(DeliveryWorker).GetMethod(
            "BackoffDelay",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.Equal(TimeSpan.FromMilliseconds(100), (TimeSpan)backoffMethod.Invoke(worker, [1])!);
        Assert.Equal(TimeSpan.FromMilliseconds(200), (TimeSpan)backoffMethod.Invoke(worker, [2])!);
        Assert.Equal(TimeSpan.FromMilliseconds(400), (TimeSpan)backoffMethod.Invoke(worker, [3])!);
        // 800ms would exceed the 500ms cap, so it saturates at MaxDelay.
        Assert.Equal(TimeSpan.FromMilliseconds(500), (TimeSpan)backoffMethod.Invoke(worker, [4])!);
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Enqueues a job, runs the worker as a hosted service, waits until <paramref name="isDone"/>
    /// reports the job has been fully processed (delivered or dead-lettered — not merely dequeued),
    /// then stops the host. Waiting on an outcome (not just <c>queue.Count == 0</c>) avoids a race where
    /// the queue is drained the moment the job is <em>dequeued</em> (before the worker finishes
    /// delivering / dead-lettering it).
    /// </summary>
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

    private static (DeliveryWorker, InMemoryDeliveryQueue, IDeliveryDeadLetterStore?, FailableHandler) BuildWorker(
        HttpStatusCode[] responses, int maxAttempts, bool throwOnSend = false,
        IDeliveryDeadLetterStore? deadLetter = null, bool noDeadLetter = false,
        int baseDelayMs = 0, int maxDelayMs = 0)
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
        var handler = new FailableHandler(responses, throwOnSend);
        // The store the worker is actually given: a fresh one (or the caller's) unless the caller asks
        // for NO store (noDeadLetter), in which case the worker dead-letters nothing (pre-F-22 behavior).
        IDeliveryDeadLetterStore? workerDeadLetter = noDeadLetter ? null : (deadLetter ?? new InMemoryDeliveryDeadLetterStore());

        var worker = new DeliveryWorker(
            queue, factory, () => handler, options,
            NullLoggerFactory.Instance.CreateLogger<DeliveryWorker>(),
            new DeliveryRetryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.FromMilliseconds(baseDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(maxDelayMs),
            },
            workerDeadLetter);

        return (worker, queue, workerDeadLetter, handler);
    }

    private static Activity BuildActivity() => new Create
    {
        Id = $"{AliceIri}/creates/test",
        Actor = [new Link { Href = new Uri(AliceIri) }],
        Object = [new Note { Id = $"{AliceIri}/notes/test", Content = ["hello"] }],
    };

    private static DeadLetterEntry MakeEntry(string inbox, int attempts) => new(
        new Iri(inbox),
        BuildActivity(),
        null,
        attempts,
        DeadLetterFailureKind.NonSuccessStatus,
        "500",
        DateTimeOffset.UtcNow);

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that returns a scripted sequence of HTTP status codes (one
    /// per send; the last repeats when the sequence is exhausted) — or throws a
    /// <see cref="NotSupportedException"/> on every send when <c>throwOnSend</c> is set (a transport
    /// failure). Counts each send in <see cref="CallCount"/>.
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
}
