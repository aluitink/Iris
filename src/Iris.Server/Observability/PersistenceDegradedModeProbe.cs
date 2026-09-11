using Iris.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Observability;

/// <summary>
/// A hosted service that probes the instance's persistence and flips the <see cref="IDegradedModeGate"/>
/// into degraded (read-only) mode when the store is unreachable.
/// </summary>
/// <remarks>
/// On host start the probe performs a real read against the actor store (the same seam a file/ or
/// database-backed <see cref="IPersistenceProvider"/> backs). If that read <em>throws</em> (the store is
/// down or unreachable — not merely empty), the probe:
/// <list type="number">
/// <item>logs a structured <c>degraded_mode_entered</c> event (with the failure detail), and</item>
/// <item>flips the <see cref="IDegradedModeGate"/> to degraded, so the write paths refuse mutations with
/// <c>503</c> instead of throwing.</item>
/// </list>
///
/// A healthy store (a read that succeeds, even when it returns no actors) leaves the instance read-write.
/// After startup the probe re-checks on a fixed interval (default 30s, configurable via the
/// <c>recheckInterval</c> constructor parameter): a read that now succeeds clears the degraded flag (the
/// instance <em>recovers</em> to read-write and logs a <c>degraded_mode_cleared</c> event), and a read
/// that fails re-enters it. This makes the degraded state recoverable without a restart.
///
/// The probe is intentionally separate from <see cref="PersistenceHealthCheck"/> (an
/// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck"/>): the health check is a
/// per-request, side-effect-free probe that an orchestrator's scrape drives; the degraded-mode probe is a
/// background service that <em>mutates</em> the gate. Both share the same read, but only the probe has the
/// side effect of flipping the gate.
/// </remarks>
public sealed class PersistenceDegradedModeProbe : BackgroundService
{
    private readonly IPersistenceProvider _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly IDegradedModeGate _gate;
    private readonly ILogger<PersistenceDegradedModeProbe> _logger;
    private readonly TimeSpan _recheckInterval;

    /// <summary>
    /// Initializes a new <see cref="PersistenceDegradedModeProbe"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (its <see cref="IPersistenceProvider.Actors"/>
    /// store is the read target).</param>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    /// <param name="gate">The degraded-mode gate to flip.</param>
    /// <param name="logger">A logger.</param>
    public PersistenceDegradedModeProbe(
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> options,
        IDegradedModeGate gate,
        ILogger<PersistenceDegradedModeProbe> logger)
        : this(persistence, options, gate, logger, TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>
    /// Initializes a new <see cref="PersistenceDegradedModeProbe"/> with a custom re-check interval.
    /// </summary>
    /// <param name="persistence">The persistence provider (its <see cref="IPersistenceProvider.Actors"/>
    /// store is the read target).</param>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceActorId"/>).</param>
    /// <param name="gate">The degraded-mode gate to flip.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="recheckInterval">How often to re-probe after startup (the recovery window).</param>
    public PersistenceDegradedModeProbe(
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> options,
        IDegradedModeGate gate,
        ILogger<PersistenceDegradedModeProbe> logger,
        TimeSpan recheckInterval)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recheckInterval = recheckInterval > TimeSpan.Zero ? recheckInterval : TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The startup probe runs before the re-check loop; a startup failure must not crash the host (the
        // instance still serves reads + reports Unhealthy via PersistenceHealthCheck).
        await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_recheckInterval, stoppingToken).ConfigureAwait(false);
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — expected
        }
    }

    /// <summary>
    /// Probes persistence once and flips the gate accordingly (the read the <see cref="ExecuteAsync"/>
    /// loop + the startup probe both perform). A throwing read enters degraded mode (when not already); a
    /// successful read clears it (when already degraded). Never throws — a probe failure is itself the
    /// degraded signal, not a reason to abort the host.
    /// </summary>
    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        try
        {
            var actorIri = _options.Value.InstanceActorId;
            var actors = _persistence.Actors;
            if (actorIri is { } instanceActor)
            {
                await actors.TryGetActorAsync(instanceActor, out _, ct).ConfigureAwait(false);
            }

            // A successful read (the store answered). If we were degraded, the store recovered.
            if (_gate.IsDegraded)
            {
                _gate.ClearDegraded();
                _logger.LogInformation(
                    "degraded_mode_cleared: persistence is reachable again; the instance returns to read-write.");
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — do not flip the gate on a cancelled probe
        }
        catch (Exception ex)
        {
            if (!_gate.IsDegraded)
            {
                _gate.MarkDegraded();
                _logger.LogWarning(
                    ex,
                    "degraded_mode_entered: persistence is unreachable; the instance is starting in (or returning to) degraded/read-only mode. Reads are served; writes are refused with 503.");
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "degraded_mode_still_unreachable: persistence read failed again; the instance remains degraded/read-only.");
            }
        }
    }
}
