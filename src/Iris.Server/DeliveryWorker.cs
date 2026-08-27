using System.Net.Http;
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
/// <strong>Signing:</strong> deliveries are signed as <see cref="ActivityPubServerOptions.InstanceActorId"/>
/// — the local actor performing the automated event (an <c>Accept</c> to a follow, a <c>Create</c>, etc.).
/// This is the "system key for automated events" the roadmap calls for; per-actor key selection (signing
/// a delivery as a specific local actor rather than the instance actor) is a follow-up.
/// </remarks>
/// <remarks>
/// <strong>Failure policy:</strong> a delivery that fails (network error, non-2xx) is logged at
/// <c>Warning</c> and dropped — it is not re-queued (the activity remains in the recipient's-perspective
/// store on the sender, and a production host may layer retry/dead-letter on top of the queue). This
/// follows the error-handling convention: log, don't throw, for recoverable delivery failures.
/// </remarks>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IDeliveryQueue _queue;
    private readonly IActivityPubClientFactory _clientFactory;
    private readonly Func<HttpMessageHandler> _transportFactory;
    private readonly Iri? _instanceActorIri;
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

    private async Task DeliverOneAsync(IActivityPubClient client, DeliveryJob job, CancellationToken ct)
    {
        try
        {
            var statusCode = await client
                .DeliverAsync(job.InboxIri, job.Activity, ct)
                .ConfigureAwait(false);

            if (statusCode is >= 200 and < 300)
            {
                _logger.LogDebug(
                    "Delivered activity {ActivityId} to {Inbox} ({Status})",
                    job.Activity.Id,
                    job.InboxIri,
                    statusCode);
            }
            else
            {
                _logger.LogWarning(
                    "Delivery of activity {ActivityId} to {Inbox} returned non-2xx status {Status}",
                    job.Activity.Id,
                    job.InboxIri,
                    statusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log, don't throw: a delivery failure is recoverable (the activity is stored; the remote
            // can be retried by a production host). Never crash the worker over one bad delivery.
            _logger.LogWarning(
                ex,
                "Delivery of activity {ActivityId} to {Inbox} failed",
                job.Activity.Id,
                job.InboxIri);
        }
    }
}
