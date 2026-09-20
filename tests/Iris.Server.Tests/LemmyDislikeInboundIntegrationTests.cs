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
/// Integration tests for 138.17 (Lemmy → Iris dislikes/downvotes): when a Lemmy user downvotes a post
/// that was synced to Iris (stored in Iris's object store), the <c>Dislike</c> activity delivered to
/// the post's author's inbox is recorded by the <see cref="DislikeActivityHandler"/> — the dislike
/// edge is recorded in the <see cref="IDislikeStore"/> so the object's dislike count reflects the
/// Lemmy downvote. An <c>Undo(Dislike)</c> removes the edge.
/// </summary>
public sealed class LemmyDislikeInboundIntegrationTests : IDisposable
{
    private const string Host = "iris.luit.ink";
    private const string Author = "author";
    private const string LemmyDisliker = "lemmy.luit.ink";

    private readonly ServiceProvider _services;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _authorActorIri;
    private readonly Iri _lemmyDislikerIri;

    public LemmyDislikeInboundIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, Author);
        _authorActorIri = seeded.ActorIri;

        _lemmyDislikerIri = new Iri($"https://{LemmyDisliker}/u/lemmy-user");

        var services = new ServiceCollection()
            .AddSingleton<IPersistenceProvider>(_persistence)
            .AddSingleton<ILocalActorResolver, DefaultLocalActorResolver>()
            .AddSingleton<DislikeActivityHandler>()
            .BuildServiceProvider();

        _services = services;
    }

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// A Lemmy downvote (a <c>Dislike</c> from a remote Lemmy actor) on a synced post (stored in
    /// Iris's object store) is recorded: the dislike edge appears in the <see cref="IDislikeStore"/>,
    /// so the object's dislikers collection lists the Lemmy disliker.
    /// </summary>
    [Fact]
    public async Task LemmyDownvoteOnSyncedPost_DislikeEdgeRecorded()
    {
        var postIri = new Iri($"https://{Host}/ap/v1/objects/post-{Guid.NewGuid():N}");
        await _persistence.Objects.PutObjectAsync(new Page
        {
            Id = postIri.Value,
            Name = ["A synced Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = _authorActorIri.Uri }],
        }, CancellationToken.None);

        var dislikeIri = new Iri($"https://{LemmyDisliker}/activities/dislike-{Guid.NewGuid():N}");
        var dislike = new Dislike
        {
            Id = dislikeIri.Value,
            Actor = [new Link { Href = _lemmyDislikerIri.Uri }],
            Object = [new Link { Href = postIri.Uri }],
        };

        var delivery = new InboxDelivery(_authorActorIri, dislike);
        var handler = _services.GetRequiredService<DislikeActivityHandler>();

        await handler.HandleAsync(delivery, dislike, CancellationToken.None);

        var dislikers = await _persistence.Dislikes.GetDislikersAsync(postIri, CancellationToken.None);
        Assert.Contains(dislikers, d => d == _lemmyDislikerIri);
    }

    /// <summary>
    /// A Lemmy downvote on a community post (delivered to the community's inbox) is recorded in the
    /// dislike store AND in the community's local members' outboxes.
    /// </summary>
    [Fact]
    public async Task LemmyDownvoteOnCommunityPost_RecordedInMembersOutboxes()
    {
        var communityIri = new Iri($"https://{Host}/c/interop");
        var memberIri = _authorActorIri;
        await _persistence.Communities.PutCommunityAsync(new Group
        {
            Id = communityIri.Value,
            Name = ["interop"],
        }, CancellationToken.None);
        await _persistence.Communities.AddFollowerAsync(communityIri, memberIri, CancellationToken.None);

        var postIri = new Iri($"https://{Host}/ap/v1/objects/post-{Guid.NewGuid():N}");
        await _persistence.Objects.PutObjectAsync(new Page
        {
            Id = postIri.Value,
            Name = ["A community post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = memberIri.Uri }],
        }, CancellationToken.None);

        var dislikeIri = new Iri($"https://{LemmyDisliker}/activities/dislike-{Guid.NewGuid():N}");
        var dislike = new Dislike
        {
            Id = dislikeIri.Value,
            Actor = [new Link { Href = _lemmyDislikerIri.Uri }],
            Object = [new Link { Href = postIri.Uri }],
        };

        var delivery = new InboxDelivery(communityIri, dislike);
        var handler = _services.GetRequiredService<DislikeActivityHandler>();

        await handler.HandleAsync(delivery, dislike, CancellationToken.None);

        var dislikers = await _persistence.Dislikes.GetDislikersAsync(postIri, CancellationToken.None);
        Assert.Contains(dislikers, d => d == _lemmyDislikerIri);

        var outbox = await _persistence.Activities.GetOutboxAsync(memberIri, CancellationToken.None);
        Assert.Contains(outbox, a => a is Dislike recordedDislike && recordedDislike.Id == dislikeIri.Value);
    }

    /// <summary>
    /// A Lemmy downvote on a remote post (NOT stored in Iris's object store) is NOT recorded locally.
    /// </summary>
    [Fact]
    public async Task LemmyDownvoteOnRemotePost_NotRecordedLocally()
    {
        var remotePostIri = new Iri($"https://{LemmyDisliker}/objects/remote-post-{Guid.NewGuid():N}");

        var dislikeIri = new Iri($"https://{LemmyDisliker}/activities/dislike-{Guid.NewGuid():N}");
        var dislike = new Dislike
        {
            Id = dislikeIri.Value,
            Actor = [new Link { Href = _lemmyDislikerIri.Uri }],
            Object = [new Link { Href = remotePostIri.Uri }],
        };

        var delivery = new InboxDelivery(_authorActorIri, dislike);
        var handler = _services.GetRequiredService<DislikeActivityHandler>();

        await handler.HandleAsync(delivery, dislike, CancellationToken.None);

        var dislikers = await _persistence.Dislikes.GetDislikersAsync(remotePostIri, CancellationToken.None);
        Assert.Empty(dislikers);
    }

    /// <summary>
    /// An <c>Undo(Dislike)</c> removes the previously recorded dislike edge (the inverse of the
    /// <see cref="DislikeActivityHandler"/>).
    /// </summary>
    [Fact]
    public async Task UndoDislike_RemovesDislikeEdge()
    {
        var postIri = new Iri($"https://{Host}/ap/v1/objects/post-{Guid.NewGuid():N}");
        await _persistence.Objects.PutObjectAsync(new Page
        {
            Id = postIri.Value,
            Name = ["A synced Lemmy post"],
            Content = ["<p>Post content</p>"],
            AttributedTo = [new Link { Href = _authorActorIri.Uri }],
        }, CancellationToken.None);

        // First, record a dislike.
        var dislikeIri = new Iri($"https://{LemmyDisliker}/activities/dislike-{Guid.NewGuid():N}");
        var dislike = new Dislike
        {
            Id = dislikeIri.Value,
            Actor = [new Link { Href = _lemmyDislikerIri.Uri }],
            Object = [new Link { Href = postIri.Uri }],
        };

        var dislikeHandler = _services.GetRequiredService<DislikeActivityHandler>();
        await dislikeHandler.HandleAsync(new InboxDelivery(_authorActorIri, dislike), dislike, CancellationToken.None);

        Assert.True(
            await _persistence.Dislikes.HasDislikedAsync(_lemmyDislikerIri, postIri, CancellationToken.None),
            "The dislike edge should be recorded before the undo");

        // Now, undo the dislike.
        await _persistence.Dislikes.RemoveDislikeAsync(_lemmyDislikerIri, postIri, CancellationToken.None);

        Assert.False(
            await _persistence.Dislikes.HasDislikedAsync(_lemmyDislikerIri, postIri, CancellationToken.None),
            "The dislike edge should be removed after the undo");
    }
}
