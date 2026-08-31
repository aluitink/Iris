namespace Iris.Server.Delivery;

/// <summary>
/// Options for the <see cref="DeliveryWorker"/> per-peer outbound-delivery circuit breaker (Phase 17.3,
/// production scaling).
/// </summary>
/// <remarks>
/// When a peer's inbox endpoint is down (repeatedly returning 5xx / timing out), the worker would
/// otherwise retry <em>every</em> queued job to that peer with its full
/// <see cref="DeliveryRetryOptions"/> budget — hammering a downed peer with a stream of doomed requests
/// while the queue backs up. A circuit breaker stops this: once a peer accumulates
/// <see cref="FailureThreshold"/> consecutive failures, the circuit for that peer <em>opens</em> and
/// deliveries to that peer are skipped (dead-lettered immediately, not retried) for
/// <see cref="OpenDuration"/>. After the open duration elapses, the circuit enters the <em>half-open</em>
/// state: a single probe delivery is allowed through. If the probe succeeds the circuit <em>closes</em>
/// (the peer is healthy again); if it fails the circuit re-opens for another <see cref="OpenDuration"/>.
/// </remarks>
/// <remarks>
/// <strong>Peer key.</strong> The breaker keys on the <em>host</em> of the recipient's inbox IRI (e.g.
/// <c>https://b.domain.local/ap/v1/u/bob/inbox</c> → <c>b.domain.local</c>), mirroring the
/// <see cref="IDeliveryRateLimiter"/>: all of a peer's inboxes (shared inbox + per-actor inboxes) share
/// a single circuit.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> A <see cref="FailureThreshold"/> of 0 (the default) disables the breaker
/// entirely — the worker delivers exactly as before (per-job retry + dead-letter, no per-peer
/// circuit). A host opts in by rebinding this to a positive threshold.
/// </remarks>
public sealed class DeliveryCircuitBreakerOptions
{
    /// <summary>
    /// The number of consecutive failures (across all deliveries to a peer) that opens the circuit for
    /// that peer. Must be at least 0 (0 = circuit breaker disabled, the default; the worker delivers
    /// with per-job retry only). Defaults to 0.
    /// </summary>
    public int FailureThreshold { get; init; } = 0;

    /// <summary>
    /// How long the circuit stays open (deliveries to the peer are skipped) before it transitions to
    /// half-open (a single probe is allowed). Must be positive when <see cref="FailureThreshold"/> is
    /// greater than 0. Defaults to 60 seconds.
    /// </summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(60);
}
