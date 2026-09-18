using System.Collections.Concurrent;

namespace Iris.Web.Accounts;

/// <summary>
/// Bounds repeated failed login attempts (a different concern from the library's inbound-federation
/// rate limiter — which gates signed inbox POSTs by peer host, and is much higher throughput). This
/// one gates <em>login attempts</em>, keyed by <c>username + remote IP</c>, with a much lower
/// threshold (default 5 attempts / 15 minutes). Reuse of the library limiter's shape is deliberate;
/// this is a new, small component in <c>Iris.Web</c>, not a change to the library.
/// </summary>
public interface ILoginRateLimiter
{
    /// <summary>
    /// Records a failed attempt for the given key and returns whether further attempts are currently
    /// permitted (false when the key has exceeded its budget inside the window).
    /// </summary>
    /// <param name="key">The rate-limit key (<c>username + remote IP</c>).</param>
    bool RecordFailure(string key);

    /// <summary>
    /// Clears the failure count for a key (called on a successful login).
    /// </summary>
    /// <param name="key">The rate-limit key.</param>
    void Clear(string key);

    /// <summary>
    /// Returns whether the key is currently over budget (and thus should be rejected before even
    /// attempting to verify the password).
    /// </summary>
    /// <param name="key">The rate-limit key.</param>
    bool IsBlocked(string key);

    /// <summary>
    /// Returns when the key's window resets (for a <c>Retry-After</c> hint), or now if unblocked.
    /// </summary>
    /// <param name="key">The rate-limit key.</param>
    DateTimeOffset RetryAfter(string key);
}

/// <summary>
/// The default <see cref="ILoginRateLimiter"/>: a sliding-window counter keyed by
/// <c>username + remote IP</c>. Mirrors the shape of
/// <c>Iris.Server.Security.SlidingWindowInboundRateLimiter</c> (per-key lock, ordered timestamp list,
/// fail-fast reject) but with the login threshold.
/// </summary>
public sealed class SlidingWindowLoginRateLimiter : ILoginRateLimiter
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, KeyWindow> _keys = new();

    /// <summary>
    /// Initializes the limiter.
    /// </summary>
    /// <param name="maxAttempts">
    /// The maximum failed attempts per key per window. 0 disables the limiter (the no-op default).
    /// </param>
    /// <param name="window">The sliding-window length. Defaults to 15 minutes.</param>
    public SlidingWindowLoginRateLimiter(int maxAttempts, TimeSpan? window = null)
    {
        if (maxAttempts < 0)
        {
            throw new ArgumentException(
                "maxAttempts must be non-negative (0 disables the limiter).", nameof(maxAttempts));
        }

        var effectiveWindow = window ?? TimeSpan.FromMinutes(15);
        if (maxAttempts > 0 && effectiveWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "window must be positive when the limiter is enabled.", nameof(window));
        }

        _maxAttempts = maxAttempts;
        _window = effectiveWindow;
    }

    /// <inheritdoc/>
    public bool RecordFailure(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_maxAttempts == 0)
        {
            return true; // disabled
        }

        var state = _keys.GetOrAdd(key.ToLowerInvariant(), _ => new KeyWindow());
        lock (state.Lock)
        {
            state.Expire(DateTimeOffset.UtcNow, _window);
            state.Add(DateTimeOffset.UtcNow);
            // Further attempts are permitted while the recorded failures are strictly below the budget
            // (once the budget is hit, the next attempt is blocked — "N attempts per window" means the
            // (N+1)th is rejected).
            return state.Count < _maxAttempts;
        }
    }

    /// <inheritdoc/>
    public void Clear(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _keys.TryRemove(key.ToLowerInvariant(), out _);
    }

    /// <inheritdoc/>
    public bool IsBlocked(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_maxAttempts == 0)
        {
            return false;
        }

        var state = _keys.GetValueOrDefault(key.ToLowerInvariant());
        if (state is null)
        {
            return false;
        }

        lock (state.Lock)
        {
            state.Expire(DateTimeOffset.UtcNow, _window);
            // Blocked once the recorded failures reach the budget (the (N+1)th attempt is rejected).
            return state.Count >= _maxAttempts;
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset RetryAfter(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_maxAttempts == 0)
        {
            return DateTimeOffset.UtcNow;
        }

        var state = _keys.GetValueOrDefault(key.ToLowerInvariant());
        if (state is null || state.Count == 0)
        {
            return DateTimeOffset.UtcNow;
        }

        lock (state.Lock)
        {
            state.Expire(DateTimeOffset.UtcNow, _window);
            if (state.Count == 0)
            {
                return DateTimeOffset.UtcNow;
            }

            var resetTime = state.Oldest() + _window;
            var now = DateTimeOffset.UtcNow;
            return resetTime > now ? resetTime : now;
        }
    }

    private sealed class KeyWindow
    {
        private readonly List<DateTimeOffset> _timestamps = new();
        public object Lock { get; } = new();
        public int Count => _timestamps.Count;

        public void Expire(DateTimeOffset now, TimeSpan window)
        {
            var cutoff = now - window;
            while (_timestamps.Count > 0 && _timestamps[0] < cutoff)
            {
                _timestamps.RemoveAt(0);
            }
        }

        public void Add(DateTimeOffset timestamp) => _timestamps.Add(timestamp);
        public DateTimeOffset Oldest() => _timestamps[0];
    }
}
