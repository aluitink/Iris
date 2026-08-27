using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="InboxProcessor"/> dispatch logic and the <see cref="FollowActivityHandler"/>.
/// These exercise the pure interpretation layer (no HTTP): the processor stores the delivered activity
/// and dispatches it to the registered <see cref="IActivityHandler"/> for its type.
/// </summary>
public sealed class InboxProcessorTests
{
    private readonly Iri RecipientIri = new("https://b.domain.local/ap/v1/u/bob");
    private readonly Iri FollowerIri = new("https://a.domain.local/ap/v1/u/alice");

    // --- Dispatch: a Follow is stored, recorded as a follow edge, and an Accept is queued --

    [Fact]
    public async Task ProcessAsync_Follow_WithFollowHandler_StoresRecordsFollowEdgeAndQueuesAccept()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, RecipientIri);
        var (queue, processor) = BuildProcessorWithFollowHandler(persistence);
        var follow = BuildFollow(FollowerIri, RecipientIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The activity is stored under its IRI.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var stored));
        Assert.Equal(follow.Id, stored!.Id);

        // The FollowActivityHandler recorded the follow edge: alice follows bob.
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        var followers = await persistence.Follows.GetFollowersAsync(RecipientIri);
        Assert.Contains(FollowerIri, followers);

        // The FollowActivityHandler scheduled an Accept response for delivery to the follower's inbox
        // (alice's inbox, derived from alice's actor IRI).
        Assert.Equal(1, queue.Count);
        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(FollowerIri.InboxOf(), job!.InboxIri);
        Assert.IsType<Accept>(job.Activity);
        var accept = Assert.IsType<Accept>(job.Activity);
        // The Accept's object references the original follow (by IRI), and the actor is bob.
        Assert.NotNull(accept.Object);
        Assert.Contains(accept.Object!, o => o is ILink { Href: { } href } && href == new Uri(follow.Id!));
        Assert.NotNull(accept.Actor);
        Assert.Contains(accept.Actor!, a => a is ILink { Href: { } href } && href == new Uri(RecipientIri.Value));

        // The delivery is signed as the local actor being followed (bob) — not the instance actor —
        // so the remote verifies the Accept against bob's key.
        Assert.Equal(RecipientIri, job.ActorIri);
    }

    // --- Dispatch: an activity with no registered handler is stored, not dispatched --

    [Fact]
    public async Task ProcessAsync_ActivityWithoutMatchingHandler_StoresActivityAndDispatchesNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        var (_, processor) = BuildProcessorWithFollowHandler(persistence);
        // A Like has no registered handler (only Follow is registered).
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-1",
            Actor = [new Link { Href = new Uri(FollowerIri.Value) }],
            Object = [new Link { Href = new Uri("https://a.domain.local/objects/note-1") }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, like));

        // The activity is still stored (unknown activity types are preserved, not dropped).
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(like.Id!), out var stored));
        Assert.NotNull(stored);

        // Nothing was dispatched (no handler for Like); no follow edge recorded.
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
    }

    // --- Dispatch: exact match wins over a base-type handler -------------------------

    [Fact]
    public async Task ProcessAsync_Activity_WithExactAndBaseHandlers_DispatchesToExactMatch()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, RecipientIri);
        var followHandler = BuildFollowHandler(persistence, out _);
        var baseHandler = new RecordingActivityHandler();
        var processor = new InboxProcessor(persistence, [baseHandler, followHandler]);
        var follow = BuildFollow(FollowerIri, RecipientIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The exact-match Follow handler ran (recorded the edge); the base Activity handler did not.
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        Assert.Empty(baseHandler.Handled);
    }

    // --- Dispatch: a base-type handler catches activities with no more specific match --

    [Fact]
    public async Task ProcessAsync_Activity_WithOnlyBaseHandler_DispatchesToClosestBase()
    {
        var persistence = new InMemoryPersistenceProvider();
        var baseHandler = new RecordingActivityHandler();
        var processor = new InboxProcessor(persistence, [baseHandler]);
        // A Like has no exact handler; the base Activity handler should catch it.
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-2",
            Actor = [new Link { Href = new Uri(FollowerIri.Value) }],
            Object = [new Link { Href = new Uri("https://a.domain.local/objects/note-2") }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, like));

        Assert.Single(baseHandler.Handled);
        Assert.Equal(like, baseHandler.Handled[0].Activity);
    }

    // --- Handler: a Follow with no resolvable actor records nothing --------------------

    [Fact]
    public async Task ProcessAsync_FollowWithNoActor_StoresActivityAndRecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        var (queue, processor) = BuildProcessorWithFollowHandler(persistence);
        // A Follow with no actor (malformed) — the handler records nothing.
        var follow = new Follow
        {
            Id = "https://a.domain.local/activities/follow-noactor",
            Object = [new Link { Href = new Uri(RecipientIri.Value) }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, follow));

        // The activity is stored, but no follow edge is recorded and no Accept is queued.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _));
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        Assert.Equal(0, queue.Count);
    }

    // --- Dispatch: an Accept finalizes a local follower's provisional follow -----------

    [Fact]
    public async Task ProcessAsync_Accept_LocalFollower_FinalizesFollowEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, FollowerIri);
        var processor = BuildProcessorWithAcceptHandler(persistence);
        // alice (local) followed bob: the follow is stored in A's activity store (as the follower).
        var follow = BuildFollow(FollowerIri, RecipientIri);
        await persistence.Activities.PutActivityAsync(follow);
        // bob accepted alice's follow (the Accept is delivered back to alice's inbox).
        var accept = FollowIris.BuildAccept(RecipientIri, follow);

        await processor.ProcessAsync(new InboxDelivery(FollowerIri, accept));

        // The Accept's object references the original follow; the follower (alice, local) now
        // follows the target (bob) — the provisional follow is finalized.
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        var aliceFollowing = await persistence.Follows.GetFollowingAsync(FollowerIri);
        Assert.Contains(RecipientIri, aliceFollowing);
    }

    // --- Dispatch: an Accept for a remote follower's follow is a no-op ------------------

    [Fact]
    public async Task ProcessAsync_Accept_RemoteFollower_DoesNotRecordLocalEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, FollowerIri);
        var processor = BuildProcessorWithAcceptHandler(persistence);
        // A remote follower (charlie, not in A's actor store) is accepted. The Accept references a
        // follow that is not in A's activity store (it belongs to the remote instance).
        var remoteFollower = new Iri("https://c.domain.local/ap/v1/u/charlie");
        var remoteFollow = BuildFollow(remoteFollower, RecipientIri);
        var accept = FollowIris.BuildAccept(RecipientIri, remoteFollow);

        await processor.ProcessAsync(new InboxDelivery(FollowerIri, accept));

        // No local follow edge is recorded (the remote follower's follow is the remote instance's
        // concern; the local store has no such follow to finalize).
        Assert.False(await persistence.Follows.IsFollowingAsync(remoteFollower, RecipientIri));
    }

    // --- Dispatch: a Reject undoes a local follower's follow ---------------------------

    [Fact]
    public async Task ProcessAsync_Reject_LocalFollower_RemovesFollowEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, FollowerIri);
        var processor = BuildProcessorWithRejectHandler(persistence);
        // alice (local) followed bob: the follow is stored, and the edge is already recorded
        // (provisional — the follow was made, pending the followed side's response).
        var follow = BuildFollow(FollowerIri, RecipientIri);
        await persistence.Activities.PutActivityAsync(follow);
        await persistence.Follows.RecordFollowAsync(FollowerIri, RecipientIri);
        Assert.True(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        // bob rejected alice's follow.
        var reject = FollowIris.BuildReject(RecipientIri, follow);

        await processor.ProcessAsync(new InboxDelivery(FollowerIri, reject));

        // The local follow edge is undone.
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, RecipientIri));
        var aliceFollowing = await persistence.Follows.GetFollowingAsync(FollowerIri);
        Assert.DoesNotContain(RecipientIri, aliceFollowing);
    }

    // --- Dispatch: a Reject for a remote follower's follow is a no-op -------------------

    [Fact]
    public async Task ProcessAsync_Reject_RemoteFollower_DoesNotTouchLocalStore()
    {
        var persistence = new InMemoryPersistenceProvider();
        var processor = BuildProcessorWithRejectHandler(persistence);
        var remoteFollower = new Iri("https://c.domain.local/ap/v1/u/charlie");
        var remoteFollow = BuildFollow(remoteFollower, RecipientIri);
        var reject = FollowIris.BuildReject(RecipientIri, remoteFollow);

        await processor.ProcessAsync(new InboxDelivery(FollowerIri, reject));

        // No local follow edge is removed (the remote follower's follow is the remote instance's
        // concern; the local store has no such follow to undo).
        Assert.False(await persistence.Follows.IsFollowingAsync(remoteFollower, RecipientIri));
    }

    // --- Handlers property exposes the registered handlers ----------------------------

    [Fact]
    public void Ctor_WithHandlers_ExposesThemViaHandlers()
    {
        var persistence = new InMemoryPersistenceProvider();
        var followHandler = BuildFollowHandler(persistence, out _);
        var processor = new InboxProcessor(persistence, [followHandler]);

        var handlers = processor.Handlers;
        Assert.Single(handlers);
        Assert.Same(followHandler, handlers[0]);
        Assert.Equal(typeof(Follow), handlers[0].HandledActivityType);
    }

    // --- Guards -------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        var followHandler = BuildFollowHandler(new InMemoryPersistenceProvider(), out _);
        Assert.Throws<ArgumentNullException>(
            () => new InboxProcessor(null!, [followHandler]));
    }

    [Fact]
    public async Task ProcessAsync_NullDelivery_Throws()
    {
        var persistence = new InMemoryPersistenceProvider();
        var (_, processor) = BuildProcessorWithFollowHandler(persistence);

        await Assert.ThrowsAsync<ArgumentNullException>(() => processor.ProcessAsync(null!));
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="FollowActivityHandler"/> wired to a real <see cref="InMemoryDeliveryQueue"/>
    /// + <see cref="DeliveryService"/> + a <see cref="DefaultLocalActorResolver"/> (over the
    /// persistence's actor store), exposing the queue (to assert the scheduled Accept) and the handler.
    /// </summary>
    private static FollowActivityHandler BuildFollowHandler(IPersistenceProvider persistence, out IDeliveryQueue queue)
    {
        queue = new InMemoryDeliveryQueue();
        var delivery = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var localActors = new DefaultLocalActorResolver(persistence);
        return new FollowActivityHandler(persistence, delivery, localActors);
    }

    /// <summary>
    /// Builds an <see cref="InboxProcessor"/> with a single <see cref="FollowActivityHandler"/>,
    /// exposing the delivery queue (to assert the scheduled Accept) and the processor.
    /// </summary>
    private static (IDeliveryQueue Queue, InboxProcessor Processor) BuildProcessorWithFollowHandler(IPersistenceProvider persistence)
    {
        var handler = BuildFollowHandler(persistence, out var queue);
        return (queue, new InboxProcessor(persistence, [handler]));
    }

    /// <summary>
    /// Builds an <see cref="InboxProcessor"/> with a single <see cref="AcceptActivityHandler"/> (wired
    /// to a <see cref="DefaultLocalActorResolver"/> over the persistence's actor store).
    /// </summary>
    private static InboxProcessor BuildProcessorWithAcceptHandler(IPersistenceProvider persistence)
    {
        var handler = new AcceptActivityHandler(persistence, new DefaultLocalActorResolver(persistence));
        return new InboxProcessor(persistence, [handler]);
    }

    /// <summary>
    /// Builds an <see cref="InboxProcessor"/> with a single <see cref="RejectActivityHandler"/> (wired
    /// to a <see cref="DefaultLocalActorResolver"/> over the persistence's actor store).
    /// </summary>
    private static InboxProcessor BuildProcessorWithRejectHandler(IPersistenceProvider persistence)
    {
        var handler = new RejectActivityHandler(persistence, new DefaultLocalActorResolver(persistence));
        return new InboxProcessor(persistence, [handler]);
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{new Uri(followerIri.Value).Host}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Seeds a local actor (Person) in the persistence's actor store, so the
    /// <see cref="ILocalActorResolver"/> resolves it as local.
    /// </summary>
    private static void SeedLocalActor(IPersistenceProvider persistence, Iri actorIri)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        var actor = new KristofferStrube.ActivityStreams.Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        persistence.Actors.PutActorAsync(actor).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A recording handler for the base <see cref="Activity"/> type, used to verify which handler
    /// the processor dispatches to (it catches any activity that has no more specific registered
    /// handler, because the processor walks the type hierarchy).
    /// </summary>
    private sealed class RecordingActivityHandler : ActivityHandlerBase<Activity>
    {
        public List<InboxDelivery> Handled { get; } = [];

        public override Task HandleAsync(InboxDelivery delivery, Activity activity, CancellationToken ct = default)
        {
            Handled.Add(delivery);
            return Task.CompletedTask;
        }
    }
}
