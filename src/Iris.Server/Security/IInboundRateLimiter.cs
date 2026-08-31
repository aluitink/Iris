namespace Iris.Server.Security;

/// <summary>
/// A per-peer inbound-delivery rate limiter (Phase 17.4, production scaling): bounds how many signed
/// POSTs a single remote peer may make to the instance's inbox endpoints per unit of time, so a
/// malicious or buggy remote instance cannot flood the inbox with signed deliveries.
/// </summary>
/// <remarks>
/// The peer is keyed by the <em>host</em> of the signer's <c>keyId</c> in the HTTP signature (e.g.
/// <c>https://remote.example.org/ap/v1/u/bob#key-1</c> → <c>remote.example.org</c>), mirroring the
/// outbound <see cref="Delivery.IDeliveryRateLimiter"/> which keys by the recipient inbox host.
/// </remarks>
/// <remarks>
/// <strong>Fail-fast, not blocking.</strong> Unlike the outbound limiter (which blocks a background
/// worker until a slot frees), the inbound limiter <em>rejects</em> a request with
/// <c>429 Too Many Requests</c> when the peer's budget is exhausted. A web request handler that
/// blocks under load is a different failure mode (thread-pool exhaustion, request timeouts); a 429
/// lets the client back off and retry later (the client's <c>RetryHandler</c> honors
/// <c>Retry-After</c> on 429s).
/// </remarks>
public interface IInboundRateLimiter
{
    /// <summary>
    /// Attempts to acquire one slot in the peer's per-minute budget. Returns <c>true</c> when the
    /// peer is within its limit (the request is permitted); <c>false</c> when the peer has exceeded
    /// its limit (the request should be rejected with <c>429 Too Many Requests</c>).
    /// </summary>
    /// <param name="senderHost">The host of the signer's <c>keyId</c> (the peer's identity). Must not
    /// be null or empty.</param>
    /// <param name="ct">The cancellation token. A host shutdown cancels it; the check returns
    /// (<see cref="OperationCanceledException"/>) promptly rather than blocking the shutdown.</param>
    /// <returns><c>true</c> when the request is permitted (within budget); <c>false</c> when it is
    /// not (budget exhausted).</returns>
    /// <exception cref="ArgumentException">When <paramref name="senderHost"/> is null or empty.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled.</exception>
    bool TryAcquire(string senderHost, CancellationToken ct);

    /// <summary>
    /// Returns the <see cref="DateTimeOffset"/> when the peer's rate-limit window will reset (the
    /// earliest time the peer may retry). Used to set the <c>Retry-After</c> HTTP-date header on
    /// 429 responses (Phase 18.3). When the limiter is disabled (no rate limit), returns the current
    /// time (the caller should not send a <c>Retry-After</c> header).
    /// </summary>
    /// <param name="senderHost">The host of the signer's <c>keyId</c> (the peer's identity).</param>
    /// <returns>The time when the peer's window resets, or <see cref="DateTimeOffset.UtcNow"/> when
    /// the limiter is disabled.</returns>
    DateTimeOffset GetRetryAfter(string senderHost);
}
