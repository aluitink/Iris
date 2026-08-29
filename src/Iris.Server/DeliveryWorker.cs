using System.Net.Http;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server;

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
            new DeliveryRetryOptions(), null)
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

        _logger.LogDebug("DeliveryWorker started; delivering as {Actor}", instanceActorIri);

        while (await _queue
            .TryDequeueAsync(stoppingToken)
            .ConfigureAwait(false) is { } job)
        {
            await DeliverOneAsync(client, job, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogDebug("DeliveryWorker stopped (queue complete and drained).");
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

            try
            {
                var statusCode = await DeliverAsAsync(client, job, ct).ConfigureAwait(false);
                if (statusCode is >= 200 and < 300)
                {
                    _logger.LogDebug(
                        "Delivered activity {ActivityId} to {Inbox} ({Status}) on attempt {Attempt}",
                        job.Activity.Id,
                        job.InboxIri,
                        statusCode,
                        attempt);
                    return; // success — no retry
                }

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
        if (_deadLetter is not { } store)
        {
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
