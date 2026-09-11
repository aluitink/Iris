using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Bootstrap;

/// <summary>
/// A hosted service that acquires the <see cref="SingleInstanceLock"/> at startup and releases it at
/// shutdown (Phase 84.5).
/// </summary>
/// <remarks>
/// When <see cref="ActivityPubServerOptions.InstanceLockPath"/> is configured, this service is registered
/// by <c>AddActivityPubServer</c>. On <see cref="IHostedService.StartAsync"/> it calls
/// <see cref="SingleInstanceLock.AcquireAsync"/>: a lock held by a <em>live</em> different instance makes
/// <see cref="StartAsync"/> throw, which fails host startup (fail fast — a second instance on the same
/// persistence is a configuration error, not a recoverable state). On <see cref="IHostedService.StopAsync"/>
/// it disposes the lock (deletes the lock file).
///
/// When no lock path is configured (the default — every test harness and the in-memory/file-backed
/// deployments), this service is not registered and the guard is inert: existing multi-host test setups
/// and single-process deployments are unaffected.
/// </remarks>
public sealed class SingleInstanceGuardHostedService : IHostedService
{
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly ILogger<SingleInstanceGuardHostedService> _logger;
    private SingleInstanceLock? _lock;
    private Task? _started;
    private readonly TaskCompletionSource _startedTcs = new();

    /// <summary>
    /// Initializes a new <see cref="SingleInstanceGuardHostedService"/>.
    /// </summary>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceLockPath"/>).</param>
    /// <param name="logger">A logger.</param>
    public SingleInstanceGuardHostedService(
        IOptions<ActivityPubServerOptions> options,
        ILogger<SingleInstanceGuardHostedService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var lockPath = _options.Value.InstanceLockPath;
        if (string.IsNullOrWhiteSpace(lockPath))
        {
            // No lock path configured — the guard is inert (this service should not have been registered,
            // but be defensive: a host that registered it without configuring the path gets no-op).
            _started = Task.CompletedTask;
            _startedTcs.SetResult();
            return _started;
        }

        var hostIdentifier = Environment.MachineName;
        _started = AcquireAsync(lockPath, hostIdentifier, cancellationToken);
        return _started;
    }

    private async Task AcquireAsync(string lockPath, string hostIdentifier, CancellationToken ct)
    {
        try
        {
            _lock = await SingleInstanceLock.AcquireAsync(lockPath, hostIdentifier, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "single_instance_lock_acquired: pid {Pid} holds the single-instance lock at {LockPath}.",
                _lock.OwnerPid,
                lockPath);
        }
        catch (InstanceAlreadyRunningException)
        {
            // Propagate — host startup fails fast (the operator must stop the other instance or change
            // this instance's persistence/lock path).
            throw;
        }
        finally
        {
            _startedTcs.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Wait for the startup acquisition to settle (success or failure) before disposing, so we never
        // dispose a lock that is still being acquired.
        await _startedTcs.Task.ConfigureAwait(false);
        if (_lock is { } acquired)
        {
            acquired.Dispose();
            _lock = null;
            _logger.LogInformation("single_instance_lock_released: the single-instance lock was released on shutdown.");
        }
    }
}
