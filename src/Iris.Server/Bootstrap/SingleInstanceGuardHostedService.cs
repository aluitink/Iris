using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Bootstrap;

/// <summary>
/// A hosted service that acquires the <see cref="SingleInstanceLock"/> at startup and releases it at
/// shutdown (Phase 84.5; lifted in Phase 84.6 to support multi-instance scale-out).
/// </summary>
/// <remarks>
/// When <see cref="ActivityPubServerOptions.InstanceLockPath"/> is configured, this service is registered
/// by <c>AddActivityPubServer</c>. On <see cref="IHostedService.StartAsync"/> it calls
/// <see cref="SingleInstanceLock.AcquireAsync"/>: a lock held by a <em>live</em> different instance is a
/// collision. The guard's behavior on a collision depends on whether the <em>scale-out</em> convergence
/// pieces are all configured (Phase 84.6):
/// <list type="bullet">
/// <item><strong>All three configured</strong> (the <see cref="Identity.DocumentDerivedKeyProvider"/> as
/// <c>IKeyProvider</c> + the <see cref="Delivery.SharedDeliveryQueue"/> as <c>IDeliveryQueue</c> + the
/// <see cref="Caching.CacheInvalidationChannel"/> as <c>ICacheInvalidationPublisher</c>): multi-instance is
/// <em>supported</em> — the instances converge via the shared state (the document-derived key provider
/// re-derives keys from the durable documents; the shared delivery queue is a single journal; the
/// cache-invalidation channel propagates actor-document changes). The guard logs a <em>warning</em> and
/// does <em>not</em> fail host startup (the operator is informed, but the deployment is valid).</item>
/// <item><strong>Any missing</strong>: multi-instance is <em>unsafe</em> (the instances would diverge on
/// the un-converged surface — the actor→key binding map, the delivery queue, or the actor/edge caches).
/// The guard <em>fails fast</em> (throws <see cref="InstanceAlreadyRunningException"/>), and the operator
/// must either stop the other instance, point this instance at a different persistence, or configure the
/// missing scale-out piece(s).</item>
/// </list>
///
/// On <see cref="IHostedService.StopAsync"/> it disposes the lock (deletes the lock file). When no lock
/// path is configured (the default — every test harness and the in-memory/file-backed deployments), this
/// service is not registered and the guard is inert: existing multi-host test setups and single-process
/// deployments are unaffected.
/// </remarks>
public sealed class SingleInstanceGuardHostedService : IHostedService
{
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SingleInstanceGuardHostedService> _logger;
    private SingleInstanceLock? _lock;
    private Task? _started;
    private readonly TaskCompletionSource _startedTcs = new();

    /// <summary>
    /// Initializes a new <see cref="SingleInstanceGuardHostedService"/>.
    /// </summary>
    /// <param name="options">The instance's options (provides <see cref="ActivityPubServerOptions.InstanceLockPath"/>).</param>
    /// <param name="serviceProvider">
    /// The service provider (used to resolve the scale-out convergence pieces on a collision — the
    /// <c>IKeyProvider</c>, <c>IDeliveryQueue</c>, and <c>ICacheInvalidationPublisher</c> — to decide
    /// whether multi-instance is supported).
    /// </param>
    /// <param name="logger">A logger.</param>
    public SingleInstanceGuardHostedService(
        IOptions<ActivityPubServerOptions> options,
        IServiceProvider serviceProvider,
        ILogger<SingleInstanceGuardHostedService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
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
        catch (InstanceAlreadyRunningException ex)
        {
            // A live different instance holds the lock. Decide: is multi-instance supported (all three
            // scale-out convergence pieces configured) or unsafe (any missing)?
            if (IsMultiInstanceSupported())
            {
                // Multi-instance is supported: the instances converge via the shared state (the
                // document-derived key provider, the shared delivery queue, the cache-invalidation
                // channel). Log a warning (the operator is informed) but do NOT fail host startup —
                // the deployment is valid.
                _logger.LogWarning(
                    "multi_instance_detected: a live different instance (pid {OwnerPid}, host '{OwnerHost}') holds the lock at {LockPath}. " +
                    "Multi-instance is supported (all three scale-out convergence pieces are configured: the document-derived key provider, " +
                    "the shared delivery queue, the cache-invalidation channel). The instances will converge via the shared state. " +
                    "No action is required.",
                    ex.OwnerPid, ex.OwnerHost, ex.LockPath);
            }
            else
            {
                // Multi-instance is unsafe (a convergence piece is missing): fail fast. The operator must
                // stop the other instance, point this instance at a different persistence, or configure
                // the missing scale-out piece(s).
                _logger.LogError(
                    "single_instance_conflict: a live different instance (pid {OwnerPid}, host '{OwnerHost}') holds the lock at {LockPath}. " +
                    "Multi-instance is NOT supported (a scale-out convergence piece is missing). " +
                    "Stop the other instance, point this one at a different persistence, or configure all three scale-out pieces " +
                    "(UseDocumentDerivedKeyProvider + UseSharedDelivery + UseCacheInvalidationChannel).",
                    ex.OwnerPid, ex.OwnerHost, ex.LockPath);
                throw;
            }
        }
        finally
        {
            _startedTcs.TrySetResult();
        }
    }

    /// <summary>
    /// Whether the scale-out convergence pieces are all configured (Phase 84.6): the
    /// <see cref="Identity.DocumentDerivedKeyProvider"/> as <c>IKeyProvider</c> + the
    /// <see cref="Delivery.SharedDeliveryQueue"/> as <c>IDeliveryQueue</c> + the
    /// <see cref="Caching.CacheInvalidationChannel"/> as <c>ICacheInvalidationPublisher</c>. When all three
    /// are configured, multi-instance over one origin is supported (the instances converge via the shared
    /// state). When any is missing, multi-instance is unsafe (the instances would diverge on the
    /// un-converged surface).
    /// </summary>
    private bool IsMultiInstanceSupported()
    {
        // Resolve the three convergence pieces by type and check their concrete types. A missing piece
        // (not registered) resolves to null (GetService) and is treated as "not configured".
        var keyProvider = _serviceProvider.GetService<Iris.Client.Auth.IKeyProvider>();
        var deliveryQueue = _serviceProvider.GetService<Iris.Server.Delivery.IDeliveryQueue>();
        var cacheInvalidation = _serviceProvider.GetService<Iris.Server.Caching.ICacheInvalidationPublisher>();

        var hasDocumentDerivedKeyProvider = keyProvider is Iris.Server.Identity.DocumentDerivedKeyProvider;
        var hasSharedDeliveryQueue = deliveryQueue is Iris.Server.Delivery.SharedDeliveryQueue;
        var hasCacheInvalidationChannel = cacheInvalidation is Iris.Server.Caching.CacheInvalidationChannel;

        return hasDocumentDerivedKeyProvider && hasSharedDeliveryQueue && hasCacheInvalidationChannel;
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
