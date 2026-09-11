using Iris.Core;
using Iris.Server.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Caching;

/// <summary>
/// A hosted service that applies cross-instance cache-invalidation events to the local actor/edge caches on
/// a fixed interval (Phase 84.6, shared-state scale-out — the "when" of the invalidation).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CacheInvalidationChannel"/> is the <em>channel</em>: a shared, file-backed invalidation
/// journal two (or more) instances over the same origin publish to and poll from. This service is the
/// <em>reader</em>: on host start, and then every <see cref="ActivityPubServerOptions
/// .CacheInvalidationPollInterval"/> (default 5 s; a non-positive value disables the periodic poll but still
/// runs the startup pass), it polls the channel for new events (since its last-seen <c>Seq</c> cursor) and,
/// for each event, invalidates the local <see cref="RemoteActorCache"/> + <see cref="LocalActorDocumentCache"/>
/// entries for the affected actor IRI.
/// </para>
/// <para>
/// The service is registered only when a <see cref="CacheInvalidationChannel"/> is registered (via
/// <c>UseCacheInvalidationChannel</c>); with the single-instance default (no channel) the service is not
/// registered, so the common single-instance deployment and every existing test harness are unaffected.
/// </para>
/// <para>
/// The poll is best-effort: a failed read (a transient I/O blip) is logged and the next tick retries; it
/// never throws into the host. The cursor (<c>lastSeq</c>) is in-memory (a restart re-reads the journal from
/// <c>Seq 0</c>, which is a harmless re-invalidating no-op for already-invalidated entries).
/// </para>
/// </remarks>
public sealed class CacheInvalidationService : BackgroundService
{
    private readonly CacheInvalidationChannel _channel;
    private readonly RemoteActorCache _remoteActors;
    private readonly LocalActorDocumentCache _localActorDocuments;
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly ILogger<CacheInvalidationService> _logger;
    private readonly TimeSpan _interval;
    private int _lastSeq;

    /// <summary>
    /// Initializes a new <see cref="CacheInvalidationService"/>.
    /// </summary>
    /// <param name="channel">
    /// The shared <see cref="CacheInvalidationChannel"/> (the invalidation journal to poll).
    /// </param>
    /// <param name="remoteActors">
    /// The local <see cref="RemoteActorCache"/> (invalidated on each event — the cached remote actor
    /// documents).
    /// </param>
    /// <param name="localActorDocuments">
    /// The local <see cref="LocalActorDocumentCache"/> (invalidated on each event — the rendered local
    /// actor documents).
    /// </param>
    /// <param name="options">
    /// The instance's options (provides <see cref="ActivityPubServerOptions.CacheInvalidationPollInterval"/>).
    /// </param>
    /// <param name="logger">A logger.</param>
    public CacheInvalidationService(
        CacheInvalidationChannel channel,
        RemoteActorCache remoteActors,
        LocalActorDocumentCache localActorDocuments,
        IOptions<ActivityPubServerOptions> options,
        ILogger<CacheInvalidationService> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _remoteActors = remoteActors ?? throw new ArgumentNullException(nameof(remoteActors));
        _localActorDocuments = localActorDocuments ?? throw new ArgumentNullException(nameof(localActorDocuments));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = _options.Value.CacheInvalidationPollInterval;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The startup pass runs before the interval loop so a host that starts with a non-empty journal
        // (events published before this instance started) applies them before serving its first request.
        await PollOnceAsync(stoppingToken).ConfigureAwait(false);

        // A non-positive interval disables the periodic poll (the startup pass still ran). This lets a host
        // that wants on-demand invalidation (e.g. via a later push trigger) opt out of the timer without
        // disabling the service.
        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — expected
        }
    }

    /// <summary>
    /// Polls the channel once and applies the new events (the pass <see cref="ExecuteAsync"/> runs at
    /// startup and on each tick). Never throws — a failed read is logged and the next tick retries.
    /// </summary>
    /// <remarks>
    /// Public so a host (or a test) can trigger an on-demand poll (e.g. after a known actor-document change)
    /// without waiting for the next tick. The cursor (<c>lastSeq</c>) advances only on a successful poll; a
    /// failed poll leaves the cursor unchanged (the next poll retries the same events).
    /// </remarks>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var events = await _channel.PollAsync(_lastSeq, ct: ct).ConfigureAwait(false);
            if (events.Count == 0)
            {
                return;
            }

            foreach (var @event in events)
            {
                var actorIri = new Iri(@event.ActorIri);
                var invalidatedRemote = _remoteActors.Invalidate(actorIri);
                var invalidatedLocal = _localActorDocuments.Invalidate(actorIri);
                _lastSeq = Math.Max(_lastSeq, @event.Seq);
                _logger.LogDebug(
                    "cache_invalidated: actor {Actor} (remote: {Remote}, local: {Local}).",
                    actorIri.Value, invalidatedRemote, invalidatedLocal);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — do not log a failure on a cancelled poll
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "cache_invalidation_poll_failed: the journal read failed; the next tick retries.");
        }
    }
}
