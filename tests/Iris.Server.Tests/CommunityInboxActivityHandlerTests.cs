using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 5 unit tests: the <see cref="CommunityInboxActivityHandler"/> — the handler that records
/// content activities delivered to a community's inbox (<see cref="Create"/>, <see cref="Like"/>,
/// <see cref="Announce"/>) in each of the community's local members' outboxes, so the content appears in
/// the community's unified feed (the <see cref="ICommunityFeedService"/> merges the members' outboxes).
/// Covers: recording to every local member's outbox (newest first), skipping remote members (the remote
/// instance's concern), no-op when the recipient is not a local community, no-op when the community has
/// no members, and the null-guard contract.
/// </summary>
public sealed class CommunityInboxActivityHandlerTests
{
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri OtherLocalMember = new("https://b.domain.local/ap/v1/u/carol");
    private static readonly Iri RemoteMember = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri RemoteAuthor = new("https://a.domain.local/ap/v1/u/alice");

    // --- Recording to local members -----------------------------------------------------

    [Fact]
    public async Task HandleAsync_Create_WithLocalMember_RecordsInMemberOutbox()
    {
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // The Create is recorded in the single local member's outbox (the activity itself, newest first).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(create.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_Create_WithMultipleLocalMembers_RecordsInEachMemberOutbox()
    {
        var (persistence, _) = BuildWithLocalMember();
        await SeedLocalActorAsync(persistence, OtherLocalMember);
        await persistence.Communities.AddMemberAsync(Community, OtherLocalMember);
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // The Create is recorded in BOTH local members' outboxes (one entry each).
        var bobIds = (await persistence.Activities.GetOutboxAsync(LocalMember))
            .Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        var carolIds = (await persistence.Activities.GetOutboxAsync(OtherLocalMember))
            .Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(create.Id, bobIds);
        Assert.Contains(create.Id, carolIds);
    }

    [Fact]
    public async Task HandleAsync_Like_RecordsInMemberOutbox()
    {
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var like = BuildLike(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(like.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_Announce_RecordsInMemberOutbox()
    {
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var announce = BuildAnnounce(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, announce), announce);

        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(announce.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_MultipleContents_RecordsNewestFirst()
    {
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var first = BuildCreate(RemoteAuthor);
        var second = BuildCreate(RemoteAuthor);

        // Two content activities, in order (first, then second). The member outbox is newest first, so
        // second precedes first.
        await sut.HandleAsync(new InboxDelivery(Community, first), first);
        await sut.HandleAsync(new InboxDelivery(Community, second), second);

        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Equal([second.Id!, first.Id!], ids);
    }

    // --- Remote members are skipped -----------------------------------------------------

    [Fact]
    public async Task HandleAsync_RemoteMemberIsSkipped()
    {
        // A community can have a member that is a remote actor (on another instance). Only local
        // members have their outboxes recorded here: the remote member's instance receives the content
        // via its own federation path.
        var (persistence, _) = BuildWithLocalMember();
        await persistence.Communities.AddMemberAsync(Community, RemoteMember); // not seeded as local
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        // Only the local member's outbox is recorded; the remote member's outbox is untouched.
        var bobIds = (await persistence.Activities.GetOutboxAsync(LocalMember))
            .Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(create.Id, bobIds);
        var remoteOutbox = await persistence.Activities.GetOutboxAsync(RemoteMember);
        Assert.Empty(remoteOutbox);
    }

    // --- Guards --------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_UnknownCommunity_NoOp()
    {
        // The recipient is not a local community (no community in the store) → no recording.
        var (persistence, _) = BuildWithLocalMember();
        var otherCommunity = new Iri("https://b.domain.local/ap/v1/c/other");
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(otherCommunity, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(LocalMember));
    }

    [Fact]
    public async Task HandleAsync_NoMembers_NoOp()
    {
        // A local community with no members → nothing to record.
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        var sut = BuildHandler(persistence);
        var create = BuildCreate(RemoteAuthor);

        await sut.HandleAsync(new InboxDelivery(Community, create), create);

        Assert.Empty(await persistence.Activities.GetOutboxAsync(LocalMember));
    }

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CommunityInboxActivityHandler(
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CommunityInboxActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static (InMemoryPersistenceProvider Persistence, Iri Member) BuildWithLocalMember()
    {
        var persistence = new InMemoryPersistenceProvider();
        persistence.Communities.PutCommunityAsync(BuildCommunity()).GetAwaiter().GetResult();
        SeedLocalActor(persistence, LocalMember);
        persistence.Communities.AddMemberAsync(Community, LocalMember).GetAwaiter().GetResult();
        return (persistence, LocalMember);
    }

    private static CommunityInboxActivityHandler BuildHandler(IPersistenceProvider persistence)
    {
        var localActors = new DefaultLocalActorResolver(persistence);
        return new CommunityInboxActivityHandler(persistence, localActors);
    }

    private static void SeedLocalActor(IPersistenceProvider persistence, Iri actorIri)
    {
        SeedLocalActorAsync(persistence, actorIri).GetAwaiter().GetResult();
    }

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
        Id = $"{authorIri}/activities/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(authorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = $"{authorIri}/notes/{Guid.NewGuid():N}",
                Content = ["hello from the followed community"],
            },
        ],
    };

    private static Like BuildLike(Iri actorIri) => new()
    {
        Id = $"{actorIri}/activities/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri($"{actorIri}/notes/{Guid.NewGuid():N}") }],
    };

    private static Announce BuildAnnounce(Iri announcerIri) => new()
    {
        Id = $"{announcerIri}/announces/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(announcerIri.Value) }],
        Object = [new Link { Href = new Uri($"{announcerIri}/notes/{Guid.NewGuid():N}") }],
    };
}
