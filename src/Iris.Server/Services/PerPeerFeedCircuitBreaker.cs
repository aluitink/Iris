using System.Collections.Concurrent;
using Iris.Core;

namespace Iris.Server.Services;

/// <summary>
/// A per-peer circuit breaker guarding the feed's <em>inbound</em> remote-follow outbox fetches (Phase 146).
/// Each followed remote actor (keyed by the host of its actor IRI) has a circuit that transitions
/// closed → open → half-open → closed based on consecutive fetch failures.
/// </summary>
/// <remarks>
/// <strong>Motivation.</strong> The feed's per-follow fan-out fetches a remote follow's outbox over the
/// wire. The "a single broken remote must not fail the whole feed" guarantee already makes a failed
/// follow contribute an empty list, but without a breaker a <em>dead</em> remote (DNS failure, connection
/// refused — instantaneous, not a 5 s timeout) is re-probed on <em>every</em> feed rebuild (the 30-second
/// feed-cache TTL), so an offline instance is hammered on each and every feed load. The breaker opens a
/// dead peer's circuit after
/// <see cref="FeedRemoteFollowCircuitBreakerOptions.FailureThreshold"/> consecutive
/// failures and stops probing it until <see cref="FeedRemoteFollowCircuitBreakerOptions.OpenDuration"/> has
/// elapsed (half-open: one probe; success closes, failure re-opens). This mirrors the outbound
/// <see cref="Iris.Server.Delivery.PerPeerDeliveryCircuitBreaker"/> (Phase 17.3), which already bounds
/// the outbound-delivery path.
/// </remarks>
/// <remarks>
/// <strong>Peer key.</strong> The breaker keys on the <em>host</em> of the follow's actor IRI (lower-cased)
/// so all of a remote instance's followed actors share a single circuit — a dead instance is probed at
/// most once per <see cref="FeedRemoteFollowCircuitBreakerOptions.OpenDuration"/> regardless of how many of its
/// actors are followed. A relative IRI (no host) is keyed by its full value.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> When <see cref="FeedRemoteFollowCircuitBreakerOptions.FailureThreshold"/> is 0
/// (the default) the breaker is a no-op: <see cref="TryAcquireAsync"/> always permits and the record
/// methods are no-ops. This keeps the default behavior (no circuit breaking) unchanged for hosts that do
/// not opt in.
/// </remarks>
public interface IFeedCircuitBreaker
{
    /// <summary>
    /// Checks whether a fetch of <paramref name="followIri"/>'s remote outbox is permitted, and records
    /// that one is in flight when it is.
    /// </summary>
    /// <returns><c>true</c> when the fetch may proceed (circuit closed, or half-open with no probe in
    /// flight); <c>false</c> when it must be skipped (circuit open, or half-open with a probe in flight).</returns>
    Task<bool> TryAcquireAsync(Iri followIri, CancellationToken ct);

    /// <summary>
    /// Records a successful fetch: resets the peer's consecutive-failure count and closes its circuit.
    /// </summary>
    Task RecordSuccessAsync(Iri followIri, CancellationToken ct);

    /// <summary>
    /// Records a failed fetch: increments the peer's consecutive-failure count; at the threshold the
    /// circuit opens. A failed half-open probe re-opens the circuit.
    /// </summary>
    Task RecordFailureAsync(Iri followIri, CancellationToken ct);
}

/// <summary>
/// The default <see cref="IFeedCircuitBreaker"/> (Phase 146): a per-peer circuit breaker for the feed's
/// inbound remote-follow fetches. See <see cref="IFeedCircuitBreaker"/> for the motivation and semantics.
/// </summary>
/// <remarks>
/// <strong>Thread safety.</strong> The per-follow fan-out runs concurrent fetches (Task.WhenAll), so
/// multiple tasks may call the acquire/record methods for the same or different peers concurrently. Each
/// peer's state is guarded by its own lock, so concurrent access is safe.
/// </remarks>
public sealed class PerPeerFeedCircuitBreaker : IFeedCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly ConcurrentDictionary<string, PeerCircuit> _peers = new();

    /// <summary>
    /// Initializes a new per-peer feed circuit breaker.
    /// </summary>
    /// <param name="failureThreshold">
    /// The number of consecutive fetch failures that opens a peer's circuit. 0 disables the breaker
    /// (the no-op default).
    /// </param>
    /// <param name="openDuration">
    /// How long a peer's circuit stays open before transitioning to half-open. Must be non-negative when
    /// <paramref name="failureThreshold"/> is greater than 0. A value of zero means the circuit
    /// transitions to half-open immediately after opening (useful for tests).
    /// </param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="failureThreshold"/> is negative, or <paramref name="openDuration"/> is
    /// negative and the breaker is enabled.
    /// </exception>
    public PerPeerFeedCircuitBreaker(int failureThreshold, TimeSpan openDuration)
    {
        if (failureThreshold < 0)
        {
            throw new ArgumentException(
                "failureThreshold must be non-negative (0 disables the breaker).", nameof(failureThreshold));
        }

        if (failureThreshold > 0 && openDuration < TimeSpan.Zero)
        {
            throw new ArgumentException(
                "openDuration must be non-negative when the breaker is enabled.", nameof(openDuration));
        }

        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
    }

    /// <inheritdoc/>
    public Task<bool> TryAcquireAsync(Iri followIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.FromResult(true); // disabled — always permitted
        }

        var peerKey = PeerKey(followIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        var permitted = peer.TryAcquire(DateTimeOffset.UtcNow, _failureThreshold, _openDuration);
        return Task.FromResult(permitted);
    }

    /// <inheritdoc/>
    public Task RecordSuccessAsync(Iri followIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.CompletedTask; // disabled — no-op
        }

        var peerKey = PeerKey(followIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        peer.RecordSuccess();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RecordFailureAsync(Iri followIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.CompletedTask; // disabled — no-op
        }

        var peerKey = PeerKey(followIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        peer.RecordFailure(_failureThreshold, _openDuration);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The peer key for a follow's actor IRI: the host of the IRI (lower-cased, per RFC 3986 host
    /// case-insensitivity). A relative IRI (no scheme/host) is keyed by its full value so it still gets
    /// a distinct (and stable) circuit.
    /// </summary>
    private static string PeerKey(Iri followIri)
    {
        var uri = followIri.Uri;
        if (uri.IsAbsoluteUri)
        {
            return uri.Host.ToLowerInvariant();
        }

        return followIri.Value;
    }

    /// <summary>
    /// A peer's circuit state machine. Guarded by a lock so concurrent feed fan-outs serialize their
    /// check-then-record.
    /// </summary>
    private sealed class PeerCircuit
    {
        private readonly object _gate = new();

        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private DateTimeOffset _openedAtUtc;
        private bool _halfOpenProbeInFlight;

        private enum CircuitState
        {
            Closed,
            Open,
            HalfOpen,
        }

        /// <summary>
        /// Checks whether a fetch is permitted and, if so, records that one is in flight.
        /// </summary>
        /// <param name="now">The current UTC time.</param>
        /// <param name="failureThreshold">The failure threshold.</param>
        /// <param name="openDuration">How long the circuit stays open.</param>
        /// <returns><c>true</c> when the fetch is permitted (closed, or half-open with no probe in
        /// flight); <c>false</c> when it is not (open, or half-open with a probe already in flight).</returns>
        public bool TryAcquire(DateTimeOffset now, int failureThreshold, TimeSpan openDuration)
        {
            lock (_gate)
            {
                // Lazy state transition: if the circuit is open and the open duration has elapsed,
                // transition to half-open.
                if (_state == CircuitState.Open && now - _openedAtUtc >= openDuration)
                {
                    _state = CircuitState.HalfOpen;
                    _halfOpenProbeInFlight = false;
                }

                return _state switch
                {
                    CircuitState.Closed => true,
                    CircuitState.HalfOpen => AcquireHalfOpen(),
                    _ => false,
                };
            }
        }

        private bool AcquireHalfOpen()
        {
            // Half-open: allow exactly one probe. If a probe is already in flight, the fetch is not
            // permitted (it will be skipped and contribute nothing).
            if (_halfOpenProbeInFlight)
            {
                return false;
            }

            _halfOpenProbeInFlight = true;
            return true;
        }

        /// <summary>
        /// Records a successful fetch: resets the consecutive-failure count and closes the circuit (if it
        /// was half-open).
        /// </summary>
        public void RecordSuccess()
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _halfOpenProbeInFlight = false;
                if (_state != CircuitState.Closed)
                {
                    _state = CircuitState.Closed;
                }
            }
        }

        /// <summary>
        /// Records a failed fetch: increments the consecutive-failure count; when the count reaches the
        /// threshold the circuit opens. When the circuit was half-open (a probe), a failure re-opens it.
        /// </summary>
        /// <param name="failureThreshold">The failure threshold.</param>
        /// <param name="openDuration">How long the circuit stays open (used to set <c>OpenedAtUtc</c>).</param>
        public void RecordFailure(int failureThreshold, TimeSpan openDuration)
        {
            lock (_gate)
            {
                _halfOpenProbeInFlight = false;

                if (_state == CircuitState.HalfOpen)
                {
                    // A probe failed: re-open the circuit for another open duration.
                    _state = CircuitState.Open;
                    _openedAtUtc = DateTimeOffset.UtcNow;
                    _consecutiveFailures = failureThreshold;
                    return;
                }

                _consecutiveFailures++;
                if (_consecutiveFailures >= failureThreshold)
                {
                    _state = CircuitState.Open;
                    _openedAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }
    }
}
