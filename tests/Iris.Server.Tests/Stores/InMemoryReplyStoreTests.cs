using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Unit tests for the <see cref="InMemoryReplyStore"/> — the reply (thread) read/write surface backing
/// a note's <c>replies</c> collection (F-12). Covers the <see cref="IReplyStore"/> contract: record a
/// reply (parent → child), query a parent's replies, has-reply, remove, per-parent isolation, idempotency
/// of a repeated reply (a set — no duplicates), and thread-safety of concurrent reply records.
/// </summary>
public sealed class InMemoryReplyStoreTests
{
    private static readonly Iri Parent1 = new("https://a.test/ap/v1/u/alice/notes/parent-1");
    private static readonly Iri Parent2 = new("https://a.test/ap/v1/u/alice/notes/parent-2");
    private static readonly Iri Reply1 = new("https://a.test/ap/v1/u/bob/notes/reply-1");
    private static readonly Iri Reply2 = new("https://a.test/ap/v1/u/carol/notes/reply-2");

    [Fact]
    public async Task RecordReply_ThenGetReplies_ReturnsTheChild()
    {
        var sut = new InMemoryReplyStore();

        await sut.RecordReplyAsync(Parent1, Reply1);

        var replies = await sut.GetRepliesAsync(Parent1);
        Assert.Equal([Reply1], replies);
    }

    [Fact]
    public async Task GetReplies_NoReplies_ReturnsEmpty()
    {
        var sut = new InMemoryReplyStore();

        var replies = await sut.GetRepliesAsync(Parent1);

        Assert.Empty(replies);
    }

    [Fact]
    public async Task HasReply_AfterRecord_ReturnsTrue()
    {
        var sut = new InMemoryReplyStore();

        await sut.RecordReplyAsync(Parent1, Reply1);

        Assert.True(await sut.HasReplyAsync(Parent1, Reply1));
    }

    [Fact]
    public async Task HasReply_NoReply_ReturnsFalse()
    {
        var sut = new InMemoryReplyStore();

        Assert.False(await sut.HasReplyAsync(Parent1, Reply1));
    }

    [Fact]
    public async Task RecordReply_ThenRemove_RemovesTheEdge()
    {
        var sut = new InMemoryReplyStore();
        await sut.RecordReplyAsync(Parent1, Reply1);

        var removed = await sut.RemoveReplyAsync(Parent1, Reply1);

        Assert.True(removed);
        Assert.False(await sut.HasReplyAsync(Parent1, Reply1));
        Assert.Empty(await sut.GetRepliesAsync(Parent1));
    }

    [Fact]
    public async Task RemoveReply_Unknown_ReturnsFalse()
    {
        var sut = new InMemoryReplyStore();

        var removed = await sut.RemoveReplyAsync(Parent1, Reply1);

        Assert.False(removed);
    }

    [Fact]
    public async Task RecordReply_Repeated_IsIdempotent()
    {
        var sut = new InMemoryReplyStore();

        await sut.RecordReplyAsync(Parent1, Reply1);
        await sut.RecordReplyAsync(Parent1, Reply1);

        // A set: a repeated reply does not duplicate the entry.
        var replies = await sut.GetRepliesAsync(Parent1);
        Assert.Equal([Reply1], replies);
    }

    [Fact]
    public async Task RecordReply_MultipleReplies_AllReturned()
    {
        var sut = new InMemoryReplyStore();
        await sut.RecordReplyAsync(Parent1, Reply1);
        await sut.RecordReplyAsync(Parent1, Reply2);

        var replies = await sut.GetRepliesAsync(Parent1);
        Assert.Equal(2, replies.Count);
        Assert.Contains(Reply1, replies);
        Assert.Contains(Reply2, replies);
    }

    [Fact]
    public async Task RecordReply_PerParentIsolation()
    {
        var sut = new InMemoryReplyStore();
        await sut.RecordReplyAsync(Parent1, Reply1);
        await sut.RecordReplyAsync(Parent2, Reply2);

        // Each parent's replies collection is independent.
        Assert.Equal([Reply1], await sut.GetRepliesAsync(Parent1));
        Assert.Equal([Reply2], await sut.GetRepliesAsync(Parent2));
        Assert.False(await sut.HasReplyAsync(Parent1, Reply2));
        Assert.False(await sut.HasReplyAsync(Parent2, Reply1));
    }

    [Fact]
    public async Task RecordReply_Concurrent_AddsAreThreadSafe()
    {
        var sut = new InMemoryReplyStore();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => sut.RecordReplyAsync(Parent1, new Iri($"https://a.test/ap/v1/u/bob/notes/reply-{i}")));

        await Task.WhenAll(tasks);

        var replies = await sut.GetRepliesAsync(Parent1);
        Assert.Equal(50, replies.Count);
    }
}
