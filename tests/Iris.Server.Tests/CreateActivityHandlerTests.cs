using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests: the <see cref="CreateActivityHandler"/> — the dedicated handler for an inbound
/// <see cref="Create"/>. When the recipient is a local person it records the <see cref="Create"/> in that
/// person's outbox (the author's own post, J-8) <em>and</em> federates it to the author's remote followers
/// (J-18); when the recipient is a local community it records it in the community's local members' outboxes
/// (the "followed content" half, delegating to the shared <see cref="CommunityContentRecorder"/>). Covers:
/// recording in the local person's outbox, federating the post to remote (but not local) followers signed
/// as the author, skipping a non-local (remote) person, the community member-recording path, newest-first
/// ordering, no-op for an unknown recipient, and the null-guard contract.
/// </summary>
public sealed class CreateActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteMember = new("https://a.domain.local/ap/v1/u/dave");
    private static readonly Iri RemoteFollower = new("https://c.domain.local/ap/v1/u/erin");
    private static readonly Iri LocalFollower = new("https://b.domain.local/ap/v1/u/frank");

    // --- Local person: the author's own post (J-8) -----------------------------------------

    [Fact]
    public async Task HandleAsync_LocalPersonRecipient_RecordsInPersonOutbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(LocalPerson); // the person posts their own note

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The Create is recorded in the local person's own outbox (newest first).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalPerson);
        var ids = OutboxIds(outbox);
        Assert.Contains(create.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_MultipleCreates_RecordsNewestFirst()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var first = BuildCreate(LocalPerson);
        var second = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, first), first);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, second), second);

        // The person outbox is newest first: second precedes first.
        var ids = OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson));
        Assert.Equal([second.Id!, first.Id!], ids);
    }

    // --- Outbound federation to the author's remote followers (J-18) ----------------------

    [Fact]
    public async Task HandleAsync_LocalPersonWithRemoteFollower_FederatesCreateToRemoteFollowerInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        // erin (remote) follows bob (local) → the follow edge is recorded.
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The post is still surfaced in the author's outbox (J-8) ...
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
        // ... AND federated to the remote follower's inbox, signed as the author (bob).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.Same(create, job.Activity);
        Assert.Equal(LocalPerson, job.ActorIri); // signed as the author, not the instance actor
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithLocalFollower_SkipsLocalFollower()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        // frank (local) follows bob (local) → local follower.
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // A local follower already sees the post via the author's outbox → no cross-instance delivery.
        Assert.Empty(delivery.Delivered);
        // The post is still recorded in the author's own outbox.
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithMixedFollowers_FederatesOnlyToRemoteFollowers()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(RemoteFollower, LocalPerson); // remote → deliver
        await persistence.Follows.RecordFollowAsync(LocalFollower, LocalPerson);  // local  → skip
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // Exactly one delivery: to the remote follower's inbox (the local follower is skipped).
        var job = Assert.Single(delivery.Delivered);
        Assert.Equal(RemoteFollower.InboxOf(), job.InboxIri);
        Assert.Equal(LocalFollower.InboxOf(), LocalFollower.InboxOf()); // (sanity: local inbox is not targeted)
        Assert.DoesNotContain(delivery.Delivered, j => j.InboxIri == LocalFollower.InboxOf());
    }

    [Fact]
    public async Task HandleAsync_LocalPersonWithNoFollowers_RecordsButDoesNotDeliver()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson); // no followers
        var delivery = new RecordingDeliveryService();
        var sut = BuildHandler(persistence, delivery);
        var create = BuildCreate(LocalPerson);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No followers → nothing scheduled, but the post is still surfaced in the author's outbox.
        Assert.Empty(delivery.Delivered);
        Assert.Contains(create.Id, OutboxIds(await persistence.Activities.GetOutboxAsync(LocalPerson)));
    }

    // --- Remote person: not this instance's concern ---------------------------------------

    [Fact]
    public async Task HandleAsync_RemotePersonRecipient_NoOp()
    {
        // The recipient is not a local person (no such actor in the store) → no recording. The remote
        // instance records the post in its own outbox.
        var persistence = new InMemoryPersistenceProvider();
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson);

        await sut.HandleAsync(new InboxDelivery(RemotePerson, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(RemotePerson));
    }

    // --- Local community: the "followed content" half --------------------------------------

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_RecordsInMemberOutbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddMemberAsync(Community, LocalMember);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson); // a remote follower publishes to the community

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // The Create is recorded in the local member's outbox (the community's unified feed surfaces it).
        var memberOutbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        Assert.Contains(create.Id, OutboxIds(memberOutbox));
        // And NOT in the community's own outbox (a community has no personal outbox of its own here).
        Assert.Empty(await persistence.Activities.GetOutboxAsync(Community));
    }

    [Fact]
    public async Task HandleAsync_CommunityWithRemoteMember_SkipsRemoteMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddMemberAsync(Community, LocalMember);
        await persistence.Communities.AddMemberAsync(Community, RemoteMember); // not seeded as local
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemotePerson);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // Only the local member's outbox is recorded; the remote member's is untouched.
        var localIds = OutboxIds(await persistence.Activities.GetOutboxAsync(LocalMember));
        Assert.Contains(create.Id, localIds);
        Assert.Empty(await persistence.Activities.GetOutboxAsync(RemoteMember));
    }

    // --- Guards --------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_UnknownRecipient_NoOp()
    {
        // The recipient is neither a local person nor a local community → no recording.
        var persistence = new InMemoryPersistenceProvider();
        var other = new Iri("https://b.domain.local/ap/v1/u/unknown");
        var sut = BuildHandler(persistence);
        var create = BuildCreate(other);

        await sut.HandleAsync(new InboxDelivery(other, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(other));
    }

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CreateActivityHandler(
            null!, new RecordingDeliveryService(), new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullDelivery_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CreateActivityHandler(
            new InMemoryPersistenceProvider(), null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CreateActivityHandler(
            new InMemoryPersistenceProvider(), new RecordingDeliveryService(), null!));
    }

    // --- F-12: reply (inReplyTo) edge recording -------------------------------------------

    [Fact]
    public async Task HandleAsync_ReplyRecordsParentToChildEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var parentNote = new Iri("https://a.domain.local/ap/v1/u/alice/notes/parent");
        var create = BuildCreate(LocalPerson, inReplyTo: parentNote.Value);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // The stored reply's inReplyTo (the parent) → the reply note IRI edge is recorded.
        var replyIri = EmbeddedNoteId(create);
        Assert.True(await persistence.Replies.HasReplyAsync(parentNote, replyIri));
        Assert.Equal([replyIri], await persistence.Replies.GetRepliesAsync(parentNote));
    }

    [Fact]
    public async Task HandleAsync_ToplevelNoteRecordsNoReplyEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(LocalPerson); // no inReplyTo

        await sut.HandleAsync(new InboxDelivery(LocalPerson, create), create);

        // No parent → no reply edge anywhere.
        var replyIri = EmbeddedNoteId(create);
        Assert.False(await persistence.Replies.HasReplyAsync(replyIri, replyIri));
    }

    [Fact]
    public async Task HandleAsync_CommunityReplyRecordsEdge()
    {
        // A reply whose parent is a note is recorded even when delivered to a local community.
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedLocalActorAsync(persistence, LocalMember);
        await persistence.Communities.AddMemberAsync(Community, LocalMember);
        var sut = BuildHandler(persistence);
        var parentNote = new Iri("https://a.domain.local/ap/v1/u/alice/notes/parent");
        var create = BuildCreate(LocalMember, inReplyTo: parentNote.Value);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        var replyIri = EmbeddedNoteId(create);
        Assert.True(await persistence.Replies.HasReplyAsync(parentNote, replyIri));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static CreateActivityHandler BuildHandler(
        IPersistenceProvider persistence, IDeliveryService? delivery = null)
        => new(
            persistence,
            delivery ?? new RecordingDeliveryService(),
            new DefaultLocalActorResolver(persistence));

    private static Task SeedLocalActorAsync(IPersistenceProvider persistence, Iri actorIri)
    {
        var handle = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last();
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        return persistence.Actors.PutActorAsync(actor);
    }

    private static Create BuildCreate(Iri authorIri, string? inReplyTo = null)
    {
        var note = new Note
        {
            Id = $"{authorIri}/notes/{Guid.NewGuid():N}",
            Content = ["hello"],
        };
        if (inReplyTo is not null)
        {
            note.InReplyTo = [new Link { Href = new Uri(inReplyTo) }];
        }

        return new Create
        {
            Id = $"{authorIri}/creates/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(authorIri.Value) }],
            Object = [note],
        };
    }

    private static Iri EmbeddedNoteId(Create create)
        => new(create.ExtractEmbeddedObject()!.Id!);

    private static List<string> OutboxIds(IReadOnlyList<IObjectOrLink> outbox)
        => outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();

    /// <summary>
    /// An <see cref="IDeliveryService"/> that records every scheduled delivery (instead of enqueuing) so a
    /// test can assert on <see cref="Delivered"/> — the target inbox, the activity, and the signing actor.
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
