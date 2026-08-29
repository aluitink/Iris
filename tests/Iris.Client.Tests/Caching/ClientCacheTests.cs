using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests.Caching;

/// <summary>
/// Unit tests for the four concrete client caches (<see cref="ActorCache"/>, <see cref="CollectionPageCache"/>,
/// <see cref="WebFingerCache"/>, <see cref="KeyCache"/>): their default policies and read-through
/// (miss→store, hit, and "absent is not cached") behavior. The shared
/// <see cref="CachingReadThrough{TValue}"/> engine they build on is tested once in
/// <c>Iris.Core.Tests.Caching.CachingReadThroughTests</c>, so it is not re-tested here.
/// </summary>
public class ClientCacheTests
{
    private static Iri Key(string s) => new($"https://a.domain.local/k/{s}");

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
