using Microsoft.Extensions.Diagnostics.HealthChecks;
using Iris.Server.Delivery;

namespace Iris.Server.Observability;

/// <summary>
/// A <see cref="IHealthCheck"/> that reports the outbound delivery queue's depth.
/// </summary>
/// <remarks>
/// A finite queue is healthy (the worker will drain it), so this check reports
/// <see cref="HealthStatus.Healthy"/> with the current pending-job count in
/// <see cref="HealthCheckResult.Data"/>. It reports <see cref="HealthStatus.Degraded"/> when the
/// pending count exceeds a warning threshold (a backlog is building up, usually because a peer is slow
/// or down), and <see cref="HealthStatus.Unhealthy"/> when it exceeds a critical threshold (the queue
/// is not keeping up and deliveries are piling up unboundedly). The thresholds are configurable via
/// <see cref="DeliveryQueueHealthOptions"/> and are disabled (treated as infinity) when left at zero.
///
/// It is registered as an <see cref="IHealthCheck"/> singleton so the instance's
/// <c>GET /ap/v1/health</c> endpoint resolves it through <c>IEnumerable&lt;IHealthCheck&gt;</c> without
/// requiring the host to call <c>UseHealthChecks</c>.
/// </remarks>
public sealed class DeliveryQueueHealthCheck : IHealthCheck
{
    private readonly IDeliveryQueue _queue;
    private readonly DeliveryQueueHealthOptions _options;

    /// <summary>
    /// Initializes a new <see cref="DeliveryQueueHealthCheck"/> over the delivery queue.
    /// </summary>
    /// <param name="queue">The outbound delivery queue to report on.</param>
    /// <param name="options">The warning/critical backlog thresholds.</param>
    public DeliveryQueueHealthCheck(IDeliveryQueue queue, DeliveryQueueHealthOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        int pending = _queue.Count;
        var data = new Dictionary<string, object> { ["pending"] = pending };

        if (pending > 0 && _options.CriticalPending > 0 && pending >= _options.CriticalPending)
        {
            return Task.FromResult(
                new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    $"The delivery queue has {pending} pending jobs (critical threshold: " +
                        $"{_options.CriticalPending}).",
                    data: data));
        }

        if (pending > 0 && _options.WarningPending > 0 && pending >= _options.WarningPending)
        {
            return Task.FromResult(
                new HealthCheckResult(
                    HealthStatus.Degraded,
                    $"The delivery queue has {pending} pending jobs (warning threshold: " +
                        $"{_options.WarningPending}).",
                    data: data));
        }

        return Task.FromResult(
            HealthCheckResult.Healthy(
                pending == 0 ? "The delivery queue is empty." : $"The delivery queue has {pending} pending job(s).",
                data));
    }
}
