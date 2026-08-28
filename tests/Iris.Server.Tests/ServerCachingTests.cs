using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 3 unit tests: the server-side read-through cache engine (<see cref="CachingReadThrough{TValue}"/>)
/// and the concrete server caches (remote actor, remote key, collection page, WebFinger) plus the local
/// actor document cache. These verify the TTL / stale-while-revalidate / <c>forceRefresh</c> semantics
/// that back the server's <c>?refresh=true</c> bypass and <c>Cache-Control</c> behavior.
/// </summary>
/// <remarks>
/// A deterministic clock (a mutable <c>DateTime</c> advanced by the test) drives TTL/staleness so no real
/// sleeps are needed. The factory is a counting delegate so hit/miss/refresh behavior is observable.
/// </remarks>
public class ServerCachingTests
{
    // --- CachingReadThrough engine ---------------------------------------------

    [Fact]
    public async Task Engine_Miss_FetchesAndStores()
    {
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        var sut = new CachingReadThrough<string>(cache);
        var factoryCalls = 0;

        var (value, wasStale, wasHit) = await sut.GetAsync(
            new Iri("https://x.test/actor"),
            bypassCache: false,
            _ =>
            {
                factoryCalls++;
                return Task.FromResult<string?>("doc");
            });

        Assert.Equal("doc", value);
        Assert.False(wasStale);
        Assert.False(wasHit);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Engine_FreshHit_DoesNotFetch()
    {
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        var sut = new CachingReadThrough<string>(cache);
        var key = new Iri("https://x.test/actor");
        var factoryCalls = 0;

        _ = await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("doc");
        });

        // Still fresh (within 5 minutes) → served from cache, no factory call.
        var (value, wasStale, wasHit) = await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("doc2");
        });

        Assert.Equal("doc", value);
        Assert.False(wasStale);
        Assert.True(wasHit);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task Engine_StaleHit_ServesStaleAndRefreshes()
    {
        // CachePolicy semantics: Ttl=5min fresh, StaleFor=10min total usable window → the stale
        // window is 5–10 minutes. The engine reads with DateTime.UtcNow, so seed an entry stamped
        // 7 minutes in the past → past fresh (5), within the usable window (10) → stale.
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)));
        var sut = new CachingReadThrough<string>(cache);
        var key = new Iri("https://x.test/actor");
        var factoryCalls = 0;

        cache.Put(key, "stale-doc", DateTime.UtcNow.AddMinutes(-7));

        var (value, wasStale, wasHit) = await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("fresh-doc");
        });

        // Served the stale value immediately, flagged stale, and refreshed (factory called).
        Assert.Equal("stale-doc", value);
        Assert.True(wasStale);
        Assert.True(wasHit);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task Engine_ForceRefresh_SkipsCacheAndWritesBack()
    {
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        var sut = new CachingReadThrough<string>(cache);
        var key = new Iri("https://x.test/actor");
        var factoryCalls = 0;

        _ = await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("v1");
        });

        // forceRefresh=true → factory consulted even though a fresh entry exists.
        var (value, wasStale, wasHit) = await sut.GetAsync(key, bypassCache: true, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("v2");
        });

        Assert.Equal("v2", value);
        Assert.False(wasStale);
        Assert.False(wasHit); // forced refresh is a "miss" (factory served it)
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Engine_AbsentResult_IsNotCached()
    {
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        var sut = new CachingReadThrough<string>(cache);
        var key = new Iri("https://x.test/missing");

        var (value, _, wasHit) = await sut.GetAsync(key, false, _ => Task.FromResult<string?>(null));

        Assert.Null(value);
        Assert.False(wasHit);
        Assert.Equal(0, cache.Count); // absent results are never cached
    }

    [Fact]
    public async Task Engine_Invalidate_RemovesEntry()
    {
        var cache = new MemoryCache<string>(CachePolicy.Create(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        var sut = new CachingReadThrough<string>(cache);
        var key = new Iri("https://x.test/actor");
        var factoryCalls = 0;

        _ = await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("doc");
        });

        Assert.True(sut.Invalidate(key));

        // After invalidation, the next read is a miss → factory called again.
        await sut.GetAsync(key, false, _ =>
        {
            factoryCalls++;
            return Task.FromResult<string?>("doc");
        });
        Assert.Equal(2, factoryCalls);
    }

    // --- Concrete server caches ------------------------------------------------

    [Fact]
    public async Task RemoteActorCache_ReadsThroughWithDefaultPolicy()
    {
        var sut = new RemoteActorCache();
        // Server-side default: 1 hour fresh (distinct from the client's 5-minute default).
        Assert.Equal(TimeSpan.FromHours(1), sut.Policy.Ttl);
        Assert.Equal(TimeSpan.FromHours(1), sut.Policy.StaleFor);

        var factoryCalls = 0;
        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("https://remote.test/actor"),
            false,
            _ =>
            {
                factoryCalls++;
                return Task.FromResult<IObject?>(new Person { Id = "https://remote.test/actor" });
            });

        Assert.NotNull(value);
        Assert.False(wasHit);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task RemoteKeyCache_ReadsThroughWithDefaultPolicy()
    {
        var sut = new RemoteKeyCache();
        Assert.Equal(CachePolicy.Key.Ttl, sut.Policy.Ttl);

        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("https://remote.test/actor#key-1"),
            false,
            _ => Task.FromResult<Iris.Client.JwkKey?>(
                new Iris.Client.JwkKey(
                    Jwk: "{\"kty\":\"OK\",\"crv\":\"P-256\"}",
                    AlgorithmLabel: "ecdsa-p256")));

        Assert.NotNull(value);
        Assert.Equal("ecdsa-p256", value!.AlgorithmLabel);
        Assert.False(wasHit);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task CollectionPageCache_ReadsThroughWithDefaultPolicy()
    {
        var sut = new CollectionPageCache();
        Assert.Equal(CachePolicy.CollectionPage.Ttl, sut.Policy.Ttl);
        Assert.Equal(TimeSpan.FromSeconds(30), sut.Policy.Ttl);

        var page = new Iris.Core.CollectionPage
        {
            Page = new OrderedCollectionPage(),
            Items = [],
        };
        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("https://remote.test/actor/outbox?page=1"),
            false,
            _ => Task.FromResult<Iris.Core.CollectionPage?>(page));

        Assert.Same(page, value);
        Assert.False(wasHit);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task WebFingerCache_ReadsThroughWithDefaultPolicy()
    {
        var sut = new WebFingerCache();
        Assert.Equal(CachePolicy.WebFinger.Ttl, sut.Policy.Ttl);

        var hit = new Iris.Client.WebFingerHit(
            new Iri("acct:bob@remote.test"),
            new Iri("https://remote.test/actor"));
        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("acct:bob@remote.test"),
            false,
            _ => Task.FromResult<Iris.Client.WebFingerHit?>(hit));

        Assert.Equal(hit, value);
        Assert.False(wasHit);
        Assert.Equal(1, sut.Count);
    }

    // --- LocalActorDocumentCache -----------------------------------------------

    [Fact]
    public async Task LocalActorDocumentCache_DefaultPolicyMatchesArchitecture()
    {
        var sut = new LocalActorDocumentCache();
        // ARCHITECTURE.md: max-age=60, stale-while-revalidate=300.
        Assert.Equal(TimeSpan.FromSeconds(60), sut.Policy.Ttl);
        Assert.Equal(TimeSpan.FromSeconds(300), sut.Policy.StaleFor);

        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("https://x.test/u/alice"),
            false,
            _ => Task.FromResult<string?>("{\"id\":\"https://x.test/u/alice\"}"));

        Assert.NotNull(value);
        Assert.False(wasHit);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task LocalActorDocumentCache_ForceRefreshRefetches()
    {
        var sut = new LocalActorDocumentCache();
        var key = new Iri("https://x.test/u/alice");
        var calls = 0;

        _ = await sut.GetAsync(key, false, _ =>
        {
            calls++;
            return Task.FromResult<string?>("v1");
        });
        var (value, _, wasHit) = await sut.GetAsync(key, forceRefresh: true, _ =>
        {
            calls++;
            return Task.FromResult<string?>("v2");
        });

        Assert.Equal("v2", value);
        Assert.False(wasHit);
        Assert.Equal(2, calls);
    }
}
