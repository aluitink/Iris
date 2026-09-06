namespace Iris.Server.Observability;

/// <summary>
/// Decides whether the instance is ready to receive traffic.
/// </summary>
/// <remarks>
/// Readiness is distinct from liveness (the <c>GET /ap/v1/health</c> endpoint): an instance may be up
/// (the process is alive) but not yet ready (it has not finished loading the key material it needs to
/// sign and serve). The <c>GET /ap/v1/ready</c> probe reports
/// <see cref="IsReadyAsync"/> so a load balancer or orchestrator can gate traffic until the instance is
/// prepared. The default implementation (<see cref="DefaultReadinessGate"/>) is ready once the instance
/// actor's signing key is registered and resolvable.
/// </remarks>
public interface IReadinessGate
{
    /// <summary>
    /// Gets a value indicating whether the instance is ready to receive traffic.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when ready; otherwise
    /// <see langword="false"/>.</returns>
    public Task<bool> IsReadyAsync(CancellationToken ct = default);
}
