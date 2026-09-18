using Iris.Client.Auth;
using Iris.Core;
using Iris.Server.Delivery;
using Iris.Server.Stores;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iris.Server.Observability;

/// <summary>
/// A <see cref="IHealthCheck"/> that reports the instance's federation observability: how many stored
/// actors have a resolvable signing identity (the key-store's resolvable-actor count) and the
/// delivery dead-letter count.
/// </summary>
/// <remarks>
/// The resolvable-actor count is the number of actors stored by this instance (local actors +
/// communities, per <see cref="IActorStore.ListActorsAsync"/>) whose signing identity resolves through
/// <see cref="IKeyProvider.TryGetIdentity(Iri, out IIdentity?)"/> — i.e. the actors the instance can
/// currently sign federation as. An actor without a registered key (a fresh community, or an actor
/// whose key has not been loaded yet) is counted as stored-but-not-resolvable, so the operator can
/// see a gap between "actors on disk" and "actors we can sign as."
///
/// The dead-letter count is the number of outbound deliveries that exhausted their retry budget and
/// were parked in the dead-letter store (see <see cref="IDeliveryDeadLetterStore"/>). A non-zero
/// count is a signal that some peers are unreachable or rejecting; it is reported as
/// <see cref="HealthStatus.Degraded"/> (the instance is otherwise healthy, but a backlog of failed
/// deliveries is accumulating) and <see cref="HealthStatus.Healthy"/> when zero.
///
/// Both figures are surfaced in <see cref="HealthCheckResult.Data"/> (<c>resolvable_actors</c>,
/// <c>stored_actors</c>, <c>dead_letters</c>) so an orchestrator or dashboard can scrape them from the
/// instance's <c>GET /ap/v1/health</c> endpoint.
///
/// It is registered as an <see cref="IHealthCheck"/> singleton (not via <c>AddHealthChecks</c>'s
/// <c>AddCheck</c>) so the endpoint resolves it through <c>IEnumerable&lt;IHealthCheck&gt;</c> without
/// requiring the host to call <c>UseHealthChecks</c>.
/// </remarks>
public sealed class InstanceObservabilityHealthCheck : IHealthCheck
{
    private readonly IPersistenceProvider _persistence;
    private readonly IKeyProvider _keyProvider;
    private readonly IDeliveryDeadLetterStore _deadLetters;

    /// <summary>
    /// Initializes a new <see cref="InstanceObservabilityHealthCheck"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (its <see cref="IPersistenceProvider.Actors"/>
    /// store lists the stored actors).</param>
    /// <param name="keyProvider">The key provider (resolves each actor's signing identity).</param>
    /// <param name="deadLetters">The delivery dead-letter store (the failed-delivery backlog).</param>
    public InstanceObservabilityHealthCheck(
        IPersistenceProvider persistence,
        IKeyProvider keyProvider,
        IDeliveryDeadLetterStore deadLetters)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        int stored = 0;
        int resolvable = 0;

        var actors = await _persistence.Actors.ListActorsAsync(ct).ConfigureAwait(false);
        stored = actors.Count;

        foreach (var actor in actors)
        {
            if (actor.Id is { Length: > 0 } id)
            {
                var actorIri = new Iri(id);
                if (_keyProvider.TryGetIdentity(actorIri, out _))
                {
                    resolvable++;
                }
            }
        }

        int deadLetters = _deadLetters.Count;
        var data = new Dictionary<string, object>
        {
            ["stored_actors"] = stored,
            ["resolvable_actors"] = resolvable,
            ["dead_letters"] = deadLetters,
        };

        if (deadLetters > 0)
        {
            return new HealthCheckResult(
                HealthStatus.Degraded,
                $"{deadLetters} outbound delivery(ies) dead-lettered; " +
                    $"{resolvable}/{stored} stored actor(s) have a resolvable signing identity.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            $"{resolvable}/{stored} stored actor(s) have a resolvable signing identity; no dead letters.",
            data);
    }
}
