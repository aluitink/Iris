using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Delivery;

/// <summary>
/// The background worker that pumps <see cref="DeliveryJob"/>s off the <see cref="IDeliveryQueue"/>
/// and delivers them — POSTing the serialized activity to the recipient's inbox, signed with the
/// instance actor's key.
/// </summary>
/// <remarks>
/// The worker is a <see cref="BackgroundService"/> (per the async conventions — background delivery
/// uses a <c>Channel</c> + hosted service, never <c>Task.Run</c>). It dequeues jobs until the queue is
/// complete and empty (signaled by <see cref="IDeliveryQueue.CompleteAsync"/>, called on host shutdown),
/// then stops.
/// </remarks>
/// <remarks>
/// <strong>Concurrency (Phase 16.1):</strong> the worker delivers jobs with bounded concurrency — at most
/// <see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/> deliveries in flight at once (a semaphore
/// admits one slot per delivery; a delivery releases its slot when it finishes). A value of 1 (the
/// default) preserves the original serial behavior; a higher value delivers a burst in parallel,
/// overlapping the per-delivery network round-trips, without opening more than that many concurrent
/// outbound connections. The dequeuer pulls a job and starts its delivery task outside the in-flight
/// bound, so a slow delivery can never stall the pump (which would serialize the worker and risk a
/// semaphore deadlock). Each in-flight delivery still honors the per-job retry / dead-letter policy
/// independently.
/// </remarks>
/// <remarks>
/// <strong>Signing:</strong> each delivery is signed as the local actor named on its
/// <see cref="DeliveryJob.ActorIri"/> (the actor performing the automated event — e.g. the local actor
/// being followed in a <c>Follow</c> → <c>Accept</c>). When a job carries no acting actor, the worker
/// falls back to <see cref="ActivityPubServerOptions.InstanceActorId"/> — the "system key for automated
/// events". The acting actor is communicated to the <see cref="SigningHandler"/> via the
/// <c>X-Iris-Actor</c> request header (which the handler treats as a per-request override of its default
/// <see cref="Iris.Client.Pipeline.SigningHandler.ActorId"/>); the handler resolves that actor's key from the
/// <see cref="Iris.Client.Auth.IKeyProvider"/>.
/// </remarks>
/// <remarks>
/// <strong>Failure policy (F-22 at-least-once delivery):</strong> a delivery that fails (network error
/// or a non-2xx response) is <em>retried</em> up to <see cref="DeliveryRetryOptions.MaxAttempts"/> total
/// attempts, with an exponentially-growing backoff between attempts (<see cref="DeliveryRetryOptions.BaseDelay"/>
/// doubled each retry, capped at <see cref="DeliveryRetryOptions.MaxDelay"/>) so a downed peer is not
/// hammered. When all attempts fail, the job is moved to the <see cref="IDeliveryDeadLetterStore"/>
/// (an operator can inspect and re-drive it) rather than dropped silently. A successful (2xx) delivery
/// is never retried. A retried delivery is harmless on the receiver: the inbox pipeline dedupes a
/// re-delivered activity by its <c>Id</c> (C-07). This follows the error-handling convention (log, don't
/// throw) — a delivery failure never crashes the worker.
/// </remarks>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IDeliveryQueue _queue;
    private readonly IActivityPubClientFactory _clientFactory;
    private readonly Func<HttpMessageHandler> _transportFactory;
    private readonly Iri? _instanceActorIri;
    private readonly DeliveryRetryOptions _retryOptions;
    private readonly IDeliveryDeadLetterStore? _deadLetter;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly int _maxConcurrentDeliveries;
    private readonly IDeliveryRateLimiter? _rateLimiter;
    private readonly Iris.Server.Observability.IrisDeliveryMetrics? _metrics;

    /// <summary>
    /// Initializes a new <see cref="DeliveryWorker"/>.
    /// </summary>
    /// <param name="queue">The delivery queue to pump. Must not be null.</param>
    /// <param name="clientFactory">The factory that builds the signed delivery client. Must not be null.</param>
    /// <param name="transportFactory">A factory for the outbound <see cref="HttpMessageHandler"/> transport.
    /// Must not be null. (A default is registered by <c>AddActivityPubServer</c>; a host or test may
    /// override it to route deliveries — e.g. to an in-process <c>TestServer</c>.)</param>
    /// <param name="options">The server options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When any required dependency is null.</exception>
    public DeliveryWorker(
        IDeliveryQueue queue,
        IActivityPubClientFactory clientFactory,
        Func<HttpMessageHandler> transportFactory,
        IOptions<ActivityPubServerOptions> options,
        ILogger<DeliveryWorker> logger)
        : this(queue, clientFactory, transportFactory, options, logger,
            new DeliveryRetryOptions(), null, 1, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="DeliveryWorker"/> with an explicit retry / dead-letter policy (F-22).
    /// </summary>
    /// <param name="queue">The delivery queue to pump. Must not be null.</param>
    /// <param name="clientFactory">The factory that builds the signed delivery client. Must not be null.</param>
    /// <param name="transportFactory">A factory for the outbound <see cref="HttpMessageHandler"/> transport.
    /// Must not be null.</param>
    /// <param name="options">The server options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <param name="retryOptions">The retry / dead-letter policy. Null uses the defaults
    /// (<see cref="DeliveryRetryOptions"/>: 5 attempts, 1s base, 60s cap).</param>
    /// <param name="deadLetter">The dead-letter store for exhausted jobs. Null disables dead-lettering
    /// (exhausted jobs are logged at <c>Error</c> and dropped).</param>
    /// <exception cref="ArgumentNullException">When any required dependency is null.</exception>
    public DeliveryWorker(
        IDeliveryQueue queue,
        IActivityPubClientFactory clientFactory,
        Func<HttpMessageHandler> transportFactory,
        IOptions<ActivityPubServerOptions> options,
        ILogger<DeliveryWorker> logger,
        DeliveryRetryOptions? retryOptions,
        IDeliveryDeadLetterStore? deadLetter)
        : this(queue, clientFactory, transportFactory, options, logger, retryOptions, deadLetter, 1, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="DeliveryWorker"/> with an explicit retry / dead-letter policy (F-22)
    /// and outbound-delivery concurrency (Phase 16.1).
    /// </summary>
    /// <param name="queue">The delivery queue to pump. Must not be null.</param>
    /// <param name="clientFactory">The factory that builds the signed delivery client. Must not be null.</param>
    /// <param name="transportFactory">A factory for the outbound <see cref="HttpMessageHandler"/> transport.
    /// Must not be null.</param>
    /// <param name="options">The server options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    /// <param name="logger">The logger. Must not be null.</param>
    /// <param name="retryOptions">The retry / dead-letter policy. Null uses the defaults
    /// (<see cref="DeliveryRetryOptions"/>: 5 attempts, 1s base, 60s cap).</param>
    /// <param name="deadLetter">The dead-letter store for exhausted jobs. Null disables dead-lettering
    /// (exhausted jobs are logged at <c>Error</c> and dropped).</param>
    /// <param name="maxConcurrentDeliveries">The maximum number of deliveries in flight at once. Must be
    /// at least 1 (1 = serial delivery; a higher value delivers a burst in parallel). A value below 1 is
    /// clamped to 1.</param>
    /// <param name="rateLimiter">The per-peer outbound-delivery rate limiter (Phase 16.3). Null disables
    /// rate limiting (the worker delivers as fast as the concurrency cap allows).</param>
    /// <param name="metrics">The delivery metrics (Phase 17.2). Null disables metric recording (the
    /// worker delivers exactly as before).</param>
    /// <exception cref="ArgumentNullException">When any required dependency is null.</exception>
    public DeliveryWorker(
        IDeliveryQueue queue,
        IActivityPubClientFactory clientFactory,
        Func<HttpMessageHandler> transportFactory,
        IOptions<ActivityPubServerOptions> options,
        ILogger<DeliveryWorker> logger,
        DeliveryRetryOptions? retryOptions,
        IDeliveryDeadLetterStore? deadLetter,
        int maxConcurrentDeliveries,
        IDeliveryRateLimiter? rateLimiter,
        Iris.Server.Observability.IrisDeliveryMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _clientFactory = clientFactory;
        _transportFactory = transportFactory;
        _instanceActorIri = options.Value.InstanceActorId;
        _retryOptions = retryOptions ?? new DeliveryRetryOptions();
        _deadLetter = deadLetter;
        _logger = logger;
        _maxConcurrentDeliveries = Math.Max(1, maxConcurrentDeliveries);
        _rateLimiter = rateLimiter;
        _metrics = metrics;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_instanceActorIri is not { } instanceActorIri)
        {
            _logger.LogWarning(
                "DeliveryWorker is not configured with an InstanceActorId; inbound deliveries will be " +
                "enqueued but never delivered. Set ActivityPubServerOptions.InstanceActorId to enable outbound delivery.");
            return;
        }

        // One signed client for the lifetime of the worker (signed as the instance actor).
        using var client = _clientFactory.Create(
            new ActivityPubClientOptions { ActorId = instanceActorIri, EnableRetry = true },
            _transportFactory());

        _logger.LogDebug(
            "DeliveryWorker started; delivering as {Actor} (up to {Concurrency} in flight)",
            instanceActorIri,
            _maxConcurrentDeliveries);

        // Phase 16.1: pump the queue with bounded concurrency. The single dequeuer pulls a job, acquires
        // one of MaxConcurrentDeliveries semaphore slots, and starts a delivery task for it; the delivery
        // task releases the slot when it finishes (delivered or dead-lettered). At most
        // MaxConcurrentDeliveries deliveries are therefore in flight at once, so a burst is delivered in
        // parallel (overlapping the per-delivery network round-trips) without the instance opening more
        // than that many concurrent outbound connections. The dequeue is NOT inside the semaphore-wait,
        // so a slow in-flight delivery can never stall the dequeuer (which would serialize the worker and
        // risk a semaphore deadlock). The worker stops once the queue is complete and empty AND every
        // in-flight delivery has finished (the drain loop).
        using var semaphore = new SemaphoreSlim(_maxConcurrentDeliveries);
        var inFlight = new ConcurrentDictionary<Task, byte>();

        while (true)
        {
            DeliveryJob? job;
            try
            {
                job = await _queue
                    .TryDequeueAsync(stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down — stop dequeuing (in-flight deliveries drain below)
            }

            if (job is null)
            {
                // Queue complete and empty. The worker may stop once the in-flight deliveries finish;
                // any delivery admitted before completion is still running.
                if (inFlight.IsEmpty)
                {
                    break;
                }

                // Wait for at least one in-flight delivery to finish, then re-check. A host shutdown
                // cancels stoppingToken, which the in-flight deliveries observe on their next await.
                await WaitForAnyInFlightAsync(inFlight).ConfigureAwait(false);
                continue;
            }

            // Admit a delivery slot before starting the task so at most MaxConcurrentDeliveries are in
            // flight. The job was already dequeued, so this wait only bounds concurrency.
            await semaphore
                .WaitAsync(stoppingToken)
                .ConfigureAwait(false);

            var deliveryTask = DeliverTrackedAsync(client, semaphore, job, stoppingToken);
            inFlight[deliveryTask] = 0;
        }

        // Drain: wait for the in-flight deliveries admitted before the break to finish. A host shutdown
        // cancels stoppingToken, so the remaining deliveries observe it and finish promptly.
        while (!inFlight.IsEmpty && !stoppingToken.IsCancellationRequested)
        {
            await WaitForAnyInFlightAsync(inFlight).ConfigureAwait(false);
        }

        _logger.LogDebug("DeliveryWorker stopped (queue complete and drained).");
    }

    /// <summary>
    /// Starts <see cref="DeliverOneAsync"/> for <paramref name="job"/> and — on completion — releases its
    /// semaphore slot. A delivery failure never propagates (the F-22 retry / dead-letter policy inside
    /// <see cref="DeliverOneAsync"/> swallows it); the <c>finally</c> guarantees the semaphore slot is
    /// released even on an unexpected fault. The returned task is tracked by the caller in the in-flight
    /// map and removed there when it completes (via <see cref="WaitForAnyInFlightAsync"/>).
    /// </summary>
    private async Task DeliverTrackedAsync(
        IActivityPubClient client,
        SemaphoreSlim semaphore,
        DeliveryJob job,
        CancellationToken ct)
    {
        try
        {
            // Phase 16.3: before sending, wait until the per-peer rate limiter permits a delivery to this
            // job's inbox host. The wait happens while holding a concurrency slot (acquired by the caller
            // before starting this task), so a rate-limited peer's blocking wait never stalls the single
            // dequeuer — other peers' deliveries still flow in parallel. A disabled limiter (null or
            // maxRequests == 0) returns immediately.
            if (_rateLimiter is { } limiter)
            {
                await limiter.WaitUntilPermittedAsync(job.InboxIri, ct).ConfigureAwait(false);
            }

            await DeliverOneAsync(client, job, ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Waits until at least one in-flight delivery task has completed (removing it from
    /// <paramref name="inFlight"/>), or the set is empty (returning immediately). Used by the pump to
    /// wait for the drain without spinning. A host shutdown is handled by the caller, which stops
    /// looping (and the in-flight deliveries themselves observe the cancellation on their next await);
    /// this wait therefore has no cancellation of its own — it only completes when a delivery finishes.
    /// </summary>
    private static async Task WaitForAnyInFlightAsync(ConcurrentDictionary<Task, byte> inFlight)
    {
        if (inFlight.IsEmpty)
        {
            return;
        }

        // Task.WhenAny over a snapshot: a delivery completing removes itself from the map, so a later
        // round re-snapshots the (possibly smaller) set.
        var completed = await Task.WhenAny(inFlight.Keys.ToArray()).ConfigureAwait(false);
        // Remove the completed task so the next round waits only on the remaining in-flight deliveries.
        inFlight.TryRemove(completed, out _);
    }

    /// <summary>
    /// Delivers a job with the F-22 retry / dead-letter policy: up to
    /// <see cref="DeliveryRetryOptions.MaxAttempts"/> total attempts with an exponentially-growing
    /// backoff between attempts; on a 2xx the delivery is done, and when the budget is exhausted the job
    /// is moved to the dead-letter store (or logged at <c>Error</c> when no store is configured). A
    /// delivery failure never throws out of the worker.
    /// </summary>
    private async Task DeliverOneAsync(IActivityPubClient client, DeliveryJob job, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _retryOptions.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            DeadLetterFailureKind kind;
            string? detail;

            var activityType = job.Activity.GetType().Name;
            try
            {
                var statusCode = await DeliverAsAsync(client, job, ct).ConfigureAwait(false);
                if (statusCode is >= 200 and < 300)
                {
                    _metrics?.RecordDelivered(activityType);
                    _logger.LogDebug(
                        "Delivered activity {ActivityId} to {Inbox} ({Status}) on attempt {Attempt}",
                        job.Activity.Id,
                        job.InboxIri,
                        statusCode,
                        attempt);
                    return; // success — no retry
                }

                _metrics?.RecordAttemptFailed(activityType, DeadLetterFailureKind.NonSuccessStatus);
                _logger.LogWarning(
                    "Delivery of activity {ActivityId} to {Inbox} returned non-2xx status {Status} on attempt {Attempt}/{MaxAttempts}",
                    job.Activity.Id,
                    job.InboxIri,
                    statusCode,
                    attempt,
                    maxAttempts);
                kind = DeadLetterFailureKind.NonSuccessStatus;
                detail = statusCode.ToString();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutting down — let the worker stop (do not retry / dead-letter on cancellation)
            }
            catch (Exception ex)
            {
                _metrics?.RecordAttemptFailed(activityType, DeadLetterFailureKind.TransportError);
                // Log, don't throw: a transport failure is retryable. Never crash the worker over one bad delivery.
                _logger.LogWarning(
                    ex,
                    "Delivery of activity {ActivityId} to {Inbox} failed on attempt {Attempt}/{MaxAttempts}",
                    job.Activity.Id,
                    job.InboxIri,
                    attempt,
                    maxAttempts);
                kind = DeadLetterFailureKind.TransportError;
                detail = ex.Message;
            }

            // This attempt failed. If there are retries left, back off (exponential, capped) and retry;
            // the backoff is observed via a cancellable delay so a host shutdown interrupts it promptly.
            if (attempt < maxAttempts)
            {
                var delay = BackoffDelay(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            else
            {
                // Budget exhausted: dead-letter the job (or log at Error when no store is configured).
                await DeadLetterAsync(job, kind, detail, maxAttempts, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The backoff delay before retry <paramref name="attempt"/> (1-based): <see
    /// cref="DeliveryRetryOptions.BaseDelay"/> doubled <c>(attempt − 1)</c> times, capped at
    /// <see cref="DeliveryRetryOptions.MaxDelay"/> (exponential backoff; a downed peer is not hammered).
    /// </summary>
    private TimeSpan BackoffDelay(int attempt)
    {
        var baseTicks = _retryOptions.BaseDelay.Ticks;
        var capTicks = _retryOptions.MaxDelay.Ticks;
        if (baseTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        // BaseDelay * 2^(attempt-1), saturating at MaxDelay (avoid overflow by capping the shift).
        var delayTicks = baseTicks;
        for (var i = 1; i < attempt && delayTicks < capTicks; i++)
        {
            delayTicks = delayTicks > capTicks / 2 ? capTicks : delayTicks * 2;
        }

        return delayTicks > capTicks ? _retryOptions.MaxDelay : TimeSpan.FromTicks(delayTicks);
    }

    /// <summary>
    /// Moves an exhausted job to the dead-letter store (F-22) and logs it; when no store is configured,
    /// logs at <c>Error</c> (the job is dropped, preserving the pre-F-22 behavior for hosts that opt out).
    /// </summary>
    private async Task DeadLetterAsync(
        DeliveryJob job, DeadLetterFailureKind kind, string? detail, int attempts, CancellationToken ct)
    {
        var activityType = job.Activity.GetType().Name;
        if (_deadLetter is not { } store)
        {
            _metrics?.RecordDeadLettered(activityType, kind);
            _logger.LogError(
                "Delivery of activity {ActivityId} to {Inbox} exhausted {Attempts} attempts ({Kind}: {Detail}); no dead-letter store configured, dropping.",
                job.Activity.Id,
                job.InboxIri,
                attempts,
                kind,
                detail);
            return;
        }

        var entry = new DeadLetterEntry(
            job.InboxIri,
            job.Activity,
            job.ActorIri,
            attempts,
            kind,
            detail,
            DateTimeOffset.UtcNow);
        await store.AddAsync(entry, ct).ConfigureAwait(false);
        _metrics?.RecordDeadLettered(activityType, kind);
        _logger.LogError(
            "Delivery of activity {ActivityId} to {Inbox} exhausted {Attempts} attempts ({Kind}: {Detail}); dead-lettered.",
            job.Activity.Id,
            job.InboxIri,
            attempts,
            kind,
            detail);
    }

    /// <summary>
    /// POSTs a job's activity to its inbox, signed as the job's acting actor. The acting actor is
    /// communicated via the <c>X-Iris-Actor</c> header, which the <see cref="SigningHandler"/> resolves
    /// to the actor's key (overriding the client's default instance-actor identity); when the job has
    /// no acting actor the header is omitted and the client signs as the instance actor.
    /// </summary>
    private static async Task<int> DeliverAsAsync(IActivityPubClient client, DeliveryJob job, CancellationToken ct)
    {
        var json = ActivityJson.Serialize(job.Activity);
        var body = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, job.InboxIri.Value)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);

        if (job.ActorIri is { } actorIri)
        {
            request.Headers.Add("X-Iris-Actor", actorIri.Value);
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }
}
