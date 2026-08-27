using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="CacheEntry{TValue}"/> and <see cref="CachePolicy"/>.
/// </summary>
public class CacheEntryTests
{
    private static readonly DateTime T0 = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GetState_WithinTtl_IsFresh()
    {
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(CacheState.Fresh, entry.GetState(T0.AddMinutes(4)));
    }

    [Fact]
    public void GetState_AtExactlyTtlBoundary_IsFresh()
    {
        // now == FreshUntilUtc is still fresh (<= comparison).
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(CacheState.Fresh, entry.GetState(T0.AddMinutes(5)));
    }

    [Fact]
    public void GetState_PastTtlWithinStale_IsStale()
    {
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(CacheState.Stale, entry.GetState(T0.AddMinutes(7)));
    }

    [Fact]
    public void GetState_AtExactlyStaleBoundary_IsStale()
    {
        // now == UsableUntilUtc is still stale (<= comparison).
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(CacheState.Stale, entry.GetState(T0.AddMinutes(10)));
    }

    [Fact]
    public void GetState_PastStale_IsExpired()
    {
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(CacheState.Expired, entry.GetState(T0.AddMinutes(11)));
    }

    [Fact]
    public void FreshUntilAndUsableUntil_DerivedFromCreatedAt()
    {
        var entry = new CacheEntry<int>(1, T0, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        Assert.Equal(T0.AddMinutes(5), entry.FreshUntilUtc);
        Assert.Equal(T0.AddHours(1), entry.UsableUntilUtc);
    }

    [Fact]
    public void With_ReplacesValueAndTimestamp_KeepsPolicy()
    {
        var entry = new CacheEntry<string>("v", T0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var now = T0.AddMinutes(20);
        var updated = entry.With("v2", now);

        Assert.Equal("v2", updated.Value);
        Assert.Equal(now, updated.CreatedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), updated.Ttl);
        Assert.Equal(TimeSpan.FromMinutes(10), updated.StaleFor);
    }

    [Fact]
    public void CachePolicy_Create_WithNonPositiveTtl_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Create(TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void CachePolicy_Create_WithNonPositiveStale_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Fact]
    public void CachePolicy_Defaults_MatchArchitecture()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CachePolicy.Actor.Ttl);
        Assert.Equal(TimeSpan.FromSeconds(30), CachePolicy.CollectionPage.Ttl);
        Assert.Equal(TimeSpan.FromHours(1), CachePolicy.Key.Ttl);
        Assert.Equal(TimeSpan.FromMinutes(15), CachePolicy.WebFinger.Ttl);
    }
}
