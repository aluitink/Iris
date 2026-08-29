using Iris.Core;

namespace Iris.Core.Tests.Caching;

/// <summary>
/// Unit tests for <see cref="CachingReadThrough{TValue}"/> — the shared async read-through cache
/// engine in Iris.Core (previously duplicated as CachingClientCache and CachingServerCache).
/// Covers the passthrough members (Count, Invalidate) and the WasHit discriminant that the
/// client/server façade tests do not assert directly.
/// </summary>
public class CachingReadThroughTests
{
    private static Iri Key(string s) => new($"https://a.domain.local/k/{s}");

    [Fact]
    public async Task Count_ReflectsUnderlyingCache()
    {
        var store = new MemoryCache<string>(CachePolicy.Actor);
        var sut = new CachingReadThrough<string>(store);

        Assert.Equal(0, sut.Count);

        await sut.GetAsync(Key("a"), bypassCache: false, _ => Task.FromResult<string?>("v"));
        Assert.Equal(1, sut.Count);

        await sut.GetAsync(Key("b"), bypassCache: false, _ => Task.FromResult<string?>("w"));
        Assert.Equal(2, sut.Count);
    }

    [Fact]
    public async Task Invalidate_RemovesEntry()
    {
        var store = new MemoryCache<string>(CachePolicy.Actor);
        var sut = new CachingReadThrough<string>(store);
        var calls = 0;

        await sut.GetAsync(Key("x"), bypassCache: false, _ =>
        {
            calls++;
            return Task.FromResult<string?>("v");
        });
        Assert.Equal(1, sut.Count);

        Assert.True(sut.Invalidate(Key("x")));
        Assert.Equal(0, sut.Count);

        // After invalidation, the next read is a miss → factory re-invoked.
        var (value, wasStale, wasHit) = await sut.GetAsync(Key("x"), bypassCache: false, _ =>
        {
            calls++;
            return Task.FromResult<string?>("v2");
        });
        Assert.Equal("v2", value);
        Assert.False(wasHit);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidate_MissingKey_ReturnsFalse()
    {
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor));

        Assert.False(sut.Invalidate(Key("missing")));
    }

    [Fact]
    public async Task GetAsync_WasHit_TrueOnFreshHit()
    {
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor));

        await sut.GetAsync(Key("h"), bypassCache: false, _ => Task.FromResult<string?>("v"));

        var (_, _, wasHit) = await sut.GetAsync(Key("h"), bypassCache: false, _ => Task.FromResult<string?>("never"));
        Assert.True(wasHit);
    }

    [Fact]
    public async Task GetAsync_WasHit_FalseOnBypass()
    {
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(CachePolicy.Actor));

        await sut.GetAsync(Key("b"), bypassCache: false, _ => Task.FromResult<string?>("v"));

        var (_, _, wasHit) = await sut.GetAsync(Key("b"), bypassCache: true, _ => Task.FromResult<string?>("v2"));
        Assert.False(wasHit);
    }

    [Fact]
    public async Task Policy_PassesThrough()
    {
        var policy = CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var sut = new CachingReadThrough<string>(new MemoryCache<string>(policy));

        Assert.Equal(policy, sut.Policy);
    }
}
