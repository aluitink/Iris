using Iris.Core;

namespace Iris.Server.Http.Proxy;

/// <summary>
/// Bounds how often a single client IP may use the anonymous proxy seam
/// (<c>GET /ap/v1/proxy/{target}</c>, a signed-out visitor's public remote reads).
/// </summary>
/// <remarks>
/// The anonymous seam has no actor identity (there is no Basic auth and no site cookie), so the
/// bound is keyed on the client IP instead of an actor IRI. The limit is a simple in-memory counter
/// per IP (no sliding window): the first <see cref="_maxPerMinute"/> anonymous proxy reads from an
/// IP within any rolling minute are allowed, and the rest are rejected until the counter expires
/// (one minute after its first recorded request). The counters are pruned opportunistically on each
/// check (expired entries are dropped), so idle clients do not accumulate state. This is a coarse,
/// in-process bound (sufficient for v1); a production host may replace the limiter with a
/// distributed one.
/// </remarks>
public sealed class AnonymousProxyRateLimiter
{
    private readonly int _maxPerMinute;
    private readonly Dictionary<string, (int Count, DateTime FirstUtc)> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new anonymous proxy rate limiter.
    /// </summary>
    /// <param name="maxPerMinute">The maximum number of anonymous proxy reads a single client IP may
    /// make per minute. Must be positive.</param>
    public AnonymousProxyRateLimiter(int maxPerMinute)
    {
        if (maxPerMinute <= 0)
        {
            throw new ArgumentException("The rate limit must be positive.", nameof(maxPerMinute));
        }

        _maxPerMinute = maxPerMinute;
    }

    /// <summary>
    /// Records an anonymous proxy read from <paramref name="clientIp"/> and reports whether it is
    /// within the per-minute budget.
    /// </summary>
    /// <param name="clientIp">The client IP to bound (the request's remote IP).</param>
    /// <param name="reason">A human-readable rejection reason when the read is over the budget;
    /// <see langword="null"/> when it is within the budget.</param>
    /// <returns><see langword="true"/> when the read is within the budget.</returns>
    public bool TryAllow(string clientIp, out string? reason)
    {
        var now = DateTime.UtcNow;
        lock (_counters)
        {
            PruneLocked(now);

            if (_counters.TryGetValue(clientIp, out var entry)
                && now - entry.FirstUtc < TimeSpan.FromMinutes(1))
            {
                if (entry.Count >= _maxPerMinute)
                {
                    reason = $"Anonymous proxy rate limit exceeded ({_maxPerMinute} requests per minute).";
                    return false;
                }

                _counters[clientIp] = (entry.Count + 1, entry.FirstUtc);
                reason = null;
                return true;
            }

            _counters[clientIp] = (1, now);
            reason = null;
            return true;
        }
    }

    /// <summary>
    /// Drops expired (older than one minute) counters. Call with <see cref="_counters"/> held.
    /// </summary>
    private void PruneLocked(DateTime now)
    {
        var expired = _counters
            .Where(kv => now - kv.Value.FirstUtc >= TimeSpan.FromMinutes(1))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _counters.Remove(key);
        }
    }
}
