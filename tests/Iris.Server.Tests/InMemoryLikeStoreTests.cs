using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="InMemoryLikeStore"/> — the like (endorsement) read/write surface
/// backing the actor's <c>liked</c> collection (F-04). Covers the <see cref="ILikeStore"/> contract:
/// record a like, query the liked collection, has-liked, remove, per-liker isolation, idempotency of a
/// repeated like (a set — no duplicates), and thread-safety of concurrent like records.
/// </summary>
public sealed class InMemoryLikeStoreTests
{
    private static readonly Iri Bob = new("https://a.test/ap/v1/u/bob");
    private static readonly Iri Carol = new("https://a.test/ap/v1/u/carol");
    private static readonly Iri Note1 = new("https://a.test/ap/v1/o/note-1");
    private static readonly Iri Note2 = new("https://a.test/ap/v1/o/note-2");

    [Fact]
    public async Task RecordLike_ThenGetLiked_ReturnsTheObject()
    {
        var sut = new InMemoryLikeStore();

        await sut.RecordLikeAsync(Bob, Note1);

        var liked = await sut.GetLikedAsync(Bob);
        Assert.Equal([Note1], liked);
    }

    [Fact]
    public async Task GetLiked_NoLikes_ReturnsEmpty()
    {
        var sut = new InMemoryLikeStore();

        var liked = await sut.GetLikedAsync(Bob);

        Assert.Empty(liked);
    }

    [Fact]
    public async Task HasLiked_AfterRecord_ReturnsTrue()
    {
        var sut = new InMemoryLikeStore();

        await sut.RecordLikeAsync(Bob, Note1);

        Assert.True(await sut.HasLikedAsync(Bob, Note1));
    }

    [Fact]
    public async Task HasLiked_NoLike_ReturnsFalse()
    {
        var sut = new InMemoryLikeStore();

        Assert.False(await sut.HasLikedAsync(Bob, Note1));
    }

    [Fact]
    public async Task RecordLike_ThenRemove_RemovesTheEdge()
    {
        var sut = new InMemoryLikeStore();
        await sut.RecordLikeAsync(Bob, Note1);

        var removed = await sut.RemoveLikeAsync(Bob, Note1);

        Assert.True(removed);
        Assert.False(await sut.HasLikedAsync(Bob, Note1));
        Assert.Empty(await sut.GetLikedAsync(Bob));
    }

    [Fact]
    public async Task RemoveLike_Unknown_ReturnsFalse()
    {
        var sut = new InMemoryLikeStore();

        var removed = await sut.RemoveLikeAsync(Bob, Note1);

        Assert.False(removed);
    }

    [Fact]
    public async Task RecordLike_Repeated_IsIdempotent()
    {
        var sut = new InMemoryLikeStore();

        await sut.RecordLikeAsync(Bob, Note1);
        await sut.RecordLikeAsync(Bob, Note1);

        // A set: a repeated like does not duplicate the entry.
        var liked = await sut.GetLikedAsync(Bob);
        Assert.Equal([Note1], liked);
    }

    [Fact]
    public async Task RecordLike_MultipleObjects_AllReturned()
    {
        var sut = new InMemoryLikeStore();
        await sut.RecordLikeAsync(Bob, Note1);
        await sut.RecordLikeAsync(Bob, Note2);

        var liked = await sut.GetLikedAsync(Bob);
        Assert.Equal(2, liked.Count);
        Assert.Contains(Note1, liked);
        Assert.Contains(Note2, liked);
    }

    [Fact]
    public async Task RecordLike_PerLikerIsolation()
    {
        var sut = new InMemoryLikeStore();
        await sut.RecordLikeAsync(Bob, Note1);
        await sut.RecordLikeAsync(Carol, Note2);

        // Each liker's liked collection is independent.
        Assert.Equal([Note1], await sut.GetLikedAsync(Bob));
        Assert.Equal([Note2], await sut.GetLikedAsync(Carol));
        Assert.False(await sut.HasLikedAsync(Bob, Note2));
        Assert.False(await sut.HasLikedAsync(Carol, Note1));
    }

    [Fact]
    public async Task RecordLike_Concurrent_AddsAreThreadSafe()
    {
        var sut = new InMemoryLikeStore();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => sut.RecordLikeAsync(Bob, new Iri($"https://a.test/ap/v1/o/note-{i}")));

        await Task.WhenAll(tasks);

        var liked = await sut.GetLikedAsync(Bob);
        Assert.Equal(50, liked.Count);
    }
}
