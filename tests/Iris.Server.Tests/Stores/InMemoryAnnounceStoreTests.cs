using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Unit tests for the <see cref="InMemoryAnnounceStore"/> — the announce (boost / re-share) read/write
/// surface backing the per-object <c>shares</c> collection (decision 056 (d), the per-object boost
/// counter). Covers the <see cref="IAnnounceStore"/> contract: record a boost, query the announced
/// collection, has-announced, remove, per-announcer isolation, idempotency of a repeated boost (a set —
/// no duplicates), the <c>announced object → [announcers]</c> reverse index (the object's <c>shares</c>
/// collection), and thread-safety of concurrent boost records.
/// </summary>
public sealed class InMemoryAnnounceStoreTests
{
    private static readonly Iri Bob = new("https://a.test/ap/v1/u/bob");
    private static readonly Iri Carol = new("https://a.test/ap/v1/u/carol");
    private static readonly Iri Note1 = new("https://a.test/ap/v1/o/note-1");
    private static readonly Iri Note2 = new("https://a.test/ap/v1/o/note-2");

    [Fact]
    public async Task RecordAnnounce_ThenGetAnnounced_ReturnsTheObject()
    {
        var sut = new InMemoryAnnounceStore();

        await sut.RecordAnnounceAsync(Bob, Note1);

        var announced = await sut.GetAnnouncedAsync(Bob);
        Assert.Equal([Note1], announced);
    }

    [Fact]
    public async Task GetAnnounced_NoBoosts_ReturnsEmpty()
    {
        var sut = new InMemoryAnnounceStore();

        var announced = await sut.GetAnnouncedAsync(Bob);

        Assert.Empty(announced);
    }

    [Fact]
    public async Task HasAnnounced_AfterRecord_ReturnsTrue()
    {
        var sut = new InMemoryAnnounceStore();

        await sut.RecordAnnounceAsync(Bob, Note1);

        Assert.True(await sut.HasAnnouncedAsync(Bob, Note1));
    }

    [Fact]
    public async Task HasAnnounced_NoBoost_ReturnsFalse()
    {
        var sut = new InMemoryAnnounceStore();

        Assert.False(await sut.HasAnnouncedAsync(Bob, Note1));
    }

    [Fact]
    public async Task RecordAnnounce_ThenRemove_RemovesTheEdge()
    {
        var sut = new InMemoryAnnounceStore();
        await sut.RecordAnnounceAsync(Bob, Note1);

        var removed = await sut.RemoveAnnounceAsync(Bob, Note1);

        Assert.True(removed);
        Assert.False(await sut.HasAnnouncedAsync(Bob, Note1));
        Assert.Empty(await sut.GetAnnouncedAsync(Bob));
    }

    [Fact]
    public async Task RemoveAnnounce_Unknown_ReturnsFalse()
    {
        var sut = new InMemoryAnnounceStore();

        var removed = await sut.RemoveAnnounceAsync(Bob, Note1);

        Assert.False(removed);
    }

    [Fact]
    public async Task RecordAnnounce_Repeated_IsIdempotent()
    {
        var sut = new InMemoryAnnounceStore();

        await sut.RecordAnnounceAsync(Bob, Note1);
        await sut.RecordAnnounceAsync(Bob, Note1);

        // A set: a repeated boost does not duplicate the entry.
        var announced = await sut.GetAnnouncedAsync(Bob);
        Assert.Equal([Note1], announced);
    }

    [Fact]
    public async Task RecordAnnounce_MultipleObjects_AllReturned()
    {
        var sut = new InMemoryAnnounceStore();
        await sut.RecordAnnounceAsync(Bob, Note1);
        await sut.RecordAnnounceAsync(Bob, Note2);

        var announced = await sut.GetAnnouncedAsync(Bob);
        Assert.Equal(2, announced.Count);
        Assert.Contains(Note1, announced);
        Assert.Contains(Note2, announced);
    }

    [Fact]
    public async Task RecordAnnounce_PerAnnouncerIsolation()
    {
        var sut = new InMemoryAnnounceStore();
        await sut.RecordAnnounceAsync(Bob, Note1);
        await sut.RecordAnnounceAsync(Carol, Note2);

        // Each announcer's announced collection is independent.
        Assert.Equal([Note1], await sut.GetAnnouncedAsync(Bob));
        Assert.Equal([Note2], await sut.GetAnnouncedAsync(Carol));
        Assert.False(await sut.HasAnnouncedAsync(Bob, Note2));
        Assert.False(await sut.HasAnnouncedAsync(Carol, Note1));
    }

    // --- The announced object → [announcers] reverse index (the object's `shares` collection) ---

    [Fact]
    public async Task RecordAnnounce_ThenGetAnnouncers_ReturnsTheAnnouncer()
    {
        var sut = new InMemoryAnnounceStore();

        await sut.RecordAnnounceAsync(Bob, Note1);

        var announcers = await sut.GetAnnouncersAsync(Note1);
        Assert.Equal([Bob], announcers);
    }

    [Fact]
    public async Task GetAnnouncers_NoBoosts_ReturnsEmpty()
    {
        var sut = new InMemoryAnnounceStore();

        Assert.Empty(await sut.GetAnnouncersAsync(Note1));
    }

    [Fact]
    public async Task RecordAnnounce_MultipleAnnouncers_AllReturned()
    {
        var sut = new InMemoryAnnounceStore();
        await sut.RecordAnnounceAsync(Bob, Note1);
        await sut.RecordAnnounceAsync(Carol, Note1);

        var announcers = await sut.GetAnnouncersAsync(Note1);
        Assert.Equal(2, announcers.Count);
        Assert.Contains(Bob, announcers);
        Assert.Contains(Carol, announcers);
    }

    [Fact]
    public async Task RecordAnnounce_ThenRemove_RemovesFromReverseIndex()
    {
        var sut = new InMemoryAnnounceStore();
        await sut.RecordAnnounceAsync(Bob, Note1);
        await sut.RecordAnnounceAsync(Carol, Note1);

        await sut.RemoveAnnounceAsync(Bob, Note1);

        // Bob is gone from the object's shares; Carol remains.
        var announcers = await sut.GetAnnouncersAsync(Note1);
        Assert.Equal([Carol], announcers);
    }

    [Fact]
    public async Task RecordAnnounce_Concurrent_AddsAreThreadSafe()
    {
        var sut = new InMemoryAnnounceStore();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => sut.RecordAnnounceAsync(Bob, new Iri($"https://a.test/ap/v1/o/note-{i}")));

        await Task.WhenAll(tasks);

        var announced = await sut.GetAnnouncedAsync(Bob);
        Assert.Equal(50, announced.Count);
    }
}
