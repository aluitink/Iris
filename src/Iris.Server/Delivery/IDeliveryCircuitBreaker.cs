namespace Iris.Server.Delivery;

/// <summary>
/// A per-peer outbound-delivery circuit breaker (Phase 17.3, production scaling): stops the
/// <see cref="DeliveryWorker"/> from hammering a downed peer. When a peer's inbox endpoint accumulates
/// enough consecutive failures, the circuit for that peer opens and deliveries to that peer are skipped
/// (dead-lettered immediately, not retried) until the peer recovers.
/// </summary>
/// <remarks>
/// The peer is keyed by the <em>host</em> of the recipient's inbox IRI (e.g.
/// <c>https://b.domain.local/ap/v1/u/bob/inbox</c> → <c>b.domain.local</c>), mirroring the
/// <see cref="IDeliveryRateLimiter"/>: all of a peer's inboxes share a single circuit.
/// </remarks>
/// <remarks>
/// <strong>States.</strong> Each peer's circuit is in one of three states:
/// <list type="bullet">
/// <item><term>Closed</term><description>Normal operation. Deliveries are allowed; consecutive
/// failures are counted.</description></item>
/// <item><term>Open</term><description>The peer has failed enough times. Deliveries to the peer are
/// <em>skipped</em> (the worker dead-letters them immediately without a network call) until
/// <c>OpenDuration</c> elapses.</description></item>
/// <item><term>Half-open</term><description>The open duration has elapsed. A single probe delivery is
/// allowed through. If it succeeds the circuit closes; if it fails the circuit re-opens.</description></item>
/// </list>
/// </remarks>
public interface IDeliveryCircuitBreaker
{
    /// <summary>
    /// Checks whether a delivery to the peer behind <paramref name="inboxIri"/> is permitted. When the
    /// circuit is open the method returns <c>false</c> (the worker should skip the delivery and
    /// dead-letter the job immediately). When the circuit is closed or half-open the method returns
    /// <c>true</c> and records that a delivery attempt is in flight (so
    /// <see cref="RecordSuccessAsync"/> / <see cref="RecordFailureAsync"/> can resolve it).
    /// </summary>
    /// <param name="inboxIri">The IRI of the inbox being delivered to. Its host is the circuit peer key.
    /// Must not be null.</param>
    /// <param name="ct">The cancellation token. A host shutdown cancels it; the check returns
    /// (<see cref="OperationCanceledException"/>) promptly rather than blocking the shutdown.</param>
    /// <returns>A task that resolves to <c>true</c> when the delivery is permitted (closed or half-open)
    /// or <c>false</c> when it is not (open).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="inboxIri"/> is null.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled.</exception>
    public Task<bool> TryAcquireAsync(Iri inboxIri, CancellationToken ct);

    /// <summary>
    /// Records that a delivery to the peer behind <paramref name="inboxIri"/> succeeded. Resets the
    /// peer's consecutive-failure count to zero and (if the circuit was half-open) closes it.
    /// </summary>
    /// <param name="inboxIri">The IRI of the inbox delivered to. Its host is the circuit peer key. Must
    /// not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="inboxIri"/> is null.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled.</exception>
    public Task RecordSuccessAsync(Iri inboxIri, CancellationToken ct);

    /// <summary>
    /// Records that a delivery to the peer behind <paramref name="inboxIri"/> failed. Increments the
    /// peer's consecutive-failure count; when the count reaches the threshold the circuit opens. When the
    /// circuit was half-open (a probe), a failure re-opens it for another <c>OpenDuration</c>.
    /// </summary>
    /// <param name="inboxIri">The IRI of the inbox delivered to. Its host is the circuit peer key. Must
    /// not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="inboxIri"/> is null.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled.</exception>
    public Task RecordFailureAsync(Iri inboxIri, CancellationToken ct);
}
