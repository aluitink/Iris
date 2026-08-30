using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Iris.Server.Observability;

/// <summary>
/// A liveness <see cref="IHealthCheck"/> for an Iris instance.
/// </summary>
/// <remarks>
/// Iris has no mandatory external dependency by default (the in-memory persistence provider is the
/// default, and there is no database), so a running instance is healthy. This check reports
/// <see cref="HealthStatus.Healthy"/> when the instance's options are configured (a <see cref="IOptions{TOptions}"/>
/// with a non-null <see cref="ActivityPubServerOptions.InstanceActorId"/>), and
/// <see cref="HealthStatus.Unhealthy"/> otherwise. It also surfaces the instance name and the local
/// actor IRI in the result's <see cref="HealthCheckResult.Data"/> so an operator can confirm which
/// instance answered.
///
/// It is registered as an <see cref="IHealthCheck"/> singleton (not via <c>AddHealthChecks</c>'s
/// <c>AddCheck</c>) so the instance's <c>GET /ap/v1/health</c> endpoint resolves it through
/// <c>IEnumerable&lt;IHealthCheck&gt;</c> without requiring the host to call <c>UseHealthChecks</c>.
/// </remarks>
public sealed class InstanceHealthCheck : IHealthCheck
{
    private readonly IOptions<ActivityPubServerOptions> _options;

    /// <summary>
    /// Initializes a new <see cref="InstanceHealthCheck"/> over the instance's
    /// <see cref="ActivityPubServerOptions"/>.
    /// </summary>
    /// <param name="options">The instance's options (the registered singleton).</param>
    public InstanceHealthCheck(IOptions<ActivityPubServerOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (opts.InstanceActorId is not { } instanceActor)
        {
            return Task.FromResult(
                new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    "The instance has no actor configured; it cannot participate in federation."));
        }

        var data = new Dictionary<string, object>
        {
            ["instance"] = opts.InstanceName ?? "(unnamed)",
            ["actor"] = instanceActor.ToString(),
        };

        return Task.FromResult(HealthCheckResult.Healthy("Instance is up.", data));
    }
}
