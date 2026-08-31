using System.Collections.Concurrent;

namespace Iris.Server.Security;

/// <summary>
/// The default <see cref="IInboundRateLimiter"/> (Phase 17.4): a per-peer sliding-window limiter. It
/// bounds how many signed inbox POSTs a peer may make per sliding window (default one minute) by
/// recording a timestamp for every accepted request and rejecting (429) when the peer's last
/// <c>maxRequests</c> timestamps are all inside the window.
/// </summary>
/// <remarks>
/// <strong>Peer key.</strong> The limiter keys on the <em>host</em> of the signer's <c>keyId</c> (the
/// sender host, e.g. <c>remote.example.org</c>) so all of a peer's actors/keys share a single inbound
/// rate budget.
/// </remarks>
/// <remarks>
/// <strong>Thread safety.</strong> Multiple concurrent requests may call <see cref="TryAcquire"/>
/// for the same or different peers. Each peer's state is guarded by its own lock, so concurrent
/// access is safe and a peer never admits more than <c>maxRequests</c> requests inside a window.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> When constructed with <c>maxRequests == 0</c> the limiter is a no-op:
/// <see cref="TryAcquire"/> always returns <c>true</c> (permitted). This keeps the default behavior
/// (no rate limit) unchanged for hosts that do not opt in.
/// </remarks>
/// <remarks>
/// <strong>Fail-fast, not blocking.</strong> Unlike the outbound
/// <see cref="Delivery.SlidingWindowDeliveryRateLimiter"/> (which blocks a background worker until a
/// slot frees), this limiter <em>rejects</em> immediately when the budget is exhausted. A web request
/// handler that blocks under load risks thread-pool exhaustion and request timeouts; a 429 lets the
/// client back off and retry later.
/// </remarks>
public sealed class SlidingWindowInboundRateLimiter : IInboundRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, PeerWindow> _peers = new();

    /// <summary>
    /// Initializes a new sliding-window inbound rate limiter.
    /// </summary>
    /// <param name="maxRequests">
    /// The maximum signed inbox POSTs per peer per window. 0 disables the limiter (the no-op default).
    /// </param>
    /// <param name="window">
    /// The sliding-window length. Defaults to one minute. Must be positive when
    /// <paramref name="maxRequests"/> is greater than 0.
    /// </param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="maxRequests"/> is negative, or <paramref name="window"/> is not positive
    /// and the limiter is enabled.
    /// </exception>
    public SlidingWindowInboundRateLimiter(int maxRequests, TimeSpan? window = null)
    {
        if (maxRequests < 0)
        {
            throw new ArgumentException(
                "maxRequests must be non-negative (0 disables the limiter).", nameof(maxRequests));
        }

        var effectiveWindow = window ?? TimeSpan.FromMinutes(1);
        if (maxRequests > 0 && effectiveWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "window must be positive when the limiter is enabled.", nameof(window));
        }

        _maxRequests = maxRequests;
        _window = effectiveWindow;
    }

    /// <inheritdoc/>
    public bool TryAcquire(string senderHost, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderHost);

        if (_maxRequests == 0)
        {
            return true; // disabled — always permitted
        }

        var peer = _peers.GetOrAdd(senderHost.ToLowerInvariant(), _ => new PeerWindow());

        // A peer's state is guarded by its own lock so concurrent requests cannot admit more than
        // _maxRequests inside a window.
        lock (peer.Lock)
        {
            var now = DateTimeOffset.UtcNow;
            // Drop timestamps that have fallen out of the sliding window (they no longer count
            // against the budget).
            peer.Expire(now, _window);

            if (peer.Count < _maxRequests)
            {
                // Room in the window: record this request and permit it.
                peer.Add(now);
                return true;
            }

            // Full: the peer has exceeded its budget. Reject with 429 (fail-fast, no blocking).
            return false;
        }
    }

    /// <summary>
    /// A peer's sliding window: an ordered list of request timestamps (oldest first) guarded by a
    /// lock so concurrent requests serialize their check-then-record.
    /// </summary>
    private sealed class PeerWindow
    {
        private readonly List<DateTimeOffset> _timestamps = new();

        /// <summary>
        /// The per-peer lock. Held for the short critical section (expire + check + add).
        /// </summary>
        public object Lock { get; } = new();

        /// <summary>
        /// The number of timestamps currently inside the window (the peer's used budget).
        /// </summary>
        public int Count => _timestamps.Count;

        /// <summary>
        /// Removes timestamps older than <c>now</c> − <paramref name="window"/>.
        /// </summary>
        public void Expire(DateTimeOffset now, TimeSpan window)
        {
            var cutoff = now - window;
            while (_timestamps.Count > 0 && _timestamps[0] < cutoff)
            {
                _timestamps.RemoveAt(0);
            }
        }

        /// <summary>
        /// Records a request at <paramref name="timestamp"/>.
        /// </summary>
        public void Add(DateTimeOffset timestamp) => _timestamps.Add(timestamp);
    }
}
