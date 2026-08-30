namespace Iris.Server.Observability;

/// <summary>
/// Backlog thresholds for <see cref="DeliveryQueueHealthCheck"/>.
/// </summary>
/// <remarks>
/// Both thresholds are disabled (treated as infinity) when left at zero, which is the default — a host
/// that wants to alert on a growing delivery backlog rebinds these options with positive values. The
/// critical threshold, when set, must be at least the warning threshold (a value that would make the
/// check report "degraded" only at a count above the "unhealthy" count is meaningless).
/// </remarks>
public sealed class DeliveryQueueHealthOptions
{
    /// <summary>
    /// The pending-job count at or above which the queue is reported <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded"/>.
    /// </summary>
    /// <value>Zero (the default) disables the warning threshold.</value>
    public int WarningPending { get; set; }

    /// <summary>
    /// The pending-job count at or above which the queue is reported <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy"/>.
    /// </summary>
    /// <value>Zero (the default) disables the critical threshold.</value>
    public int CriticalPending { get; set; }
}
