using System.Collections.Concurrent;
using Iris.Core;

namespace Iris.Server.Delivery;

/// <summary>
/// The default <see cref="IDeliveryCircuitBreaker"/> (Phase 17.3): a per-peer circuit breaker. Each peer
/// (keyed by the host of the inbox IRI) has a circuit that transitions closed → open → half-open →
/// closed based on consecutive delivery failures.
/// </summary>
/// <remarks>
/// <strong>Peer key.</strong> The breaker keys on the <em>host</em> of the inbox IRI (e.g.
/// <c>https://b.example/ap/v1/u/bob/inbox</c> → <c>b.example</c>) so all of a peer's inboxes (shared
/// inbox + per-actor inboxes) share a single circuit.
/// </remarks>
/// <remarks>
/// <strong>Thread safety.</strong> Multiple delivery tasks (up to
/// <see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/> in flight) may call
/// <see cref="TryAcquireAsync"/> / <see cref="RecordSuccessAsync"/> / <see cref="RecordFailureAsync"/>
/// concurrently for the same or different peers. Each peer's state is guarded by its own lock, so
/// concurrent access is safe.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> When constructed with <c>failureThreshold == 0</c> the breaker is a no-op:
/// <see cref="TryAcquireAsync"/> always returns <c>true</c> and the record methods are no-ops. This keeps
/// the default behavior (no circuit breaking) unchanged for hosts that do not opt in.
/// </remarks>
/// <remarks>
/// <strong>State machine.</strong>
/// <list type="bullet">
/// <item><term>Closed → Open</term><description>When the consecutive-failure count reaches
/// <c>failureThreshold</c>. The <c>OpenedAtUtc</c> timestamp is set to <c>UtcNow</c>.</description></item>
/// <item><term>Open → Half-open</term><description>When <c>UtcNow − OpenedAtUtc ≥ OpenDuration</c> (checked
/// lazily in <see cref="TryAcquireAsync"/>).</description></item>
/// <item><term>Half-open → Closed</term><description>When a probe delivery succeeds
/// (<see cref="RecordSuccessAsync"/>).</description></item>
/// <item><term>Half-open → Open</term><description>When a probe delivery fails
/// (<see cref="RecordFailureAsync"/>). <c>OpenedAtUtc</c> is reset to <c>UtcNow</c>.</description></item>
/// <item><term>Closed (reset)</term><description>When any delivery succeeds (<see cref="RecordSuccessAsync"/>):
/// the consecutive-failure count is reset to zero.</description></item>
/// </list>
/// </remarks>
public sealed class PerPeerDeliveryCircuitBreaker : IDeliveryCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly ConcurrentDictionary<string, PeerCircuit> _peers = new();

    /// <summary>
    /// Initializes a new per-peer circuit breaker.
    /// </summary>
    /// <param name="failureThreshold">
    /// The number of consecutive failures that opens a peer's circuit. 0 disables the breaker (the no-op
    /// default).
    /// </param>
    /// <param name="openDuration">
    /// How long a peer's circuit stays open before transitioning to half-open. Must be non-negative
    /// when <paramref name="failureThreshold"/> is greater than 0. A value of zero means the circuit
    /// transitions to half-open immediately after opening (useful for tests).
    /// </param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="failureThreshold"/> is negative, or <paramref name="openDuration"/> is
    /// negative and the breaker is enabled.
    /// </exception>
    public PerPeerDeliveryCircuitBreaker(int failureThreshold, TimeSpan openDuration)
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
    public Task<bool> TryAcquireAsync(Iri inboxIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.FromResult(true); // disabled — always permitted
        }

        var peerKey = PeerKey(inboxIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        var permitted = peer.TryAcquire(DateTimeOffset.UtcNow, _failureThreshold, _openDuration);
        return Task.FromResult(permitted);
    }

    /// <inheritdoc/>
    public Task RecordSuccessAsync(Iri inboxIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.CompletedTask; // disabled — no-op
        }

        var peerKey = PeerKey(inboxIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        peer.RecordSuccess();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RecordFailureAsync(Iri inboxIri, CancellationToken ct)
    {
        if (_failureThreshold == 0)
        {
            return Task.CompletedTask; // disabled — no-op
        }

        var peerKey = PeerKey(inboxIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerCircuit());

        peer.RecordFailure(_failureThreshold, _openDuration);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The peer key for an inbox IRI: the host of the IRI (lower-cased, per RFC 3986 host
    /// case-insensitivity). A relative IRI (no scheme/host) is keyed by its full value so it still gets
    /// a distinct (and stable) circuit.
    /// </summary>
    private static string PeerKey(Iri inboxIri)
    {
        var uri = inboxIri.Uri;
        if (uri.IsAbsoluteUri)
        {
            return uri.Host.ToLowerInvariant();
        }

        return inboxIri.Value;
    }

    /// <summary>
    /// A peer's circuit state machine. Guarded by a lock so concurrent delivery tasks serialize their
    /// check-then-record.
    /// </summary>
    private sealed class PeerCircuit
    {
        private readonly object _gate = new();

        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private DateTimeOffset _openedAtUtc;
        private bool _halfOpenProbeInFlight;

        /// <summary>
        /// The three states of the circuit.
        /// </summary>
        private enum CircuitState
        {
            Closed,
            Open,
            HalfOpen,
        }

        /// <summary>
        /// Checks whether a delivery is permitted and, if so, records that one is in flight.
        /// </summary>
        /// <param name="now">The current UTC time (injected for testability).</param>
        /// <param name="failureThreshold">The failure threshold (redundant with the outer breaker's value,
        /// but passed to avoid a second field lookup).</param>
        /// <param name="openDuration">How long the circuit stays open.</param>
        /// <returns><c>true</c> when the delivery is permitted (closed, or half-open with no probe in
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
                    CircuitState.Closed => AcquireClosed(),
                    CircuitState.HalfOpen => AcquireHalfOpen(),
                    CircuitState.Open => false,
                    _ => false,
                };
            }
        }

        private bool AcquireClosed()
        {
            // Closed: the delivery is permitted. No in-flight tracking needed — the record methods
            // operate on the failure count directly.
            return true;
        }

        private bool AcquireHalfOpen()
        {
            // Half-open: allow exactly one probe. If a probe is already in flight, the delivery is not
            // permitted (it will be dead-lettered by the worker).
            if (_halfOpenProbeInFlight)
            {
                return false;
            }

            _halfOpenProbeInFlight = true;
            return true;
        }

        /// <summary>
        /// Records a successful delivery: resets the consecutive-failure count and closes the circuit
        /// (if it was half-open).
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
        /// Records a failed delivery: increments the consecutive-failure count; when the count reaches
        /// the threshold the circuit opens. When the circuit was half-open (a probe), a failure re-opens
        /// it.
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
