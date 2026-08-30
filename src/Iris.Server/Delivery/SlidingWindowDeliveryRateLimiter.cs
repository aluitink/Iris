using System.Collections.Concurrent;
using Iris.Core;

namespace Iris.Server.Delivery;

/// <summary>
/// The default <see cref="IDeliveryRateLimiter"/> (Phase 16.3): a per-peer sliding-window limiter. It
/// bounds how many deliveries a peer may receive per sliding window (default one minute) by recording a
/// timestamp for every allowed delivery and waiting until the oldest of the peer's last <c>maxRequests</c>
/// timestamps falls out of the window.
/// </summary>
/// <remarks>
/// <strong>Peer key.</strong> The limiter keys on the <em>host</em> of the inbox IRI (e.g.
/// <c>https://b.example/ap/v1/u/bob/inbox</c> → <c>b.example</c>) so all of a peer's inboxes (shared
/// inbox + per-actor inboxes) share a single outbound rate budget.
/// </remarks>
/// <remarks>
/// <strong>Thread safety.</strong> Multiple delivery tasks (up to
/// <see cref="DeliveryWorkerOptions.MaxConcurrentDeliveries"/> in flight) may call
/// <see cref="WaitUntilPermittedAsync"/> concurrently for the same or different peers. Each peer's
/// state is guarded by its own lock, so concurrent access is safe and a peer never admits more than
/// <c>maxRequests</c> deliveries inside a window. A peer whose window has room returns immediately
/// without blocking.
/// </remarks>
/// <remarks>
/// <strong>Disabled.</strong> When constructed with <c>maxRequests == 0</c> the limiter is a no-op:
/// <see cref="WaitUntilPermittedAsync"/> returns without waiting or recording anything. This keeps the
/// default behavior (no rate limit) unchanged for hosts that do not opt in.
/// </remarks>
public sealed class SlidingWindowDeliveryRateLimiter : IDeliveryRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, PeerWindow> _peers = new();

    /// <summary>
    /// Initializes a new sliding-window rate limiter.
    /// </summary>
    /// <param name="maxRequests">
    /// The maximum deliveries per peer per window. 0 disables the limiter (the no-op default).
    /// </param>
    /// <param name="window">
    /// The sliding-window length. Defaults to one minute. Must be positive when
    /// <paramref name="maxRequests"/> is greater than 0.
    /// </param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="maxRequests"/> is negative, or <paramref name="window"/> is not positive
    /// and the limiter is enabled.
    /// </exception>
    public SlidingWindowDeliveryRateLimiter(int maxRequests, TimeSpan? window = null)
    {
        if (maxRequests < 0)
        {
            throw new ArgumentException("maxRequests must be non-negative (0 disables the limiter).", nameof(maxRequests));
        }

        var effectiveWindow = window ?? TimeSpan.FromMinutes(1);
        if (maxRequests > 0 && effectiveWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException("window must be positive when the limiter is enabled.", nameof(window));
        }

        _maxRequests = maxRequests;
        _window = effectiveWindow;
    }

    /// <inheritdoc/>
    public async Task WaitUntilPermittedAsync(Iri inboxIri, CancellationToken ct)
    {
        if (_maxRequests == 0)
        {
            return; // disabled — no rate limit
        }

        var peerKey = PeerKey(inboxIri);
        var peer = _peers.GetOrAdd(peerKey, _ => new PeerWindow());

        // A peer's state is guarded by its own lock so concurrent delivery tasks (up to the
        // MaxConcurrentDeliveries cap) cannot admit more than _maxRequests deliveries per window.
        await peer.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Drop timestamps that have fallen out of the sliding window (they no longer count against
            // the budget).
            peer.Expire(now, _window);

            if (peer.Count < _maxRequests)
            {
                // Room in the window: record this delivery and go.
                peer.Add(now);
                return;
            }

            // Full: the earliest timestamp is the next to expire. Wait until it leaves the window, then
            // record this delivery. (After the wait, re-check against the fresh clock so a slightly-early
            // wake does not admit a delivery that would push the window over the limit.)
            var earliest = peer.Oldest();
            var releaseAt = earliest + _window;
            var wait = releaseAt - now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            now = DateTimeOffset.UtcNow;
            peer.Expire(now, _window);
            // The earliest timestamp has (by construction) now expired, freeing exactly one slot.
            peer.Add(now);
        }
        finally
        {
            peer.Lock.Release();
        }
    }

    /// <summary>
    /// The peer key for an inbox IRI: the host of the IRI (lower-cased, per RFC 3986 host case-insensitivity).
    /// A relative IRI (no scheme/host) is keyed by its full value so it still gets a distinct (and stable)
    /// budget.
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
    /// A peer's sliding window: an ordered list of delivery timestamps (oldest first) guarded by a
    /// semaphore so concurrent waiters serialize their check-then-record.
    /// </summary>
    private sealed class PeerWindow
    {
        private readonly List<DateTimeOffset> _timestamps = new();

        /// <summary>
        /// The per-peer lock. Held for the short critical section (expire + check + add) and, when the
        /// window is full, across the blocking wait — which is correct here because no other waiter can
        /// make progress until this one admits (or the window frees a slot).
        /// </summary>
        public SemaphoreSlim Lock { get; } = new(1, 1);

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
        /// The oldest timestamp in the window (the next to expire). Valid only when <see cref="Count"/>
        /// is greater than 0.
        /// </summary>
        public DateTimeOffset Oldest() => _timestamps[0];

        /// <summary>
        /// Records a delivery at <paramref name="timestamp"/>.
        /// </summary>
        public void Add(DateTimeOffset timestamp) => _timestamps.Add(timestamp);
    }
}
