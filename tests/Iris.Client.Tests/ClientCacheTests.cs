using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for the client caching layer: <see cref="CachingClientCache{TValue}"/> (the generic
/// engine) and the four concrete caches (<see cref="ActorCache"/>, <see cref="CollectionPageCache"/>,
/// <see cref="WebFingerCache"/>, <see cref="KeyCache"/>). Covers fresh hit, miss→store,
/// stale-while-revalidate, bypass, and "absent is not cached".
/// </summary>
public class ClientCacheTests
{
    private static Iri Key(string s) => new($"https://a.domain.local/k/{s}");

    // --- CachingClientCache<TValue> (generic engine) --------------------------

    [Fact]
    public async Task GetAsync_Miss_FetchesAndStores()
    {
        var cache = new CachingClientCache<string>(new MemoryCache<string>(CachePolicy.Actor));
        var calls = 0;

        async Task<string?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return "v";
        }

        var (value, wasStale) = await cache.GetAsync(Key("a"), bypassCache: false, Factory);
        Assert.Equal("v", value);
        Assert.False(wasStale);
        Assert.Equal(1, calls);

        // Second call is served from cache; factory not invoked.
        var (value2, wasStale2) = await cache.GetAsync(Key("a"), bypassCache: false, Factory);
        Assert.Equal("v", value2);
        Assert.False(wasStale2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAsync_Bypass_SkipsReadButWritesBack()
    {
        var cache = new CachingClientCache<string>(new MemoryCache<string>(CachePolicy.Actor));

        // Seed the cache via the first (non-bypass) call.
        async Task<string?> SeedFactory(Iri _)
        {
            await Task.Yield();
            return "cached";
        }

        await cache.GetAsync(Key("c"), bypassCache: false, SeedFactory);

        // Now bypass: the factory is consulted (ignores the cached value) and its result is written back.
        var calls = 0;
        async Task<string?> BypassFactory(Iri _)
        {
            calls++;
            await Task.Yield();
            return "fetched";
        }

        var (value, wasStale) = await cache.GetAsync(Key("c"), bypassCache: true, BypassFactory);
        Assert.Equal("fetched", value);
        Assert.False(wasStale);
        Assert.Equal(1, calls);

        // Subsequent non-bypass read gets the written-back value without re-fetching.
        var (value2, _) = await cache.GetAsync(Key("c"), bypassCache: false, BypassFactory);
        Assert.Equal("fetched", value2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAsync_NullResult_NotCached()
    {
        var cache = new CachingClientCache<string>(new MemoryCache<string>(CachePolicy.Actor));
        var calls = 0;

        async Task<string?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return null;
        }

        var (value, wasStale) = await cache.GetAsync(Key("d"), bypassCache: false, Factory);
        Assert.Null(value);
        Assert.False(wasStale);
        Assert.Equal(1, calls);

        // A null result is not memoized → the next lookup retries the factory.
        var (value2, _) = await cache.GetAsync(Key("d"), bypassCache: false, Factory);
        Assert.Null(value2);
        Assert.Equal(2, calls);
    }

    // --- Stale-while-revalidate (uses a 1ms TTL so the first entry is stale on the 2nd read) ---

    [Fact]
    public async Task GetAsync_StaleHit_ServesStaleAndRefreshes()
    {
        // TTL = 50ms (stale after that), stale window = 10 minutes.
        var policy = CachePolicy.Create(TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(10));
        var cache = new CachingClientCache<string>(new MemoryCache<string>(policy));
        var calls = 0;

        async Task<string?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            // First call seeds; subsequent calls (stale refresh) return a new value.
            return calls == 1 ? "old" : "new";
        }

        // First call: miss → fetch "old" and store.
        var (value, wasStale) = await cache.GetAsync(Key("s"), bypassCache: false, Factory);
        Assert.Equal("old", value);
        Assert.False(wasStale);
        Assert.Equal(1, calls);

        // Delay so the 50ms TTL has elapsed → the entry is now stale (still within the 10-min stale window).
        await Task.Delay(300);

        // Second call: stale hit → serves "old" immediately and refreshes to "new".
        var (value2, wasStale2) = await cache.GetAsync(Key("s"), bypassCache: false, Factory);
        Assert.Equal("old", value2);
        Assert.True(wasStale2);
        Assert.Equal(2, calls);

        // Third call: now fresh (refreshed) → served from cache, no re-fetch.
        var (value3, wasStale3) = await cache.GetAsync(Key("s"), bypassCache: false, Factory);
        Assert.Equal("new", value3);
        Assert.False(wasStale3);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetAsync_StaleHit_RefreshYieldsNull_KeepsStale()
    {
        var policy = CachePolicy.Create(TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(10));
        var cache = new CachingClientCache<string>(new MemoryCache<string>(policy));
        var calls = 0;

        async Task<string?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            // First call seeds; stale refresh returns null (simulating a failed revalidation).
            return calls == 1 ? "stale-value" : null;
        }

        // Seed.
        await cache.GetAsync(Key("e"), bypassCache: false, Factory);
        await Task.Delay(300);

        // Stale hit: serves "stale-value"; refresh returns null → stale value is kept.
        var (value, wasStale) = await cache.GetAsync(Key("e"), bypassCache: false, Factory);
        Assert.Equal("stale-value", value);
        Assert.True(wasStale);
        Assert.Equal(2, calls);

        // Still present (stale kept), served again (still stale since the refresh was null).
        var (value2, wasStale2) = await cache.GetAsync(Key("e"), bypassCache: false, Factory);
        Assert.Equal("stale-value", value2);
        Assert.True(wasStale2);
        Assert.Equal(3, calls);
    }

    // --- ActorCache (IObject) --------------------------------------------------

    [Fact]
    public async Task ActorCache_MissThenHit()
    {
        var cache = new ActorCache();
        var actor = new Person { Id = "https://a.domain.local/u/alice", Name = ["Alice"] };
        var calls = 0;

        async Task<IObject?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return actor;
        }

        var (value, _) = await cache.GetAsync(Key("alice"), bypassCache: false, Factory);
        Assert.Same(actor, value);
        Assert.Equal(1, calls);

        var (value2, _) = await cache.GetAsync(Key("alice"), bypassCache: false, Factory);
        Assert.Same(actor, value2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ActorCache_DefaultPolicy_IsActor()
        => Assert.Equal(CachePolicy.Actor, new ActorCache().Policy);

    // --- CollectionPageCache (IObject) ----------------------------------------

    [Fact]
    public async Task CollectionPageCache_MissThenHit()
    {
        var cache = new CollectionPageCache();
        var page = new OrderedCollectionPage { Id = "https://a.domain.local/c/1", TotalItems = 10 };
        var calls = 0;

        async Task<IObject?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return page;
        }

        var (value, _) = await cache.GetAsync(Key("page-1"), bypassCache: false, Factory);
        Assert.Same(page, value);
        Assert.Equal(1, calls);

        var (value2, _) = await cache.GetAsync(Key("page-1"), bypassCache: false, Factory);
        Assert.Same(page, value2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CollectionPageCache_DefaultPolicy_IsCollectionPage()
        => Assert.Equal(CachePolicy.CollectionPage, new CollectionPageCache().Policy);

    // --- WebFingerCache (Iri) -------------------------------------------------

    [Fact]
    public async Task WebFingerCache_MissThenHit()
    {
        var cache = new WebFingerCache();
        var account = new Iri("acct:bob@b.domain.local");
        var actorId = new Iri("https://b.domain.local/u/bob");
        var calls = 0;

        async Task<Iri?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return actorId;
        }

        var (value, _) = await cache.GetAsync(account, bypassCache: false, Factory);
        Assert.Equal(actorId, value);
        Assert.Equal(1, calls);

        var (value2, _) = await cache.GetAsync(account, bypassCache: false, Factory);
        Assert.Equal(actorId, value2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WebFingerCache_Unresolvable_NotCached()
    {
        var cache = new WebFingerCache();
        var account = new Iri("acct:nobody@b.domain.local");
        var calls = 0;

        async Task<Iri?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return null;
        }

        var (value, _) = await cache.GetAsync(account, bypassCache: false, Factory);
        Assert.Null(value);
        Assert.Equal(1, calls);

        // Retry (not memoized).
        var (value2, _) = await cache.GetAsync(account, bypassCache: false, Factory);
        Assert.Null(value2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WebFingerCache_DefaultPolicy_IsWebFinger()
        => Assert.Equal(CachePolicy.WebFinger, new WebFingerCache().Policy);

    // --- KeyCache (JwkKey) ----------------------------------------------------

    [Fact]
    public async Task KeyCache_MissThenHit()
    {
        var cache = new KeyCache();
        var keyId = new Iri("https://a.domain.local/u/alice#key-1");
        var jwk = new JwkKey("""{"kty":"RSA","n":"abc","e":"AQAB"}""", "rsa-sha256");
        var calls = 0;

        async Task<JwkKey?> Factory(Iri _)
        {
            calls++;
            await Task.Yield();
            return jwk;
        }

        var (value, _) = await cache.GetAsync(keyId, bypassCache: false, Factory);
        Assert.Same(jwk, value);
        Assert.Equal(1, calls);

        var (value2, _) = await cache.GetAsync(keyId, bypassCache: false, Factory);
        Assert.Same(jwk, value2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void KeyCache_DefaultPolicy_IsKey()
        => Assert.Equal(CachePolicy.Key, new KeyCache().Policy);

    [Fact]
    public void JwkKey_ToElement_Parses()
    {
        var jwk = new JwkKey("""{"kty":"EC","crv":"P-256","x":"xyz","y":"uvw"}""", "ecdsa-p256-sha256");
        var element = jwk.ToElement();
        Assert.Equal("EC", element.GetProperty("kty").GetString());
        Assert.Equal("P-256", element.GetProperty("crv").GetString());
    }
}
