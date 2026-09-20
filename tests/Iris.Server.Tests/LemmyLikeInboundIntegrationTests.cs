using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Inbox;
using Iris.Server.Stores;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Integration tests for 138.15 (Lemmy → Iris likes): when a Lemmy user upvotes a post that was
/// synced to Iris (stored in Iris's object store), the <c>Like</c> activity delivered to the post's
/// author's inbox is recorded by the existing <see cref="LikeActivityHandler"/> — the like edge is
/// recorded in the <see cref="ILikeStore"/> so the object's <c>/likes</c> collection and like count
/// reflect the Lemmy upvote. This verifies the handler works identically for a remote Lemmy actor as
/// it does for a remote Mastodon actor (31.10).
/// </summary>
public sealed class LemmyLikeInboundIntegrationTests : IDisposable
{
    private const string Host = "iris.luit.ink";
    private const string Author = "author";
    private const string LemmyLiker = "lemmy.luit.ink";

    private readonly ServiceProvider _services;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _authorActorIri;
    private readonly Iri _lemmyLikerIri;

    public LemmyLikeInboundIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, Author);
        _authorActorIri = seeded.ActorIri;

        // A remote Lemmy actor (the liker).
        _lemmyLikerIri = new Iri($"https://{LemmyLiker}/u/lemmy-user");

        var services = new ServiceCollection()
            .AddSingleton<IPersistenceProvider>(_persistence)
            .AddSingleton<ILocalActorResolver, DefaultLocalActorResolver>()
            .AddSingleton<LikeActivityHandler>()
            .BuildServiceProvider();

        _services = services;
    }

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// A Lemmy upvote (a <c>Like</c> from a remote Lemmy actor) on a synced post (stored in Iris's
    /// object store) is recorded: the like edge appears in the <see cref="ILikeStore"/>, so the
    /// object's <c>/likes</c> collection lists the Lemmy liker.
    /// </summary>
    [Fact]
    public async Task LemmyUpvoteOnSyncedPost_LikeEdgeRecorded()
    {
        // A synced post (a Page, Lemmy's post format) stored in Iris's object store.
        var postIri = new Iri($"https://{Host}/ap/v1/objects/post-{Guid.NewGuid():N}");
        await _persistence.Objects.PutObjectAsync(new Page
        {
            Id = postIri.Value,
            Name = ["A synced Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = _authorActorIri.Uri }],
        }, CancellationToken.None);

        // A Lemmy user upvotes the post: a Like activity from the remote Lemmy actor.
        var likeIri = new Iri($"https://{LemmyLiker}/activities/like-{Guid.NewGuid():N}");
        var like = new Like
        {
            Id = likeIri.Value,
            Actor = [new Link { Href = _lemmyLikerIri.Uri }],
            Object = [new Link { Href = postIri.Uri }],
        };

        var delivery = new InboxDelivery(_authorActorIri, like);
        var handler = _services.GetRequiredService<LikeActivityHandler>();

        await handler.HandleAsync(delivery, like, CancellationToken.None);

        // The like edge is recorded: the Lemmy liker appears in the post's likers.
        var likers = await _persistence.Likes.GetLikersAsync(postIri, CancellationToken.None);
        Assert.Contains(likers, l => l == _lemmyLikerIri);
    }

    /// <summary>
    /// A Lemmy upvote on a community post (delivered to the community's inbox) is recorded in the
    /// like store AND in the community's local members' outboxes (the community-feed path).
    /// </summary>
    [Fact]
    public async Task LemmyUpvoteOnCommunityPost_RecordedInMembersOutboxes()
    {
        // A community (a Group) with a local member.
        var communityIri = new Iri($"https://{Host}/c/interop");
        var memberIri = _authorActorIri;
        await _persistence.Communities.PutCommunityAsync(new Group
        {
            Id = communityIri.Value,
            Name = ["interop"],
        }, CancellationToken.None);
        await _persistence.Communities.AddFollowerAsync(communityIri, memberIri, CancellationToken.None);

        // A synced post (a Page) in the community.
        var postIri = new Iri($"https://{Host}/ap/v1/objects/post-{Guid.NewGuid():N}");
        await _persistence.Objects.PutObjectAsync(new Page
        {
            Id = postIri.Value,
            Name = ["A community post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = memberIri.Uri }],
        }, CancellationToken.None);

        // A Lemmy user upvotes the post: a Like activity delivered to the community's inbox.
        var likeIri = new Iri($"https://{LemmyLiker}/activities/like-{Guid.NewGuid():N}");
        var like = new Like
        {
            Id = likeIri.Value,
            Actor = [new Link { Href = _lemmyLikerIri.Uri }],
            Object = [new Link { Href = postIri.Uri }],
        };

        var delivery = new InboxDelivery(communityIri, like);
        var handler = _services.GetRequiredService<LikeActivityHandler>();

        await handler.HandleAsync(delivery, like, CancellationToken.None);

        // The like edge is recorded (the post is locally stored).
        var likers = await _persistence.Likes.GetLikersAsync(postIri, CancellationToken.None);
        Assert.Contains(likers, l => l == _lemmyLikerIri);

        // The like is recorded in the community's local members' outboxes.
        var outbox = await _persistence.Activities.GetOutboxAsync(memberIri, CancellationToken.None);
        Assert.Contains(outbox, a => a is Like recordedLike && recordedLike.Id == likeIri.Value);
    }

    /// <summary>
    /// A Lemmy upvote on a remote post (NOT stored in Iris's object store) is NOT recorded locally —
    /// the edge is recorded on the object's author's home instance instead.
    /// </summary>
    [Fact]
    public async Task LemmyUpvoteOnRemotePost_NotRecordedLocally()
    {
        // A remote post NOT in Iris's object store.
        var remotePostIri = new Iri($"https://{LemmyLiker}/objects/remote-post-{Guid.NewGuid():N}");

        // A Lemmy user upvotes the remote post.
        var likeIri = new Iri($"https://{LemmyLiker}/activities/like-{Guid.NewGuid():N}");
        var like = new Like
        {
            Id = likeIri.Value,
            Actor = [new Link { Href = _lemmyLikerIri.Uri }],
            Object = [new Link { Href = remotePostIri.Uri }],
        };

        var delivery = new InboxDelivery(_authorActorIri, like);
        var handler = _services.GetRequiredService<LikeActivityHandler>();

        await handler.HandleAsync(delivery, like, CancellationToken.None);

        // No like edge is recorded locally (the post is not stored in Iris's object store).
        var likers = await _persistence.Likes.GetLikersAsync(remotePostIri, CancellationToken.None);
        Assert.Empty(likers);
    }
}
