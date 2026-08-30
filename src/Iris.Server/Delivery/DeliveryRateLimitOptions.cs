namespace Iris.Server.Delivery;

/// <summary>
/// Options for the <see cref="DeliveryWorker"/> per-peer outbound-delivery rate limit (Phase 16.3,
/// production scaling).
/// </summary>
/// <remarks>
/// A production instance delivering a burst of activities (a relay fan-out, a popular actor posting to
/// many followers, or a retry storm to a downed peer) can overwhelm a single remote peer's inbox
/// endpoint. <see cref="PerPeerMaxRequestsPerMinute"/> bounds how many deliveries the worker may send
/// to a single peer (keyed by the host of the recipient's inbox IRI) per sliding minute. A value of 0
/// (the default) disables the rate limit entirely — the worker delivers as fast as the
/// <see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/> concurrency cap allows.
/// </remarks>
/// <remarks>
/// <strong>Why per host, not per inbox IRI.</strong> A well-behaved instance wants a single outbound
/// rate to a remote peer (its host), not one per inbox: a peer's shared inbox and its per-actor
/// inboxes all live on the same host, and spreading deliveries across inboxes would let the instance
/// exceed the peer's comfortable intake rate. The rate limit is therefore keyed by the inbox IRI's
/// host. This complements the Phase 16.1 concurrency cap: <see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/>
/// bounds how many deliveries are in flight at once (connection-pool pressure), while this bounds how
/// fast the worker may send to a given peer over time (peer politeness).
/// </remarks>
public sealed class DeliveryRateLimitOptions
{
    /// <summary>
    /// The maximum number of deliveries the worker may send to a single peer (keyed by the host of the
    /// recipient's inbox IRI) per sliding minute. Must be at least 0 (0 = rate limiting disabled, the
    /// default; the worker delivers as fast as the concurrency cap allows). Defaults to 0.
    /// </summary>
    public int PerPeerMaxRequestsPerMinute { get; init; } = 0;
}
