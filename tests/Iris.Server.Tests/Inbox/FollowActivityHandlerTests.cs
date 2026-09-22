using System.Text.Json;
using Iris.Core;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="FollowActivityHandler"/> (the followed side of a follow). Covers the
/// <c>manuallyApprovesFollowers</c> behavior (Resolved Decision #46 / gap J-10): a local person that
/// auto-approves has the follow edge recorded <em>and</em> an <c>Accept</c> scheduled back to the
/// follower; a local person with <c>manuallyApprovesFollowers</c> set has the edge recorded but NO
/// <c>Accept</c> (the operator responds with an explicit <c>Accept</c> or <c>Reject</c>); a community
/// recipient always auto-accepts (the flag does not apply to communities). Also covers the no-op
/// guards (a follow with no resolvable actor, a follow of a non-local actor) and the null-guard
/// contract.
/// </summary>
public sealed class FollowActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri LocalManuallyApproving = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteFollower = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri UnknownActor = new("https://b.domain.local/ap/v1/u/nobody");

    // --- Auto-approve (the default): the edge is recorded AND an Accept is scheduled --------

    [Fact]
    public async Task HandleAsync_LocalPersonAutoApproves_RecordsEdgeAndSchedulesAccept()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson); // no manuallyApprovesFollowers → auto-approve
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, LocalPerson);

        await handler.HandleAsync(new InboxDelivery(LocalPerson, follow), follow);

        // The follow edge is recorded (alice follows bob) ...
        Assert.True(await persistence.Follows.IsFollowingAsync(RemoteFollower, LocalPerson));
        // ... and an Accept is scheduled back to the follower's inbox, signed as the followed actor.
        var job = Assert.Single(await DequeueAllAsync(delivery));
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.IsType<Accept>(job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri);
    }

    // --- manuallyApprovesFollowers: the follow is HELD — pending request only, NO Accept ----

    [Fact]
    public async Task HandleAsync_LocalPersonManuallyApproves_HoldsFollowRequestWithholdsFollowEdge()
    {
        // S34: when the recipient manually approves followers, the inbound follow is HELD for approval —
        // only a pending follow-request edge is recorded, and the directed Follow edge is WITHHELD so the
        // requesting actor does NOT appear in the public followers collection (GET /ap/v1/u/{h}/followers)
        // before the owner Accepts. The operator must respond with an explicit Accept/Reject (the Accept
        // path materializes the Follow edge via ApplyFollowDecisionEdgeAsync).
        var persistence = new InMemoryPersistenceProvider();
        await SeedManuallyApprovingPersonAsync(persistence, LocalManuallyApproving);
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, LocalManuallyApproving);

        await handler.HandleAsync(new InboxDelivery(LocalManuallyApproving, follow), follow);

        // A pending follow request is recorded (the /local/v1/u/{handle}/requests queue lists it) ...
        Assert.True(await persistence.Follows.HasFollowRequestAsync(RemoteFollower, LocalManuallyApproving));
        Assert.Contains(RemoteFollower, await persistence.Follows.GetFollowRequestsAsync(LocalManuallyApproving));
        // ... but the directed Follow edge is WITHHELD (the public followers collection must not list the
        // pending requester before acceptance) ...
        Assert.False(await persistence.Follows.IsFollowingAsync(RemoteFollower, LocalManuallyApproving));
        Assert.DoesNotContain(RemoteFollower, await persistence.Follows.GetFollowersAsync(LocalManuallyApproving));
        // ... and NO Accept is scheduled: the operator must respond with an explicit Accept/Reject.
        Assert.Empty(await DequeueAllAsync(delivery));
    }

    // --- The inbound follow is surfaced on the dedicated follow-request queue (not the outbox) ---

    [Fact]
    public async Task HandleAsync_LocalPerson_InboundFollowLandsInFollowedActorsOutbox()
    {
        // S24-D2: an inbound follow is surfaced on the DEDICATED follow-request queue
        // (IFollowStore.GetFollowRequestsAsync, served by /local/v1/u/{handle}/requests), NOT in the
        // followed actor's outbox. The old behavior recorded the foreign Follow in the outbox so a UI
        // could enumerate it, but that polluted the public outbox with foreign (non-self-authored)
        // activities (S24-D2) — the queue surface supersedes the outbox surface, so the outbox now
        // stays clean (only the actor's own authored activities).
        var persistence = new InMemoryPersistenceProvider();
        await SeedManuallyApprovingPersonAsync(persistence, LocalManuallyApproving);
        var (handler, _) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, LocalManuallyApproving);

        await handler.HandleAsync(new InboxDelivery(LocalManuallyApproving, follow), follow);

        // The follow is on the dedicated request queue (the /local/v1/u/{handle}/requests surface).
        Assert.Contains(RemoteFollower, await persistence.Follows.GetFollowRequestsAsync(LocalManuallyApproving));
        // ... and is NOT in the followed actor's outbox (the outbox is the actor's own authored content).
        Assert.DoesNotContain(await persistence.Activities.GetOutboxAsync(LocalManuallyApproving), a => a.Id == follow.Id);

        // (Same for the auto-approve path — the request queue is the surface, the outbox stays clean.)
        var persistence2 = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence2, LocalPerson);
        var (handler2, _) = BuildHandler(persistence2);
        var follow2 = BuildFollow(RemoteFollower, LocalPerson);
        await handler2.HandleAsync(new InboxDelivery(LocalPerson, follow2), follow2);
        Assert.DoesNotContain(await persistence2.Activities.GetOutboxAsync(LocalPerson), a => a.Id == follow2.Id);
    }

    [Fact]
    public async Task HandleAsync_ManuallyApprovesSetToFalse_AutoApproves()
    {
        // An explicit false is equivalent to the default (auto-approve) — the flag is only meaningful
        // when true.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonWithFlagAsync(persistence, LocalPerson, JsonDocument.Parse("false").RootElement.Clone());
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, LocalPerson);

        await handler.HandleAsync(new InboxDelivery(LocalPerson, follow), follow);

        Assert.True(await persistence.Follows.IsFollowingAsync(RemoteFollower, LocalPerson));
        Assert.IsType<Accept>(Assert.Single(await DequeueAllAsync(delivery)).Activity);
    }

    // --- Community recipient: the flag does not apply (always auto-accepts) ----------------

    [Fact]
    public async Task HandleAsync_LocalCommunity_RecordsCommunityFollowAndSchedulesAccept()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(new Group
        {
            Id = Community.Value,
            Name = ["Iris"],
            PreferredUsername = "iris",
        });
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, Community);

        await handler.HandleAsync(new InboxDelivery(Community, follow), follow);

        // The follow is recorded in the community's follows set (the community follows the follower)
        // ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowsAsync(Community));
        // ... and in the community's followers set (F-24: the follower follows the community), so the
        // community's `followers` collection lists the follower ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowersAsync(Community));
        // ... and an Accept is scheduled, signed as the community.
        var job = Assert.Single(await DequeueAllAsync(delivery));
        Assert.IsType<Accept>(job.Activity);
        Assert.Equal(Community, job.ActorIri);
    }

    [Fact]
    public async Task HandleAsync_LocalCommunity_RecordsFollowerInFollowersSet()
    {
        // F-24: a follow of a local community records BOTH directions — the community follows the
        // follower (the follows set, so the follower's content reaches the community's members) and the
        // follower follows the community (the followers set, so the community's `followers` collection
        // lists the follower). Before F-24 only the follows edge was recorded, so the `followers`
        // collection was always empty.
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(new Group
        {
            Id = Community.Value,
            Name = ["Iris"],
            PreferredUsername = "iris",
        });
        var (handler, _) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, Community);

        await handler.HandleAsync(new InboxDelivery(Community, follow), follow);

        // Both edges are recorded: the follows set (community → follower) and the followers set
        // (follower → community). The follows edge was pre-F-24; the followers edge is F-24.
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowsAsync(Community));
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowersAsync(Community));
    }

    // --- Community manuallyApprovesMembers: follow held as a join request, NO Accept --------

    [Fact]
    public async Task HandleAsync_LocalCommunityManuallyApproves_HoldsJoinRequestAndSchedulesNoAccept()
    {
        // A community with manuallyApprovesMembers set does NOT auto-accept an inbound follow (a join —
        // members are followers, change 221): the community's follows edge is still recorded (the
        // follower's content can reach members via the federation path) and a pending join request is
        // recorded, but the membership (followers) edge is WITHHELD and NO Accept is scheduled — the
        // creator responds with an explicit Accept/Reject via the community join-request endpoints.
        var persistence = new InMemoryPersistenceProvider();
        await SeedCommunityWithFlagAsync(persistence, Community, JsonDocument.Parse("true").RootElement.Clone());
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, Community);

        await handler.HandleAsync(new InboxDelivery(Community, follow), follow);

        // The community follows the follower (the follows edge) ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetFollowsAsync(Community));
        // ... but the membership (followers) edge is withheld ...
        Assert.DoesNotContain(RemoteFollower, await persistence.Communities.GetFollowersAsync(Community));
        // ... and a pending join request is recorded for the creator to approve ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetJoinRequestsAsync(Community));
        // ... with NO Accept scheduled: the creator must respond with an explicit Accept/Reject.
        Assert.Empty(await DequeueAllAsync(delivery));
    }

    // --- The inbound follow of a community is surfaced on the join-request queue (not the outbox) --

    [Fact]
    public async Task HandleAsync_LocalCommunity_InboundFollowLandsInCommunityOutbox()
    {
        // S24-D2: an inbound follow of a local community is surfaced on the DEDICATED join-request queue
        // (ICommunityStore.GetJoinRequestsAsync, served by /local/v1/c/{name}/requests when the gate is
        // on), NOT in the community's outbox. The old behavior recorded the foreign Follow in the
        // community's outbox so a UI could enumerate it, but that polluted the public outbox with a
        // foreign (non-self-authored) activity (S24-D2) — the join-request queue supersedes the outbox
        // surface, so the outbox now stays clean.
        var persistence = new InMemoryPersistenceProvider();
        await SeedCommunityWithFlagAsync(persistence, Community, JsonDocument.Parse("true").RootElement.Clone());
        var (handler, _) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, Community);

        await handler.HandleAsync(new InboxDelivery(Community, follow), follow);

        // The follow is on the dedicated join-request queue (the /local/v1/c/{name}/requests surface) ...
        Assert.Contains(RemoteFollower, await persistence.Communities.GetJoinRequestsAsync(Community));
        // ... and is NOT in the community's outbox (the outbox is the actor's own authored content).
        Assert.DoesNotContain(await persistence.Activities.GetOutboxAsync(Community), a => a.Id == follow.Id);

        // (Same for the auto-approve path — the follow edge is recorded, the outbox stays clean.)
        var persistence2 = new InMemoryPersistenceProvider();
        await persistence2.Communities.PutCommunityAsync(new Group { Id = Community.Value, Name = ["Iris"] });
        var (handler2, _) = BuildHandler(persistence2);
        var follow2 = BuildFollow(RemoteFollower, Community);
        await handler2.HandleAsync(new InboxDelivery(Community, follow2), follow2);
        // Auto-approve: the follow edge is recorded (the community follows the follower) ...
        Assert.Contains(RemoteFollower, await persistence2.Communities.GetFollowsAsync(Community));
        // ... and the outbox stays clean (no foreign Follow recorded).
        Assert.DoesNotContain(await persistence2.Activities.GetOutboxAsync(Community), a => a.Id == follow2.Id);
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_FollowWithNoActor_RecordsNothingAndSchedulesNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var (handler, delivery) = BuildHandler(persistence);
        var follow = new Follow
        {
            Id = "https://a.domain.local/activities/follow-noactor",
            Object = [new Link { Href = new Uri(LocalPerson.Value) }],
        };

        await handler.HandleAsync(new InboxDelivery(LocalPerson, follow), follow);

        Assert.Empty(await DequeueAllAsync(delivery));
    }

    [Fact]
    public async Task HandleAsync_FollowOfNonLocalActor_NoOp()
    {
        // The recipient is neither a local person nor a local community → not this instance's concern.
        var persistence = new InMemoryPersistenceProvider();
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, UnknownActor);

        await handler.HandleAsync(new InboxDelivery(UnknownActor, follow), follow);

        Assert.Empty(await DequeueAllAsync(delivery));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static (FollowActivityHandler Handler, RecordingDeliveryService Delivery) BuildHandler(
        IPersistenceProvider persistence)
    {
        var delivery = new RecordingDeliveryService();
        var handler = new FollowActivityHandler(
            persistence, delivery, new DefaultLocalActorResolver(persistence), new IdMinter());
        return (handler, delivery);
    }

    private static Task<List<DeliveryJob>> DequeueAllAsync(RecordingDeliveryService delivery) => Task.FromResult(delivery.Delivered);

    private static Task SeedPersonAsync(IPersistenceProvider persistence, Iri actorIri)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        return persistence.Actors.PutActorAsync(new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        });
    }

    private static Task SeedManuallyApprovingPersonAsync(IPersistenceProvider persistence, Iri actorIri)
        => SeedPersonWithFlagAsync(persistence, actorIri, JsonDocument.Parse("true").RootElement.Clone());

    private static Task SeedPersonWithFlagAsync(IPersistenceProvider persistence, Iri actorIri, JsonElement flag)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] = flag;
        return persistence.Actors.PutActorAsync(actor);
    }

    private static Task SeedCommunityWithFlagAsync(IPersistenceProvider persistence, Iri communityIri, JsonElement flag)
    {
        var name = new Uri(communityIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        var community = new Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [name],
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        // A community's members are its followers (change 221), so the gate on an inbound follow (a join)
        // is manuallyApprovesMembers, not manuallyApprovesFollowers.
        community.ExtensionData[ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] = flag;
        return persistence.Communities.PutCommunityAsync(community);
    }

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{new Uri(followerIri.Value).Host}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// An <see cref="IDeliveryService"/> that records every scheduled delivery (instead of enqueuing) so
    /// a test can assert on <see cref="Delivered"/> — the target inbox, the activity, and the signing
    /// actor.
    /// </summary>
    private sealed class RecordingDeliveryService : IDeliveryService
    {
        public List<DeliveryJob> Delivered { get; } = [];

        public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default)
            => DeliverAsync(inboxIri, activity, actorIri: null, ct);

        public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
        {
            Delivered.Add(new DeliveryJob(inboxIri, activity, actorIri));
            return Task.CompletedTask;
        }

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default)
            => DeliverToActorAsync(recipientIri, activity, actorIri: null, ct);

        public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default)
            => DeliverAsync(recipientIri.InboxOf(), activity, actorIri, ct);
    }
}
