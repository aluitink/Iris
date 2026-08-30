using Iris.Server.Delivery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Observability;

/// <summary>
/// A hosted service that completes the outbound <see cref="IDeliveryQueue"/> on host shutdown.
/// </summary>
/// <remarks>
/// Marking the queue complete (via <see cref="IDeliveryQueue.CompleteAsync"/>) signals that no further
/// jobs will be enqueued; once it drains, <see cref="IDeliveryQueue.TryDequeueAsync"/> returns
/// <c>null</c> and the <see cref="DeliveryWorker"/> stops. Without this, the queue would remain "open"
/// after the host stops, and a stray enqueue (or a re-resolved queue) could accept jobs with no worker to
/// deliver them.
///
/// The host calls hosted services' <see cref="IHostedService.StopAsync"/> in registration order,
/// sequentially. This service is registered <em>before</em> the <see cref="DeliveryWorker"/> (see
/// <c>AddActivityPubServer</c>), so its <see cref="StopAsync"/> — which completes the queue — runs before
/// the worker's <see cref="BackgroundService.StopAsync"/> (which cancels the worker's stopping token and
/// awaits <see cref="BackgroundService.ExecuteAsync"/>). Completing the queue first means the worker's
/// dequeue loop observes a complete-and-empty queue and exits cleanly (draining in-flight deliveries)
/// rather than blocking on an open channel. The service holds no state beyond its resolved references.
/// </remarks>
public sealed class DeliveryQueueShutdownService : IHostedService
{
    private readonly IDeliveryQueue _queue;
    private readonly ILogger<DeliveryQueueShutdownService> _logger;

    /// <summary>
    /// Initializes a new <see cref="DeliveryQueueShutdownService"/>.
    /// </summary>
    /// <param name="queue">The outbound delivery queue to complete on shutdown.</param>
    /// <param name="logger">A logger.</param>
    public DeliveryQueueShutdownService(IDeliveryQueue queue, ILogger<DeliveryQueueShutdownService> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Host stopping: completing the delivery queue so the worker drains in-flight deliveries.");

        try
        {
            return _queue.CompleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // CompleteAsync failing is not fatal to shutdown (the worker still stops via its own token);
            // log and continue so the host's shutdown pipeline is not interrupted.
            _logger.LogError(
                ex, "Failed to complete the delivery queue on shutdown; in-flight work may not drain.");
            return Task.CompletedTask;
        }
    }
}
