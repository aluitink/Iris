using Iris.Core;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Inbox;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Xunit;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// 138.23 (deletion vs. moderator-removal semantics) — verifies that Iris's
/// <see cref="DeleteActivityHandler"/> correctly distinguishes an author's own delete from a
/// community moderator's removal. An author delete (actor == attributedTo) produces a plain
/// tombstone. A mod removal (actor is a community member, not the owner) is accepted and the
/// tombstone carries <c>iris:removedBy</c> recording the moderator's IRI. A non-member remote
/// actor's delete is rejected (no tombstone).
/// </summary>
public sealed class LemmyDeletionSemanticsIntegrationTests
{
    private const string Host = "delsim.domain.local";
    private const string Member = "bob";
    private const string RemoteHost = "lemmy.luit.ink";
    private const string Community = "interop";

    private static readonly Iri MemberIri = new($"https://{Host}/ap/v1/u/{Member}");
    private static readonly Iri CommunityIri = new($"https://{RemoteHost}/c/{Community}");
    private static readonly Iri RemoteAuthorIri = new($"https://{RemoteHost}/u/lemmyadmin");
    private static readonly Iri RemoteModIri = new($"https://{RemoteHost}/u/moderator");
    private static readonly Iri RemoteStrangerIri = new($"https://{RemoteHost}/u/stranger");
    private static readonly Iri PostIri = new($"https://{RemoteHost}/post/500");

    [Fact]
    public async Task RemoteAuthor_DeletesOwnPost_PermanentTombstone_NoRemovedBy()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The post is in the local store.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var original));
        Assert.NotNull(original);

        // (b) The author deletes their own post (actor == attributedTo).
        var delete = new Delete
        {
            Id = $"https://{RemoteHost}/c/{Community}/delete/post/500",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [new Link { Href = PostIri.Uri }],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, delete), delete);

        // (c) The local store now has a Tombstone (not the original Page).
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var tombstoned));
        var tombstone = Assert.IsType<Tombstone>(tombstoned);
        Assert.Equal(PostIri.Value, tombstone.Id);
        Assert.Contains("Page", tombstone.FormerType!);

        // (d) No iris:removedBy (author delete, not a mod removal).
        Assert.False(
            tombstone.ExtensionData is { Count: > 0 } && tombstone.ExtensionData.ContainsKey(IrisExtensionTerms.RemovedBy),
            "Author delete should not carry iris:removedBy");
    }

    [Fact]
    public async Task RemoteMod_RemovesCommunityPost_TombstoneHasRemovedBy()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The post is in the local store, attributed to the author, with the community in To.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var original));
        Assert.NotNull(original);

        // (b) A community moderator removes the post (actor != attributedTo, actor is a community member).
        var delete = new Delete
        {
            Id = $"https://{RemoteHost}/c/{Community}/modremove/post/500",
            Actor = [new Link { Href = RemoteModIri.Uri }],
            Object = [new Link { Href = PostIri.Uri }],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, delete), delete);

        // (c) The local store now has a Tombstone.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var tombstoned));
        var tombstone = Assert.IsType<Tombstone>(tombstoned);
        Assert.Equal(PostIri.Value, tombstone.Id);

        // (d) The tombstone carries iris:removedBy = the moderator's IRI.
        Assert.True(
            tombstone.ExtensionData is { Count: > 0 } && tombstone.ExtensionData.ContainsKey(IrisExtensionTerms.RemovedBy),
            "Mod removal should carry iris:removedBy");
        var removedByValue = tombstone.ExtensionData[IrisExtensionTerms.RemovedBy].GetString();
        Assert.Equal(RemoteModIri.Value, removedByValue);
    }

    [Fact]
    public async Task RemoteStranger_DeletesCommunityPost_Rejected_NoTombstone()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The post is in the local store.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var original));
        var originalPage = Assert.IsType<Page>(original);
        Assert.NotNull(originalPage);

        // (b) A stranger (not the author, not a community member) attempts to delete the post.
        var delete = new Delete
        {
            Id = $"https://{RemoteHost}/c/{Community}/stranger/delete/post/500",
            Actor = [new Link { Href = RemoteStrangerIri.Uri }],
            Object = [new Link { Href = PostIri.Uri }],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, delete), delete);

        // (c) The post is unchanged (no tombstone — the delete was rejected).
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var unchanged));
        Assert.IsType<Page>(unchanged);
    }

    [Fact]
    public async Task RemoteMod_RemovesPost_ReplyEdgeCleanedUp()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The post has a reply.
        var replies = await persistence.Replies.GetRepliesAsync(PostIri);
        Assert.True(replies.Any());

        // (b) A community moderator removes the post.
        var delete = new Delete
        {
            Id = $"https://{RemoteHost}/c/{Community}/modremove2/post/500",
            Actor = [new Link { Href = RemoteModIri.Uri }],
            Object = [new Link { Href = PostIri.Uri }],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, delete), delete);

        // (c) The post is tombstoned.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var tombstoned));
        Assert.IsType<Tombstone>(tombstoned);

        // (d) The reply edge is collapsed (the tombstoned parent's replies are empty).
        var remaining = await persistence.Replies.GetRepliesAsync(PostIri);
        Assert.False(remaining.Any());
    }

    [Fact]
    public async Task RemoteAuthor_DeletesOwnPost_ReplyEdgeCleanedUp()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The post has a reply.
        var replies = await persistence.Replies.GetRepliesAsync(PostIri);
        Assert.True(replies.Any());

        // (b) The author deletes their own post.
        var delete = new Delete
        {
            Id = $"https://{RemoteHost}/c/{Community}/delete2/post/500",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [new Link { Href = PostIri.Uri }],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, delete), delete);

        // (c) The post is tombstoned.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var tombstoned));
        Assert.IsType<Tombstone>(tombstoned);

        // (d) The reply edge is collapsed.
        var remaining = await persistence.Replies.GetRepliesAsync(PostIri);
        Assert.False(remaining.Any());
    }

    // --- Fixture -----------------------------------------------------------------------------------

    private static async Task<(InMemoryPersistenceProvider, DeleteActivityHandler)> BuildFixtureAsync()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedCommunityWithKey(persistence, Host, "local", memberIri: MemberIri);
        TestSeeder.SeedPersonWithKey(persistence, Host, Member);

        // Archive a Lemmy post (Page) attributed to the remote author, with the Lemmy community
        // in the To array (as Lemmy's real posts are structured).
        var post = new Page
        {
            Id = PostIri.Value,
            Name = ["Lemmy post"],
            Content = ["<p>Post body</p>"],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
            To = [new Link { Href = CommunityIri.Uri }],
        };
        await persistence.Objects.PutObjectAsync(post);

        // A reply to the post (to verify reply-edge cleanup).
        var replyIri = new Iri($"https://{RemoteHost}/comment/600");
        var reply = new Note
        {
            Id = replyIri.Value,
            Content = ["<p>A reply</p>"],
            InReplyTo = [new Link { Href = PostIri.Uri }],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        await persistence.Objects.PutObjectAsync(reply);
        await persistence.Replies.RecordReplyAsync(PostIri, replyIri);

        // Register the moderator as a member of the remote Lemmy community. The
        // DeleteActivityHandler checks ICommunityStore.IsMemberAsync for the mod-removal path.
        await persistence.Communities.AddFollowerAsync(CommunityIri, RemoteModIri);

        var handler = new DeleteActivityHandler(
            persistence,
            new StubLocalActorResolver(),
            new NoOpDeletePropagationService());

        return (persistence, handler);
    }

    private sealed class StubLocalActorResolver : ILocalActorResolver
    {
        public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class NoOpDeletePropagationService : IDeletePropagationService
    {
        public Task PropagateUpdateAsync(Iri authorIri, Iri objectIri, Update activity, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PropagateDeleteAsync(Iri authorIri, Iri objectIri, Delete activity, IObject? tombstone, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
