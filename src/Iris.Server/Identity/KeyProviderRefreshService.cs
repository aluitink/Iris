using Iris.Client.Auth;
using Iris.Server.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Identity;

/// <summary>
/// A hosted service that converges a <see cref="DocumentDerivedKeyProvider"/> to the durable actor
/// documents on a fixed interval (Phase 84.6, shared-state scale-out — the "when" of the refresh).
/// </summary>
/// <remarks>
/// <para>
/// Part 1 of 84.6 shipped the <em>convergence primitive</em>: <see cref="DocumentDerivedKeyProvider
/// .RefreshFromActorsAsync"/> re-derives every local actor's key IRI from the durable actor document's
/// <c>publicKey.id</c>, so a rotation performed on <em>another</em> instance over the same persistence
/// becomes visible here — no restart. But the primitive is only useful if something <em>calls</em> it.
/// The sync <c>SigningHandler.TryGetIdentity</c> cannot do the async document read itself (CODING_STYLE
/// forbids sync-over-async in library code), so the "when" is this background service.
/// </para>
/// <para>
/// On host start, and then every <see cref="ActivityPubServerOptions.KeyProviderRefreshInterval"/>
/// (default 30s; a non-positive value disables the periodic refresh but still runs the startup pass), the
/// service resolves the instance's <see cref="IKeyProvider"/> and, if it is a
/// <see cref="DocumentDerivedKeyProvider"/>, re-runs <c>RefreshFromActorsAsync</c> over the shared
/// <see cref="IPersistenceProvider"/>. Any other <see cref="IKeyProvider"/> (the default
/// <see cref="InMemoryKeyProvider"/>, or a host-registered <see cref="DelegatingKeyProvider"/>) is
/// left untouched: the service is then a no-op, so the common single-instance deployment and every
/// existing test harness are unaffected.
/// </para>
/// <para>
/// The refresh is best-effort: a failed document read (a transient persistence blip) is logged and the
/// next tick retries; it never throws into the host. The startup pass runs before the interval loop so a
/// host that registers a fresh (empty) <see cref="DocumentDerivedKeyProvider"/> converges to the seeded
/// actors before it signs its first outbound request.
/// </para>
/// </remarks>
public sealed class KeyProviderRefreshService : BackgroundService
{
    private readonly IKeyProvider _keyProvider;
    private readonly IPersistenceProvider _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly ILogger<KeyProviderRefreshService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>
    /// Initializes a new <see cref="KeyProviderRefreshService"/>.
    /// </summary>
    /// <param name="keyProvider">
    /// The instance's <see cref="IKeyProvider"/> (the convergence target when it is a
    /// <see cref="DocumentDerivedKeyProvider"/>; otherwise the service is inert).
    /// </param>
    /// <param name="persistence">
    /// The shared persistence provider (its <see cref="IPersistenceProvider.Actors"/> +
    /// <see cref="IPersistenceProvider.Keys"/> are the durable source of truth).
    /// </param>
    /// <param name="options">
    /// The instance's options (provides <see cref="ActivityPubServerOptions.KeyProviderRefreshInterval"/>).
    /// </param>
    /// <param name="logger">A logger.</param>
    public KeyProviderRefreshService(
        IKeyProvider keyProvider,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> options,
        ILogger<KeyProviderRefreshService> logger)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = _options.Value.KeyProviderRefreshInterval;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The startup pass runs before the interval loop so a fresh (empty) provider converges to the
        // seeded actors before the first outbound signature. A no-op when the provider is not
        // document-derived.
        await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

        // A non-positive interval disables the periodic refresh (the startup pass still ran). This lets a
        // host that wants on-demand convergence (e.g. via a later on-miss trigger) opt out of the timer
        // without disabling the service.
        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — expected
        }
    }

    /// <summary>
    /// Converges the instance's key provider once (the pass <see cref="ExecuteAsync"/> runs at startup and
    /// on each tick). A no-op when the provider is not a <see cref="DocumentDerivedKeyProvider"/>. Never
    /// throws — a failed document read is logged and the next tick retries.
    /// </summary>
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        if (_keyProvider is not DocumentDerivedKeyProvider documentDerived)
        {
            // Inert: the default InMemoryKeyProvider (or any host-registered provider) has no refresh.
            // The common single-instance deployment is unaffected.
            return;
        }

        try
        {
            var count = await documentDerived
                .RefreshFromActorsAsync(_persistence.Actors, _persistence.Keys, ct)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "key_provider_converged: re-derived {Count} local actor key bindings from the durable documents.",
                count);
        }
        catch (OperationCanceledException)
        {
            // host stopping — do not log a failure on a cancelled refresh
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "key_provider_refresh_failed: the document read failed; the provider keeps its last converged state and the next tick retries.");
        }
    }
}
