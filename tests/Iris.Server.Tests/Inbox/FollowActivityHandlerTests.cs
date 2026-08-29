using System.Text.Json;
using Iris.Core;
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

    // --- manuallyApprovesFollowers: the edge is recorded, but NO Accept --------------------

    [Fact]
    public async Task HandleAsync_LocalPersonManuallyApproves_RecordsEdgeAndSchedulesNoAccept()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedManuallyApprovingPersonAsync(persistence, LocalManuallyApproving);
        var (handler, delivery) = BuildHandler(persistence);
        var follow = BuildFollow(RemoteFollower, LocalManuallyApproving);

        await handler.HandleAsync(new InboxDelivery(LocalManuallyApproving, follow), follow);

        // The follow edge IS recorded (the follower's content can reach the local followers' outboxes
        // via the federation path) ...
        Assert.True(await persistence.Follows.IsFollowingAsync(RemoteFollower, LocalManuallyApproving));
        // ... but NO Accept is scheduled: the operator must respond with an explicit Accept/Reject.
        Assert.Empty(await DequeueAllAsync(delivery));
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

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FollowActivityHandler(
            null!, new RecordingDeliveryService(), new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullDelivery_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FollowActivityHandler(
            new InMemoryPersistenceProvider(), null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FollowActivityHandler(
            new InMemoryPersistenceProvider(), new RecordingDeliveryService(), null!));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static (FollowActivityHandler Handler, RecordingDeliveryService Delivery) BuildHandler(
        IPersistenceProvider persistence)
    {
        var delivery = new RecordingDeliveryService();
        var handler = new FollowActivityHandler(
            persistence, delivery, new DefaultLocalActorResolver(persistence));
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
