using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Caching;

/// <summary>
/// Phase 3 unit tests for the concrete server caches (remote actor, remote key, collection page,
/// WebFinger) and the local actor document cache: their default policies and read-through behavior
/// (the TTL / stale-while-revalidate / <c>bypassCache</c> semantics that back the server's
/// <c>?refresh=true</c> bypass and <c>Cache-Control</c> behavior). The shared
/// <see cref="CachingReadThrough{TValue}"/> engine they build on is tested once in
/// <c>Iris.Core.Tests.Caching.CachingReadThroughTests</c>, so it is not re-tested here.
/// </summary>
public class ServerCachingTests
{
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

        var page = new Iris.Core.Collections.CollectionPage
        {
            Page = new OrderedCollectionPage(),
            Items = [],
        };
        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("https://remote.test/actor/outbox?page=1"),
            false,
            _ => Task.FromResult<Iris.Core.Collections.CollectionPage?>(page));

        Assert.Same(page, value);
        Assert.False(wasHit);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task WebFingerCache_ReadsThroughWithDefaultPolicy()
    {
        var sut = new WebFingerCache();
        Assert.Equal(CachePolicy.WebFinger.Ttl, sut.Policy.Ttl);

        var hit = new Iris.Client.Discovery.WebFingerHit(
            new Iri("acct:bob@remote.test"),
            new Iri("https://remote.test/actor"));
        var (value, _, wasHit) = await sut.GetAsync(
            new Iri("acct:bob@remote.test"),
            false,
            _ => Task.FromResult<Iris.Client.Discovery.WebFingerHit?>(hit));

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
        var (value, _, wasHit) = await sut.GetAsync(key, bypassCache: true, _ =>
        {
            calls++;
            return Task.FromResult<string?>("v2");
        });

        Assert.Equal("v2", value);
        Assert.False(wasHit);
        Assert.Equal(2, calls);
    }
}
