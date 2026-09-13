using Iris.Core;

namespace Iris.Core.Tests.Caching;

/// <summary>
/// Unit tests for <see cref="CacheMetrics"/> and <see cref="NullCacheMetrics"/> — the hit/miss
/// counter implementations, and the wiring of metrics into <see cref="CachingReadThrough{TValue}"/>.
/// </summary>
public class CacheMetricsTests
{
    private static Iri Key(string s) => new($"https://a.domain.local/k/{s}");

    [Fact]
    public void CacheMetrics_InitialCounters_AreZero()
    {
        var sut = new CacheMetrics();

        Assert.Equal(0, sut.Hits);
        Assert.Equal(0, sut.Misses);
        Assert.Equal(0, sut.StaleHits);
        Assert.Equal(0.0, sut.HitRate);
    }

    [Fact]
    public void CacheMetrics_RecordHit_IncrementsHits()
    {
        var sut = new CacheMetrics();

        sut.RecordHit();
        sut.RecordHit();

        Assert.Equal(2, sut.Hits);
        Assert.Equal(0, sut.Misses);
        Assert.Equal(0, sut.StaleHits);
        Assert.Equal(1.0, sut.HitRate);
    }

    [Fact]
    public void CacheMetrics_RecordMiss_IncrementsMisses()
    {
        var sut = new CacheMetrics();

        sut.RecordMiss();

        Assert.Equal(0, sut.Hits);
        Assert.Equal(1, sut.Misses);
        Assert.Equal(0.0, sut.HitRate);
    }

    [Fact]
    public void CacheMetrics_RecordStaleHit_IncrementsStaleHits()
    {
        var sut = new CacheMetrics();

        sut.RecordStaleHit();

        Assert.Equal(0, sut.Hits);
        Assert.Equal(0, sut.Misses);
        Assert.Equal(1, sut.StaleHits);
        Assert.Equal(0.0, sut.HitRate);
    }

    [Fact]
    public void CacheMetrics_HitRate_ComputesCorrectRatio()
    {
        var sut = new CacheMetrics();

        sut.RecordHit();
        sut.RecordHit();
        sut.RecordHit();
        sut.RecordMiss();

        Assert.Equal(0.75, sut.HitRate, 4);
    }

    [Fact]
    public void NullCacheMetrics_AllCounters_AreZero()
    {
        var sut = NullCacheMetrics.Instance;

        Assert.Equal(0, sut.Hits);
        Assert.Equal(0, sut.Misses);
        Assert.Equal(0, sut.StaleHits);
        Assert.Equal(0.0, sut.HitRate);
    }

    [Fact]
    public void NullCacheMetrics_RecordMethods_AreNoOp()
    {
        var sut = NullCacheMetrics.Instance;

        sut.RecordHit();
        sut.RecordMiss();
        sut.RecordStaleHit();

        Assert.Equal(0, sut.Hits);
        Assert.Equal(0, sut.Misses);
        Assert.Equal(0, sut.StaleHits);
    }

    [Fact]
    public async Task CachingReadThrough_FreshHit_RecordsHit()
    {
        var metrics = new CacheMetrics();
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor), metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v"));
        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v2"));

        Assert.Equal(1, metrics.Hits);
        Assert.Equal(1, metrics.Misses);
        Assert.Equal(0, metrics.StaleHits);
    }

    [Fact]
    public async Task CachingReadThrough_Miss_RecordsMiss()
    {
        var metrics = new CacheMetrics();
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor), metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v"));

        Assert.Equal(0, metrics.Hits);
        Assert.Equal(1, metrics.Misses);
    }

    [Fact]
    public async Task CachingReadThrough_NullFactoryResult_RecordsMissAndDoesNotCache()
    {
        var metrics = new CacheMetrics();
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor), metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>(null));
        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>(null));

        Assert.Equal(0, metrics.Hits);
        Assert.Equal(2, metrics.Misses);
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task CachingReadThrough_BypassCache_RecordsMiss()
    {
        var metrics = new CacheMetrics();
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor), metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v"));
        await sut.GetAsync(Key("a"), bypassCache: true, _ => Task.FromResult<string?>("v2"));

        // First read: miss. Second read (bypass): also a miss (bypass always calls the factory).
        Assert.Equal(2, metrics.Misses);
        Assert.Equal(0, metrics.Hits);
    }

    [Fact]
    public async Task CachingReadThrough_NoMetrics_UsesNullCacheMetrics()
    {
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor));

        Assert.IsType<NullCacheMetrics>(sut.Metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v"));

        Assert.Equal(0, sut.Metrics.Hits);
        Assert.Equal(0, sut.Metrics.Misses);
    }

    [Fact]
    public async Task CachingReadThrough_StaleHit_RecordsStaleHit()
    {
        // Use a very short TTL/stale window so the entry is stale within the test's real-time window.
        var policy = CachePolicy.Create(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
        var metrics = new CacheMetrics();
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(policy), metrics);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v1"));
        Assert.Equal(1, metrics.Misses);

        // Wait for the entry to become stale (TTL = 50 ms).
        await Task.Delay(100);

        var (value, wasStale, wasHit) = await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v2"));
        Assert.Equal("v1", value);
        Assert.True(wasStale);
        Assert.True(wasHit);

        Assert.Equal(1, metrics.StaleHits);
        Assert.Equal(1, metrics.Misses);
        Assert.Equal(0, metrics.Hits);
    }
}
