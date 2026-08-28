using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests: the <see cref="CreateActivityHandler"/> — the dedicated handler for an inbound
/// <see cref="Create"/>. When the recipient is a local person it records the <see cref="Create"/> in that
/// person's outbox (the author's own post, J-8); when the recipient is a local community it records it in
/// the community's local members' outboxes (the "followed content" half, delegating to the shared
/// <see cref="CommunityContentRecorder"/>). Covers: recording in the local person's outbox, skipping a
/// non-local (remote) person, the community member-recording path, newest-first ordering, no-op for an
/// unknown recipient, and the null-guard contract.
/// </summary>
public sealed class CreateActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemotePerson = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteMember = new("https://a.domain.local/ap/v1/u/dave");

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
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CreateActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static CreateActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence, new DefaultLocalActorResolver(persistence));

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

    private static Create BuildCreate(Iri authorIri) => new()
    {
        Id = $"{authorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(authorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = $"{authorIri}/notes/{Guid.NewGuid():N}",
                Content = ["hello"],
            },
        ],
    };

    private static List<string> OutboxIds(IReadOnlyList<IObjectOrLink> outbox)
        => outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
}
