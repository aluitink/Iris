using Iris.Core;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Inbox;

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

    [Fact]
    public async Task ProcessAsync_FollowOfCommunity_RecordsCommunityFollowAndQueuesAccept()
    {
        // A remote actor (alice) follows a local community (the Group). The follow is recorded in the
        // community's follows set (not the person-follow store), and an Accept is queued — but a follow
        // of a community is NOT a membership grant (alice is not added to the community's members).
        var persistence = new InMemoryPersistenceProvider();
        var communityIri = new Iri("https://b.domain.local/ap/v1/c/iris");
        var community = new Group
        {
            Id = communityIri.Value,
            Name = ["Iris"],
            PreferredUsername = "iris",
        };
        await persistence.Communities.PutCommunityAsync(community);
        // The community has a local member (so the feed has somewhere to surface content later); alice
        // is the remote follower and is NOT a member.
        var localMember = new Iri("https://b.domain.local/ap/v1/u/bob");
        SeedLocalActor(persistence, localMember);
        await persistence.Communities.AddMemberAsync(communityIri, localMember);

        var (queue, processor) = BuildProcessorWithFollowHandler(persistence);
        var follow = BuildFollow(FollowerIri, communityIri);

        await processor.ProcessAsync(new InboxDelivery(communityIri, follow));

        // The activity is stored under its IRI.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var stored));
        Assert.Equal(follow.Id, stored!.Id);

        // The follow is recorded in the community's follows set (the community follows alice).
        Assert.Contains(FollowerIri, await persistence.Communities.GetFollowsAsync(communityIri));

        // A follow of a community is NOT a membership grant: alice is not a member, and the
        // person-follow store has no edge (the community is not a Person in the actor store).
        Assert.False(await persistence.Communities.IsMemberAsync(communityIri, FollowerIri));
        Assert.False(await persistence.Follows.IsFollowingAsync(FollowerIri, communityIri));

        // An Accept is queued to the follower's inbox, signed as the community.
        Assert.Equal(1, queue.Count);
        var job = await queue.TryDequeueAsync();
        Assert.NotNull(job);
        Assert.Equal(FollowerIri.InboxOf(), job!.InboxIri);
        Assert.IsType<Accept>(job.Activity);
        Assert.Equal(communityIri, job.ActorIri);
    }

    // --- Dispatch: an Announce is recorded in the outbox and propagated to local followers --

    [Fact]
    public async Task ProcessAsync_Announce_WithLocalFollowers_RecordsInOutboxAndPropagatesToEachLocalFollowerInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, RecipientIri); // bob (local, the announcer)
        var (queue, processor) = BuildProcessorWithAnnounceHandler(persistence);
        // bob has two local followers (carol, dave) and one remote follower (charlie).
        var carol = new Iri("https://b.domain.local/ap/v1/u/carol");
        var dave = new Iri("https://b.domain.local/ap/v1/u/dave");
        var charlie = new Iri("https://c.domain.local/ap/v1/u/charlie");
        SeedLocalActor(persistence, carol);
        SeedLocalActor(persistence, dave);
        await persistence.Follows.RecordFollowAsync(carol, RecipientIri);
        await persistence.Follows.RecordFollowAsync(dave, RecipientIri);
        await persistence.Follows.RecordFollowAsync(charlie, RecipientIri);

        var objectIri = new Iri("https://a.domain.local/objects/note-1");
        var announce = BuildAnnounce(RecipientIri, objectIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, announce));

        // The announce is stored under its IRI (by the processor) and recorded in bob's outbox.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out var stored));
        Assert.Equal(announce.Id, stored!.Id);
        var outbox = await persistence.Activities.GetOutboxAsync(RecipientIri);
        Assert.Single(outbox);
        Assert.IsType<Announce>(outbox[0]);

        // The AnnounceActivityHandler propagated the announce to each follower (mirroring Create):
        // local followers (carol, dave) see the boost via their outbox on this instance (recorded
        // directly — no cross-instance delivery); the remote follower (charlie) needs a cross-instance
        // delivery (one job in the delivery queue).
        var carolOutbox = await persistence.Activities.GetOutboxAsync(carol);
        var daveOutbox = await persistence.Activities.GetOutboxAsync(dave);
        Assert.Single(carolOutbox);
        Assert.Single(daveOutbox);
        var carolPropagated = Assert.IsType<Announce>(carolOutbox[0]);
        var davePropagated = Assert.IsType<Announce>(daveOutbox[0]);
        Assert.Equal(announce.Id, carolPropagated.Id); // deterministic IRI reused
        Assert.Equal(announce.Id, davePropagated.Id);
        // The propagated form is addressed (to) to its follower, cc'd to the announcer.
        Assert.Equal(new Uri(carol.Value), ((Link)carolPropagated.To!.First()).Href);
        Assert.Equal(new Uri(RecipientIri.Value), ((Link)carolPropagated.Cc!.First()).Href);
        Assert.Equal(new Uri(dave.Value), ((Link)davePropagated.To!.First()).Href);
        Assert.Equal(new Uri(RecipientIri.Value), ((Link)davePropagated.Cc!.First()).Href);

        // The remote follower (charlie) receives a cross-instance delivery (one job).
        Assert.Equal(1, queue.Count);
        var job = (await queue.TryDequeueAsync())!;
        Assert.Equal(charlie.InboxOf(), job.InboxIri);
        var remotePropagated = Assert.IsType<Announce>(job.Activity);
        Assert.Equal(announce.Id, remotePropagated.Id); // deterministic IRI reused
        Assert.Equal(new Uri(charlie.Value), ((Link)remotePropagated.To!.First()).Href);
        Assert.Equal(new Uri(RecipientIri.Value), ((Link)remotePropagated.Cc!.First()).Href);
        // The delivery is signed as the announcer (bob).
        Assert.Equal(RecipientIri, job.ActorIri);
    }

    // --- Handler: an Announce with no local followers is still recorded in the outbox --

    [Fact]
    public async Task ProcessAsync_Announce_WithNoLocalFollowers_RecordsInOutboxAndPropagatesNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, RecipientIri); // bob (local, the announcer) — no followers
        var (queue, processor) = BuildProcessorWithAnnounceHandler(persistence);
        var objectIri = new Iri("https://a.domain.local/objects/note-2");
        var announce = BuildAnnounce(RecipientIri, objectIri);

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, announce));

        // The announce is recorded in bob's outbox, but nothing is propagated (no local followers).
        var outbox = await persistence.Activities.GetOutboxAsync(RecipientIri);
        Assert.Single(outbox);
        Assert.IsType<Announce>(outbox[0]);
        Assert.Equal(0, queue.Count);
    }

    // --- Handler: an Announce to a remote recipient is a no-op ----------------------------

    [Fact]
    public async Task ProcessAsync_Announce_RemoteRecipient_StoresActivityAndRecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        // The recipient is NOT seeded (it is a remote actor) → the handler records nothing.
        var (queue, processor) = BuildProcessorWithAnnounceHandler(persistence);
        var remoteRecipient = new Iri("https://c.domain.local/ap/v1/u/charlie");
        var objectIri = new Iri("https://c.domain.local/objects/note-1");
        var announce = BuildAnnounce(remoteRecipient, objectIri);

        await processor.ProcessAsync(new InboxDelivery(remoteRecipient, announce));

        // The activity is stored (unknown/remote announce is preserved), but no outbox entry is
        // recorded (the recipient is not a local actor) and nothing is propagated.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out _));
        Assert.Empty(await persistence.Activities.GetOutboxAsync(remoteRecipient));
        Assert.Equal(0, queue.Count);
    }

    // --- Handler: an Announce with no resolvable actor/object records nothing -------------

    [Fact]
    public async Task ProcessAsync_AnnounceWithNoActor_StoresActivityAndRecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        SeedLocalActor(persistence, RecipientIri);
        var (queue, processor) = BuildProcessorWithAnnounceHandler(persistence);
        // An Announce with no actor (malformed) — the handler records nothing.
        var announce = new Announce
        {
            Id = "https://b.domain.local/announces/noactor",
            Object = [new Link { Href = new Uri("https://a.domain.local/objects/note-3") }],
        };

        await processor.ProcessAsync(new InboxDelivery(RecipientIri, announce));

        // The activity is stored, but no outbox entry is recorded and nothing is propagated.
        Assert.True(await persistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out _));
        Assert.Empty(await persistence.Activities.GetOutboxAsync(RecipientIri));
        Assert.Equal(0, queue.Count);
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
        // bob accepted alice's follow (the Accept is delivered back to alice's inbox). The Accept's
        // own id is minted by the server (decision 055); the test does not predict it — the handler
        // only needs the Accept to reference the original follow by IRI.
        var accept = FollowIris.BuildAccept(new IdMinter(), RecipientIri, follow);

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
        var accept = FollowIris.BuildAccept(new IdMinter(), RecipientIri, remoteFollow);

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
        // bob rejected alice's follow (the Reject's own id is minted by the server — decision 055).
        var reject = FollowIris.BuildReject(new IdMinter(), RecipientIri, follow);

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
        var reject = FollowIris.BuildReject(new IdMinter(), RecipientIri, remoteFollow);

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
        return new FollowActivityHandler(persistence, delivery, localActors, new IdMinter());
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

    private static Announce BuildAnnounce(Iri announcerIri, Iri objectIri) => new()
    {
        Id = $"{announcerIri}/announces/{objectIri}",
        Actor = [new Link { Href = new Uri(announcerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Builds an <see cref="InboxProcessor"/> with a single <see cref="AnnounceActivityHandler"/>
    /// (wired to a real <see cref="InMemoryDeliveryQueue"/> + <see cref="DeliveryService"/> + a
    /// <see cref="DefaultLocalActorResolver"/> over the persistence's actor store), exposing the
    /// delivery queue (to assert the scheduled propagations) and the processor.
    /// </summary>
    private static (IDeliveryQueue Queue, InboxProcessor Processor) BuildProcessorWithAnnounceHandler(IPersistenceProvider persistence)
    {
        var queue = new InMemoryDeliveryQueue();
        var delivery = new DeliveryService(queue, NullLogger<DeliveryService>.Instance);
        var localActors = new DefaultLocalActorResolver(persistence);
        var handler = new AnnounceActivityHandler(persistence, delivery, localActors);
        return (queue, new InboxProcessor(persistence, [handler]));
    }

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
