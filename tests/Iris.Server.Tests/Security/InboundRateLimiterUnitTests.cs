namespace Iris.Server.Tests.Security;

/// <summary>
/// Phase 17.4 unit tests for the <see cref="SlidingWindowInboundRateLimiter"/>: a per-peer
/// sliding-window limiter that bounds how many signed inbox POSTs a peer may make per sliding
/// minute. A peer that exceeds its budget is rejected (429) — fail-fast, not blocking.
/// </summary>
public sealed class InboundRateLimiterUnitTests
{
    // --- A disabled limiter (maxRequests 0) always permits ---------------------------------

    [Fact]
    public void DisabledLimiter_AlwaysPermits()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 0);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        }
    }

    [Fact]
    public void DisabledLimiter_DoesNotTrackState()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 0);

        // Record many requests — a disabled limiter ignores them.
        for (var i = 0; i < 100; i++)
        {
            limiter.TryAcquire("remote.example.org", CancellationToken.None);
        }

        // Still permitted (no state was tracked).
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
    }

    [Fact]
    public void Limiter_ThrowsOnNegativeMaxRequests()
    {
        Assert.Throws<ArgumentException>(() => new SlidingWindowInboundRateLimiter(-1));
    }

    [Fact]
    public void Limiter_ThrowsOnNonPositiveWindow_WhenEnabled()
    {
        Assert.Throws<ArgumentException>(() => new SlidingWindowInboundRateLimiter(10, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new SlidingWindowInboundRateLimiter(10, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Limiter_ThrowsOnNullOrEmptyHost()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 10);

        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(string.Empty, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire("   ", CancellationToken.None));
    }

    // --- Enabled limiter: permits up to maxRequests per window -----------------------------

    [Fact]
    public void EnabledLimiter_PermitsUpToMaxRequests()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 5, window: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        }

        // The 6th request exceeds the budget: rejected.
        Assert.False(limiter.TryAcquire("remote.example.org", CancellationToken.None));
    }

    [Fact]
    public void EnabledLimiter_RejectsBeyondMaxRequests()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 3, window: TimeSpan.FromMinutes(1));

        // 3 requests: all permitted.
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));

        // 4th, 5th: rejected (budget exhausted).
        Assert.False(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        Assert.False(limiter.TryAcquire("remote.example.org", CancellationToken.None));
    }

    // --- Per-peer isolation -----------------------------------------------------------------

    [Fact]
    public void EnabledLimiter_DifferentPeers_AreIndependent()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 2, window: TimeSpan.FromMinutes(1));

        // Peer A: 2 requests (budget exhausted).
        Assert.True(limiter.TryAcquire("a.example.org", CancellationToken.None));
        Assert.True(limiter.TryAcquire("a.example.org", CancellationToken.None));
        Assert.False(limiter.TryAcquire("a.example.org", CancellationToken.None));

        // Peer B: unaffected (its own budget).
        Assert.True(limiter.TryAcquire("b.example.org", CancellationToken.None));
        Assert.True(limiter.TryAcquire("b.example.org", CancellationToken.None));
        Assert.False(limiter.TryAcquire("b.example.org", CancellationToken.None));
    }

    // --- Case-insensitive host keying -------------------------------------------------------

    [Fact]
    public void EnabledLimiter_HostIsCaseInsensitive()
    {
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 2, window: TimeSpan.FromMinutes(1));

        // Same host, different case: should share the same budget.
        Assert.True(limiter.TryAcquire("Remote.Example.ORG", CancellationToken.None));
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));

        // Budget exhausted (2 requests from the same host, different case).
        Assert.False(limiter.TryAcquire("REMOTE.EXAMPLE.ORG", CancellationToken.None));
    }

    // --- Window expiry ----------------------------------------------------------------------

    [Fact]
    public async Task EnabledLimiter_WindowExpires_AllowsNewRequests()
    {
        // Window of 100ms: requests older than 100ms fall out of the window.
        var limiter = new SlidingWindowInboundRateLimiter(maxRequests: 2, window: TimeSpan.FromMilliseconds(100));

        // 2 requests (budget exhausted).
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
        Assert.False(limiter.TryAcquire("remote.example.org", CancellationToken.None));

        // Wait for the window to expire.
        await Task.Delay(150);

        // Budget is now free: a new request is permitted.
        Assert.True(limiter.TryAcquire("remote.example.org", CancellationToken.None));
    }
}
