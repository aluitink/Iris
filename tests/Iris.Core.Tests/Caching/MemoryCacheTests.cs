using Iris.Core;

namespace Iris.Core.Tests.Caching;

/// <summary>
/// Unit tests for <see cref="MemoryCache{TValue}"/> (implements <see cref="ICache{TValue}"/>):
/// TTL, LRU eviction, stale-while-revalidate, and invalidation.
/// </summary>
public class MemoryCacheTests
{
    private static readonly DateTime T0 = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CachePolicy Policy = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private static Iri Key(string s) => new($"https://a.domain.local/k/{s}");

    [Fact]
    public void PutThenGet_ReturnsValue_Fresh()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v", T0);

        var result = cache.TryGetEntry(Key("a"), T0.AddSeconds(1));
        Assert.NotNull(result);
        Assert.Equal("v", result!.Value.Entry.Value);
        Assert.Equal(CacheState.Fresh, result.Value.State);
    }

    [Fact]
    public void Get_Miss_ReturnsNull()
    {
        var cache = new MemoryCache<string>(Policy);
        Assert.Null(cache.Get(Key("absent"), T0));
        Assert.Null(cache.TryGetEntry(Key("absent"), T0));
    }

    [Fact]
    public void Get_PastStaleWindow_ReturnsNull_AndEvicts()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v", T0);

        // 11 minutes later: past the 10-minute stale window → expired.
        Assert.Null(cache.Get(Key("a"), T0.AddMinutes(11)));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Get_PastTtlWithinStale_ReturnsValue_Stale()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v", T0);

        // 7 minutes later: past the 5-minute TTL, within the 10-minute stale window.
        var result = cache.TryGetEntry(Key("a"), T0.AddMinutes(7));
        Assert.NotNull(result);
        Assert.Equal("v", result!.Value.Entry.Value);
        Assert.Equal(CacheState.Stale, result.Value.State);
    }

    [Fact]
    public void Put_OverwritesExistingEntry()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v1", T0);
        cache.Put(Key("a"), "v2", T0.AddMinutes(1));

        Assert.Equal(1, cache.Count);
        Assert.Equal("v2", cache.Get(Key("a"), T0.AddMinutes(2)));
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v", T0);

        Assert.True(cache.Invalidate(Key("a")));
        Assert.Null(cache.Get(Key("a"), T0));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.Invalidate(Key("a")));
    }

    [Fact]
    public void LruEviction_EvictsLeastRecentlyUsed_First()
    {
        var cache = new MemoryCache<string>(Policy, capacity: 3);
        var k1 = Key("1");
        var k2 = Key("2");
        var k3 = Key("3");
        var k4 = Key("4");
        cache.Put(k1, "a", T0);
        cache.Put(k2, "b", T0);
        cache.Put(k3, "c", T0);

        // Touch k1 so k2 becomes the least-recently-used.
        _ = cache.Get(k1, T0);

        // Add a 4th → evicts k2 (LRU), keeps k1 (recently used) and k3.
        cache.Put(k4, "d", T0);
        Assert.Equal(3, cache.Count);
        Assert.Null(cache.Get(k2, T0));
        Assert.Equal("a", cache.Get(k1, T0));
        Assert.Equal("c", cache.Get(k3, T0));
        Assert.Equal("d", cache.Get(k4, T0));
    }

    [Fact]
    public void Count_ReflectsInsertionsAndEvictions()
    {
        var cache = new MemoryCache<int>(Policy, capacity: 2);
        Assert.Equal(0, cache.Count);
        cache.Put(Key("a"), 1, T0);
        cache.Put(Key("b"), 2, T0);
        Assert.Equal(2, cache.Count);
        cache.Put(Key("c"), 3, T0); // evicts "a"
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryGetEntry(Key("a"), T0));
    }

    [Fact]
    public void ExpiredEntries_AreEvictedOnWrite()
    {
        var cache = new MemoryCache<string>(Policy, capacity: 10);
        cache.Put(Key("old"), "v", T0);
        cache.Put(Key("new"), "w", T0.AddMinutes(11));

        // "old" expired (11 min > 10 min stale); a write should opportunistically evict it.
        Assert.Equal(1, cache.Count);
        Assert.Null(cache.Get(Key("old"), T0.AddMinutes(11)));
        Assert.Equal("w", cache.Get(Key("new"), T0.AddMinutes(11)));
    }

    [Fact]
    public void GetOrAdd_Hit_ReturnsCached()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "cached", T0);

        var (value, wasHit) = cache.GetOrAdd(Key("a"), () => "fresh", T0.AddSeconds(1));
        Assert.Equal("cached", value);
        Assert.True(wasHit);
    }

    [Fact]
    public void GetOrAdd_Miss_InvokesFactoryAndStores()
    {
        var cache = new MemoryCache<string>(Policy);
        var invocations = 0;

        var (value, wasHit) = cache.GetOrAdd(Key("a"), () => { invocations++; return "fresh"; }, T0);
        Assert.Equal("fresh", value);
        Assert.False(wasHit);
        Assert.Equal(1, invocations);
        Assert.Equal(1, cache.Count);

        // Subsequent GetOrAdd is a hit and does not re-invoke the factory.
        var (value2, wasHit2) = cache.GetOrAdd(Key("a"), () => { invocations++; return "again"; }, T0.AddSeconds(1));
        Assert.Equal("fresh", value2);
        Assert.True(wasHit2);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void Lookup_ReturnsCachedValue_WithState()
    {
        var cache = new MemoryCache<string>(Policy);
        cache.Put(Key("a"), "v", T0);

        var fresh = cache.Lookup(Key("a"), T0);
        Assert.True(fresh.Hit);
        Assert.True(fresh.IsFresh);
        Assert.Equal("v", fresh.Value);

        var stale = cache.Lookup(Key("a"), T0.AddMinutes(7));
        Assert.True(stale.Hit);
        Assert.True(stale.IsStale);
        Assert.Equal("v", stale.Value);
    }
}
