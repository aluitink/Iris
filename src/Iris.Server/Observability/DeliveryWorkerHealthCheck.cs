using Iris.Server.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Iris.Server.Observability;

/// <summary>
/// A <see cref="IHealthCheck"/> that reports whether the outbound delivery worker is running.
/// </summary>
/// <remarks>
/// The <see cref="DeliveryWorker"/> is registered as an untyped <c>IHostedService</c> (its constructor
/// is wired with explicit DI), so this check cannot resolve it by type. It instead locates the worker
/// instance among the host's <see cref="IHostedService"/> registrations (the single
/// <see cref="DeliveryWorker"/> in that collection) and reads its <see cref="DeliveryWorker.IsRunning"/>.
///
/// It reports <see cref="HealthStatus.Healthy"/> when the worker is configured (an instance actor) and
/// has begun pumping the delivery queue, <see cref="HealthStatus.Degraded"/> when it has not started yet
/// (the process is up but outbound delivery is not yet live — the readiness probe uses this to gate
/// traffic until the worker is operational), and <see cref="HealthStatus.Unhealthy"/> when the worker is
/// absent (it was not registered, so nothing will deliver outbound activity).
///
/// It is registered as an <see cref="IHealthCheck"/> singleton (not via <c>AddHealthChecks</c>'s
/// <c>AddCheck</c>) so the instance's <c>GET /ap/v1/health</c> endpoint resolves it through
/// <c>IEnumerable&lt;IHealthCheck&gt;</c> without requiring the host to call <c>UseHealthChecks</c>.
/// </remarks>
public sealed class DeliveryWorkerHealthCheck : IHealthCheck
{
    private readonly IEnumerable<IHostedService> _hostedServices;

    /// <summary>
    /// Initializes a new <see cref="DeliveryWorkerHealthCheck"/> over the host's hosted services.
    /// </summary>
    /// <param name="hostedServices">The registered <see cref="IHostedService"/> instances (the
    /// <see cref="DeliveryWorker"/> is one of them).</param>
    public DeliveryWorkerHealthCheck(IEnumerable<IHostedService> hostedServices)
    {
        _hostedServices = hostedServices ?? throw new ArgumentNullException(nameof(hostedServices));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var worker = _hostedServices.OfType<DeliveryWorker>().FirstOrDefault();

        if (worker is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The delivery worker is not registered; outbound activity will not be delivered."));
        }

        if (!worker.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "The delivery worker has not started yet; outbound delivery is not live."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("The delivery worker is running."));
    }
}
