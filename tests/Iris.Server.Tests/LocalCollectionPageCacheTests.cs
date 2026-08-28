using Iris.Core;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 unit tests: the <see cref="LocalCollectionPageCache"/> — the server → client response cache
/// for paged local collections (outbox/followers/following). Mirrors the <see cref="LocalActorDocumentCache"/>
/// semantics (miss → render + cache, fresh hit → no render, absent → not cached, forceRefresh → bypass read
/// + write back, invalidate). The stale-while-revalidate path is exercised at the
/// <see cref="CachingReadThrough{TValue}"/> engine level in <c>ServerCachingTests</c>; this suite covers the
/// concrete cache's observable behavior (hit/miss/absent/force/invalidate).
/// </summary>
public class LocalCollectionPageCacheTests
{
    private static readonly Iri Page = new Iri("https://a.test/ap/v1/u/alice/outbox/?page=1");

    [Fact]
    public async Task Miss_RendersAndCaches()
    {
        var sut = new LocalCollectionPageCache();
        var renderCalls = 0;

        var (value, wasStale, wasHit) = await sut.GetAsync(
            Page,
            forceRefresh: false,
            _ =>
            {
                renderCalls++;
                return Task.FromResult<string?>("{\"type\":\"OrderedCollection\"}");
            });

        Assert.Equal("{\"type\":\"OrderedCollection\"}", value);
        Assert.False(wasStale);
        Assert.False(wasHit);
        Assert.Equal(1, renderCalls);
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task FreshHit_DoesNotRender()
    {
        var sut = new LocalCollectionPageCache();
        var renderCalls = 0;

        _ = await sut.GetAsync(Page, false, _ =>
        {
            renderCalls++;
            return Task.FromResult<string?>("page-v1");
        });

        var (value, wasStale, wasHit) = await sut.GetAsync(Page, false, _ =>
        {
            renderCalls++;
            return Task.FromResult<string?>("page-v2");
        });

        Assert.Equal("page-v1", value);
        Assert.False(wasStale);
        Assert.True(wasHit);
        Assert.Equal(1, renderCalls);
    }

    [Fact]
    public async Task AbsentResult_NotCached()
    {
        var sut = new LocalCollectionPageCache();
        var renderCalls = 0;

        var (value, _, wasHit) = await sut.GetAsync(
            Page,
            false,
            _ =>
            {
                renderCalls++;
                return Task.FromResult<string?>(null);
            });

        Assert.Null(value);
        Assert.False(wasHit);
        Assert.Equal(1, renderCalls);
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task ForceRefresh_BypassesReadAndWritesBack()
    {
        var sut = new LocalCollectionPageCache();
        var renderCalls = 0;

        _ = await sut.GetAsync(Page, false, _ =>
        {
            renderCalls++;
            return Task.FromResult<string?>("v1");
        });

        var (value, wasStale, wasHit) = await sut.GetAsync(Page, true, _ =>
        {
            renderCalls++;
            return Task.FromResult<string?>("v2");
        });

        // The forced refresh bypassed the fresh entry (factory consulted → a miss for the read) and the
        // new value replaced the old one on the next plain read.
        Assert.Equal("v2", value);
        Assert.False(wasStale);
        Assert.False(wasHit);
        Assert.Equal(2, renderCalls);

        var (value2, _, _) = await sut.GetAsync(Page, false, _ =>
        {
            renderCalls++;
            return Task.FromResult<string?>("v3");
        });
        Assert.Equal("v2", value2);
        Assert.Equal(2, renderCalls);
    }

    [Fact]
    public async Task Invalidate_RemovesEntry()
    {
        var sut = new LocalCollectionPageCache();
        _ = await sut.GetAsync(Page, false, _ => Task.FromResult<string?>("v1"));
        Assert.Equal(1, sut.Count);

        var removed = sut.Invalidate(Page);
        Assert.True(removed);
        Assert.Equal(0, sut.Count);
    }
}
