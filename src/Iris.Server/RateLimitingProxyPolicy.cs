using Iris.Core;

namespace Iris.Server;

/// <summary>
/// An <see cref="IProxyTargetPolicy"/> that bounds how often a single actor may use the proxy
/// (a fixed per-actor per-minute rate limit).
/// </summary>
/// <remarks>
/// The limit is a simple in-memory counter per actor IRI (no sliding window): the first
/// <see cref="_maxPerMinute"/> proxy requests an actor makes within any rolling minute are allowed,
/// and the rest are rejected until the actor's count expires (one minute after its first recorded
/// request). The counters are per-<c>actorIri</c>, so one actor's traffic does not exhaust another
/// actor's budget. This is a coarse, in-process bound (sufficient for v1); a production host may
/// replace the policy with a distributed rate limiter. Counters are pruned opportunistically on each
/// check (expired entries are dropped), so idle actors do not accumulate state.
/// </remarks>
public sealed class RateLimitingProxyPolicy : IProxyTargetPolicy
{
    private readonly int _maxPerMinute;
    private readonly Dictionary<Iri, (int Count, DateTime FirstUtc)> _counters = new();

    /// <summary>
    /// Initializes a new rate-limiting policy.
    /// </summary>
    /// <param name="maxPerMinute">The maximum number of proxy requests a single actor may make per
    /// minute. Must be positive.</param>
    public RateLimitingProxyPolicy(int maxPerMinute)
    {
        if (maxPerMinute <= 0)
        {
            throw new ArgumentException("The rate limit must be positive.", nameof(maxPerMinute));
        }

        _maxPerMinute = maxPerMinute;
    }

    /// <inheritdoc/>
    public Task<bool> TryAuthorizeAsync(Iri actorIri, Iri target, out string? reason, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        lock (_counters)
        {
            PruneLocked(now);

            if (_counters.TryGetValue(actorIri, out var entry)
                && now - entry.FirstUtc < TimeSpan.FromMinutes(1))
            {
                if (entry.Count >= _maxPerMinute)
                {
                    reason = $"Proxy rate limit exceeded ({_maxPerMinute} requests per minute).";
                    return Task.FromResult(false);
                }

                _counters[actorIri] = (entry.Count + 1, entry.FirstUtc);
                reason = null;
                return Task.FromResult(true);
            }

            _counters[actorIri] = (1, now);
            reason = null;
            return Task.FromResult(true);
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
