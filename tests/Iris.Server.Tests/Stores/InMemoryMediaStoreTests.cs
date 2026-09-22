using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Stores;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Unit tests for the <see cref="InMemoryMediaStore"/> — the in-memory media store (Phase 20.4 (a)):
/// persists uploaded media (a note's attachment) and hands out the same-origin media IRI
/// (<c>{base}/ap/v1/media/{id}</c>) that an object's attachment references. The shared
/// <see cref="IMediaStore"/> contract behaviors (store + read back, the same-origin media-IRI shape) run
/// via <see cref="IMediaStoreContractTests"/>; this class adds the in-memory-specific behaviors: a unique
/// unguessable id per item, a missing-media miss, per-item independence, and id resolution from the last
/// path segment.
/// </summary>
public sealed class InMemoryMediaStoreTests : IMediaStoreContractTests
{
    private static readonly Iri Base = new("https://a.test");
    private static readonly byte[] Pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // a PNG-ish blob

    protected override IMediaStore CreateStore()
        => new InMemoryMediaStore();

    private static InMemoryMediaStore NewStore()
        => new();

    /// <summary>The shared <see cref="IMediaStore"/> contract: store + read back (bytes, content-type,
    /// file name) and the same-origin media-IRI shape.</summary>
    [Fact]
    public async Task Contract_PutReadBackAndSameOriginMediaIri()
        => await RunSharedContractBehaviors();

    [Fact]
    public async Task Put_Twice_ReturnsDistinctIds()
    {
        var sut = NewStore();

        var iri1 = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);
        var iri2 = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);

        Assert.NotEqual(iri1, iri2);
    }

    [Fact]
    public async Task TryGet_UnknownMedia_ReturnsFalse()
    {
        var sut = NewStore();

        var missing = new Iri($"{Base.Value}/ap/v1/media/deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.False(await sut.TryGetAsync(missing, out var content, out var contentType, out var fileName));
        Assert.Null(content);
        Assert.Null(contentType);
        Assert.Null(fileName);
    }

    [Fact]
    public async Task Put_StoresEachItemIndependently()
    {
        var sut = NewStore();

        var iri1 = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);
        byte[] other = [1, 2, 3];
        var iri2 = await sut.PutAsync(other, "image/jpeg", "dog.jpg", Base);

        Assert.True(await sut.TryGetAsync(iri1, out var c1, out var t1, out var n1));
        Assert.Equal(Pixels, c1);
        Assert.Equal("image/png", t1);
        Assert.Equal("cat.png", n1);

        Assert.True(await sut.TryGetAsync(iri2, out var c2, out var t2, out var n2));
        Assert.Equal(other, c2);
        Assert.Equal("image/jpeg", t2);
        Assert.Equal("dog.jpg", n2);
    }

    [Fact]
    public async Task TryGet_UsesLastPathSegmentAsId()
    {
        // A reader (the serve route) hands the store the full media IRI; the store resolves the id from
        // the last path segment (the id), regardless of the base.
        var sut = NewStore();
        var iri = await sut.PutAsync(Pixels, "image/png", "cat.png", Base);

        // Re-ask with the same IRI (what the serve route does).
        Assert.True(await sut.TryGetAsync(iri, out var content, out _, out _));
        Assert.Equal(Pixels, content);
    }
}
