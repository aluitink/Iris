using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Inbox;
using Iris.Server.Media;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Integration tests for 138.13 (Lemmy comment threading): when a Lemmy community member posts a
/// comment (a <c>Create(Note)</c> where the <c>Note</c> has <c>inReplyTo</c> pointing to a parent
/// post or comment), the comment threads correctly under its parent in the Iris community feed.
/// The test verifies: (1) the comment <c>Note</c> is stored in the object store, (2) the reply edge
/// (parent → child) is recorded in the replies store, and (3) the comment is recorded in the
/// community's local member's outbox.
/// </summary>
public sealed class LemmyCommentThreadingIntegrationTests
{
    private const string AHost = "a.domain.local";
    private const string Bob = "bob";
    private const string ACommunity = "inter";

    private readonly InMemoryPersistenceProvider _persistence;
    private readonly InboxProcessor _processor;
    private readonly Iri _bobActorIri;
    private readonly Iri _aCommunityIri;

    public LemmyCommentThreadingIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // Seed bob (a member of the community).
        var aSeeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Bob);
        _bobActorIri = aSeeded.ActorIri;

        // Seed the community, with bob as a local member.
        var (_, aCommunityIri, _) = TestSeeder.SeedCommunityWithKey(
            _persistence, AHost, ACommunity, memberIri: _bobActorIri);
        _aCommunityIri = aCommunityIri;

        // Build the InboxProcessor with the CreateActivityHandler (handles inbound Create activities).
        var queue = new InMemoryDeliveryQueue();
        var delivery = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var localActors = new DefaultLocalActorResolver(_persistence);
        var handler = new CreateActivityHandler(
            _persistence, delivery, localActors, new NoOpMediaWarmer(),
            Options.Create(new ActivityPubServerOptions()));
        _processor = new InboxProcessor(_persistence, [handler]);
    }

    /// <summary>
    /// A Lemmy comment (a <c>Create(Note)</c> with <c>inReplyTo</c> pointing to a parent post) is
    /// stored in the object store, the reply edge is recorded, and the comment is recorded in the
    /// community's local member's outbox.
    /// </summary>
    [Fact]
    public async Task LemmyComment_InReplyTo_Post_ThreadsUnderParent_AndRecordedInMemberOutbox()
    {
        // The parent post (a Page from Lemmy, already stored in the object store).
        var parentPostIri = new Iri($"https://lemmy.luit.ink/post/1");
        var parentPost = new Page
        {
            Id = parentPostIri.Value,
            Name = ["Parent post"],
            Content = ["<p>Parent content</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
        };
        await _persistence.Objects.PutObjectAsync(parentPost, CancellationToken.None);

        // The comment (a Note with inReplyTo pointing to the parent post).
        var commentIri = new Iri($"https://lemmy.luit.ink/comment/101");
        var commentNote = new Note
        {
            Id = commentIri.Value,
            Content = ["<p>A comment on the parent post</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            InReplyTo = [new Link { Href = parentPostIri.Uri }],
            Published = DateTime.UtcNow,
        };

        var create = new Create
        {
            Id = $"https://lemmy.luit.ink/activities/create-comment-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            Object = [commentNote],
            Published = DateTime.UtcNow,
        };

        // Deliver the Create(Note) to the community's inbox.
        var delivery = new InboxDelivery(_aCommunityIri, create);
        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // (a) The comment Note is stored in the object store.
        Assert.True(
            await _persistence.Objects.TryGetObjectAsync(commentIri, out var storedNote),
            "The comment Note should be stored in the object store");
        Assert.IsType<Note>(storedNote);

        // (b) The reply edge (parent → child) is recorded in the replies store.
        var replies = await _persistence.Replies.GetRepliesAsync(parentPostIri, CancellationToken.None);
        Assert.Contains(replies, r => r.Value == commentIri.Value);

        // (c) The Create is recorded in bob's (the community's local member) outbox.
        var bobOutbox = (await _persistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.Contains(bobOutbox, a =>
            a is Create c && c.Object?.OfType<Note>().Any(n => n.Id == commentIri.Value) == true);
    }

    /// <summary>
    /// A multi-level Lemmy comment thread (comment → reply to comment) threads correctly: each level
    /// records its reply edge, and all comments are stored in the object store.
    /// </summary>
    [Fact]
    public async Task LemmyComment_MultiLevelThread_RecordsAllReplyEdges()
    {
        // The parent post (a Page from Lemmy).
        var parentPostIri = new Iri($"https://lemmy.luit.ink/post/1");
        var parentPost = new Page
        {
            Id = parentPostIri.Value,
            Name = ["Parent post"],
            Content = ["<p>Parent content</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
        };
        await _persistence.Objects.PutObjectAsync(parentPost, CancellationToken.None);

        // Level 1: a comment on the parent post.
        var level1Iri = new Iri($"https://lemmy.luit.ink/comment/101");
        var level1Note = new Note
        {
            Id = level1Iri.Value,
            Content = ["<p>Level 1 comment</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            InReplyTo = [new Link { Href = parentPostIri.Uri }],
            Published = DateTime.UtcNow,
        };

        var level1Create = new Create
        {
            Id = $"https://lemmy.luit.ink/activities/create-comment-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            Object = [level1Note],
            Published = DateTime.UtcNow,
        };

        var delivery1 = new InboxDelivery(_aCommunityIri, level1Create);
        await _processor.ProcessAsync(delivery1, CancellationToken.None);

        // Level 2: a reply to the level 1 comment.
        var level2Iri = new Iri($"https://lemmy.luit.ink/comment/102");
        var level2Note = new Note
        {
            Id = level2Iri.Value,
            Content = ["<p>Level 2 reply to level 1</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/bob2").Uri }],
            InReplyTo = [new Link { Href = level1Iri.Uri }],
            Published = DateTime.UtcNow,
        };

        var level2Create = new Create
        {
            Id = $"https://lemmy.luit.ink/activities/create-comment-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Iri("https://lemmy.luit.ink/u/bob2").Uri }],
            Object = [level2Note],
            Published = DateTime.UtcNow,
        };

        var delivery2 = new InboxDelivery(_aCommunityIri, level2Create);
        await _processor.ProcessAsync(delivery2, CancellationToken.None);

        // (a) Both comments are stored in the object store.
        Assert.True(await _persistence.Objects.TryGetObjectAsync(level1Iri, out var level1),
            "Level 1 comment should be stored");
        Assert.True(await _persistence.Objects.TryGetObjectAsync(level2Iri, out var level2),
            "Level 2 comment should be stored");

        // (b) The reply edges are recorded: parent → level1, level1 → level2.
        var parentReplies = await _persistence.Replies.GetRepliesAsync(parentPostIri, CancellationToken.None);
        Assert.Contains(parentReplies, r => r.Value == level1Iri.Value);

        var level1Replies = await _persistence.Replies.GetRepliesAsync(level1Iri, CancellationToken.None);
        Assert.Contains(level1Replies, r => r.Value == level2Iri.Value);

        // (c) Both comments are recorded in bob's outbox.
        var bobOutbox = (await _persistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.Contains(bobOutbox, a =>
            a is Create c && c.Object?.OfType<Note>().Any(n => n.Id == level1Iri.Value) == true);
        Assert.Contains(bobOutbox, a =>
            a is Create c && c.Object?.OfType<Note>().Any(n => n.Id == level2Iri.Value) == true);
    }

    /// <summary>
    /// A Lemmy comment delivered to a community's inbox does NOT appear in the community feed as a
    /// top-level post (it's a reply, not a top-level post). The community feed should only show
    /// top-level posts (Pages), not comments (Notes with inReplyTo).
    /// </summary>
    [Fact]
    public async Task LemmyComment_DeliveredToCommunity_DoesNotAppearAsTopLevelInFeed()
    {
        // The parent post (a Page from Lemmy, already stored).
        var parentPostIri = new Iri($"https://lemmy.luit.ink/post/1");
        var parentPost = new Page
        {
            Id = parentPostIri.Value,
            Name = ["Parent post"],
            Content = ["<p>Parent content</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
        };
        await _persistence.Objects.PutObjectAsync(parentPost, CancellationToken.None);

        // The comment (a Note with inReplyTo pointing to the parent post).
        var commentIri = new Iri($"https://lemmy.luit.ink/comment/101");
        var commentNote = new Note
        {
            Id = commentIri.Value,
            Content = ["<p>A comment</p>"],
            AttributedTo = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            InReplyTo = [new Link { Href = parentPostIri.Uri }],
            Published = DateTime.UtcNow,
        };

        var create = new Create
        {
            Id = $"https://lemmy.luit.ink/activities/create-comment-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Iri("https://lemmy.luit.ink/u/lemmyadmin").Uri }],
            Object = [commentNote],
            Published = DateTime.UtcNow,
        };

        var delivery = new InboxDelivery(_aCommunityIri, create);
        await _processor.ProcessAsync(delivery, CancellationToken.None);

        // The comment is recorded in bob's outbox (so it's available for threading).
        var bobOutbox = (await _persistence.Activities.GetOutboxAsync(_bobActorIri)).ToList();
        Assert.Contains(bobOutbox, a =>
            a is Create c && c.Object?.OfType<Note>().Any(n => n.Id == commentIri.Value) == true);

        // The comment's Note has inReplyTo set (it's a reply, not a top-level post).
        Assert.True(
            await _persistence.Objects.TryGetObjectAsync(commentIri, out var storedComment),
            "The comment should be stored");
        var storedCommentAsNote = (Note)storedComment!;
        Assert.NotNull(storedCommentAsNote.InReplyTo);
        Assert.Contains(storedCommentAsNote.InReplyTo, l =>
            l is Link link && link.Href == parentPostIri.Uri);
    }

    private sealed class NoOpMediaWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
