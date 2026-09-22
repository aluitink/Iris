namespace Iris.Server.Services;

/// <summary>
/// Options for the feed's per-peer remote-follow circuit breaker (Phase 146): bounds how often a dead
/// remote is re-probed when assembling the feed.
/// </summary>
/// <remarks>
/// The feed's per-follow fan-out fetches each remote follow's outbox over the wire. A failed fetch
/// contributes nothing (a single broken remote must not fail the whole feed), but without a breaker a
/// dead remote (DNS failure, connection refused) is re-probed on every feed rebuild (the 30-second feed
/// cache TTL). When <see cref="FailureThreshold"/> consecutive fetch failures are recorded for a peer
/// (keyed by the host of the follow's actor IRI), that peer's circuit <em>opens</em> and the feed skips
/// probing it until <see cref="OpenDuration"/> has elapsed. After the open duration elapses the circuit
/// enters the <em>half-open</em> state: a single probe is allowed; a success closes the circuit (the
/// remote is healthy again) and a failure re-opens it for another <see cref="OpenDuration"/>.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> A <see cref="FailureThreshold"/> of 0 (the default) disables the breaker:
/// every remote follow is fetched on every rebuild (the pre-146 behavior). This mirrors
/// <see cref="Iris.Server.Delivery.DeliveryCircuitBreakerOptions"/>, which guards the outbound-delivery
/// path the same way.
/// </remarks>
public sealed class FeedRemoteFollowCircuitBreakerOptions
{
    /// <summary>
    /// The number of consecutive remote-fetch failures that opens a peer's circuit. 0 (the default)
    /// disables the breaker (no circuit breaking).
    /// </summary>
    public int FailureThreshold { get; init; } = 0;

    /// <summary>
    /// How long a peer's circuit stays open before transitioning to half-open (a single probe is
    /// allowed). Must be non-negative when <see cref="FailureThreshold"/> is greater than 0.
    /// </summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(60);
}
