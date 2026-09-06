using Iris.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Iris.Server.Observability;

/// <summary>
/// A <see cref="IHealthCheck"/> that verifies the instance's persistence is reachable by reading the
/// local actor record from the actor store.
/// </summary>
/// <remarks>
/// Unlike <see cref="InstanceHealthCheck"/> (which only inspects the in-memory options), this check
/// performs a real read against the registered <see cref="IActorStore"/> — the persistence seam a host
/// may back with a file- or database-backed <see cref="IPersistenceProvider"/>. When that backing store
/// is down or unreachable, the read throws and the check reports
/// <see cref="HealthStatus.Unhealthy"/>; the <c>GET /ap/v1/health</c> endpoint surfaces the fault and
/// returns <c>503</c>, so an orchestrator can evict the instance from rotation.
///
/// It probes the instance actor (<see cref="ActivityPubServerOptions.InstanceActorId"/>): if that actor
/// is not yet stored the check still reports healthy (the store answered; it simply has no such record),
/// because "no actor stored" is a configuration state, not a persistence fault. Only a read that
/// throws (the store is unreachable) is unhealthy.
///
/// It is registered as an <see cref="IHealthCheck"/> singleton (not via <c>AddHealthChecks</c>'s
/// <c>AddCheck</c>) so the instance's <c>GET /ap/v1/health</c> endpoint resolves it through
/// <c>IEnumerable&lt;IHealthCheck&gt;</c> without requiring the host to call <c>UseHealthChecks</c>.
/// </remarks>
public sealed class PersistenceHealthCheck : IHealthCheck
{
    private readonly IPersistenceProvider _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;

    /// <summary>
    /// Initializes a new <see cref="PersistenceHealthCheck"/> over the persistence provider.
    /// </summary>
    /// <param name="persistence">The persistence provider (the seam to probe; its <see cref="IPersistenceProvider.Actors"/>
    /// store is the real read target).</param>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    public PersistenceHealthCheck(
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> options)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var actorIri = _options.Value.InstanceActorId;

        try
        {
            // A null actor IRI is a configuration state (probed separately by InstanceHealthCheck), not a
            // persistence fault; treat it as "no actor to probe" rather than a store read.
            var actors = _persistence.Actors;
            var found = actorIri is { } instanceActor
                ? await actors.TryGetActorAsync(instanceActor, out _, ct).ConfigureAwait(false)
                : false;

            var data = new Dictionary<string, object> { ["actor_stored"] = found };
            return HealthCheckResult.Healthy("Persistence is reachable.", data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the request was cancelled — let the host handle it
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Persistence is unreachable.", ex);
        }
    }
}
