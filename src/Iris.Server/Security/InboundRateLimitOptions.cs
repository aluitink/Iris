namespace Iris.Server.Security;

/// <summary>
/// Options for the inbound-delivery rate limit (Phase 17.4, production scaling): bounds how many
/// signed POSTs a single remote peer may make to the instance's inbox endpoints per sliding minute.
/// </summary>
/// <remarks>
/// A malicious or buggy remote instance can flood the inbox with signed deliveries (a relay
/// misconfiguration, a bug that re-delivers the same activity in a tight loop, or a deliberate
/// DoS). <see cref="PerPeerMaxRequestsPerMinute"/> bounds how many signed inbox POSTs the server
/// accepts from a single peer (keyed by the host of the signer's <c>keyId</c> in the HTTP signature)
/// per sliding minute. A peer that exceeds the limit receives <c>429 Too Many Requests</c>
/// (fail-fast; the request is not queued or retried). A value of 0 (the default) disables the rate
/// limit entirely — the server accepts inbox POSTs as fast as the request pipeline allows.
/// </remarks>
/// <remarks>
/// <strong>Why per sender host, not per keyId.</strong> A well-behaved remote instance signs all its
/// deliveries with keys that live on the same host (e.g.
/// <c>https://remote.example.org/ap/v1/u/bob#key-1</c> → host <c>remote.example.org</c>). Keying by
/// host (not by the full keyId IRI) means all of a peer's actors/keys share a single inbound rate
/// budget, mirroring the outbound <see cref="Delivery.DeliveryRateLimitOptions"/> which keys by the
/// recipient inbox host.
/// </remarks>
public sealed class InboundRateLimitOptions
{
    /// <summary>
    /// The maximum number of signed inbox POSTs the server accepts from a single peer (keyed by the
    /// host of the signer's <c>keyId</c>) per sliding minute. Must be at least 0 (0 = rate limiting
    /// disabled, the default; the server accepts inbox POSTs as fast as the request pipeline allows).
    /// Defaults to 0.
    /// </summary>
    public int PerPeerMaxRequestsPerMinute { get; init; } = 0;
}
