using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the <see cref="LikeActivityHandler"/> (F-04): a <c>Like</c> from a <em>local</em>
/// actor records the directed like edge (so the actor's <c>liked</c> collection can be served), and a
/// <c>Like</c> delivered to a <em>local community</em>'s inbox is recorded in each of the community's
/// local members' outboxes (so the like appears in the community feed, delegating to the same
/// <see cref="CommunityContentRecorder"/> the catch-all handler used). Covers the no-op guards (a like
/// with no resolvable actor/object, a remote liker that is not recorded in any local <c>liked</c>
/// collection) and the null-guard contract.
/// </summary>
public sealed class LikeActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteLiker = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri RemoteObject = new("https://a.domain.local/ap/v1/o/note-1");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/bob");

    // --- Local liker: the like edge is recorded ------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalLiker_RecordsLikeEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        // The like edge is recorded (bob liked the object) — the liked collection lists it.
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, RemoteObject));
        Assert.Contains(RemoteObject, await persistence.Likes.GetLikedAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_LocalLiker_TwoLikes_BothRecorded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like1 = BuildLike(LocalPerson, RemoteObject);
        var like2 = BuildLike(LocalPerson, new Iri("https://a.domain.local/ap/v1/o/note-2"));

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like1), like1);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, like2), like2);

        // Both liked objects are in the liked collection.
        var liked = await persistence.Likes.GetLikedAsync(LocalPerson);
        Assert.Equal(2, liked.Count);
        Assert.Contains(new Iri("https://a.domain.local/ap/v1/o/note-1"), liked);
        Assert.Contains(new Iri("https://a.domain.local/ap/v1/o/note-2"), liked);
    }

    [Fact]
    public async Task HandleAsync_LocalLikerOfLocalObject_RecordsLikeEdge()
    {
        // A local actor liking a local object is recorded the same way (the object IRI is local).
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var localObject = new Iri("https://b.domain.local/ap/v1/o/note-local");
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, localObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, localObject));
    }

    // --- Remote liker: no local liked edge is recorded -----------------------------------

    [Fact]
    public async Task HandleAsync_RemoteLiker_DoesNotRecordLocalLikeEdge()
    {
        // A remote actor's like is not recorded in any local actor's liked collection (the liked
        // collection lists objects THIS actor liked, not objects others liked).
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = BuildLike(RemoteLiker, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        // No local actor has recorded a like edge for the remote liker.
        Assert.Empty(await persistence.Likes.GetLikedAsync(LocalPerson));
        Assert.False(await persistence.Likes.HasLikedAsync(RemoteLiker, RemoteObject));
    }

    // --- Local community recipient: recorded in members' outboxes ------------------------

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_RecordsInMemberOutbox()
    {
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var like = BuildLike(RemoteLiker, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        // The like is recorded in the local member's outbox (appears in the community feed).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(like.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_LocalLiker_RecordsEdgeAndMemberOutbox()
    {
        // A local actor's like delivered to a community: the like edge is recorded (local liker) AND the
        // like is recorded in the community's local members' outboxes (community recipient) — both paths.
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        // The like edge is recorded (local liker) ...
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, RemoteObject));
        // ... AND the like is recorded in the community's local member's outbox (community recipient).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(like.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_NoMembers_NoMemberOutbox()
    {
        // A local community with no members → nothing to record in members' outboxes (but the like edge
        // is still recorded for a local liker).
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        // The like edge is recorded (local liker) ...
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, RemoteObject));
        // ... but there are no members, so no member outbox entry.
        Assert.Empty(await persistence.Activities.GetOutboxAsync(LocalMember));
    }

    // --- Guards ---------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_LikeWithNoActor_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-noactor",
            Object = [new Link { Href = new Uri(RemoteObject.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        Assert.Empty(await persistence.Likes.GetLikedAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_LikeWithNoObject_RecordsNothing()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = new Like
        {
            Id = "https://a.domain.local/activities/like-noobject",
            Actor = [new Link { Href = new Uri(LocalPerson.Value) }],
        };

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        Assert.Empty(await persistence.Likes.GetLikedAsync(LocalPerson));
    }

    // --- Null guards ----------------------------------------------------------------------

    [Fact]
    public void Ctor_NullPersistence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LikeActivityHandler(
            null!, new DefaultLocalActorResolver(new InMemoryPersistenceProvider())));
    }

    [Fact]
    public void Ctor_NullLocalActors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LikeActivityHandler(
            new InMemoryPersistenceProvider(), null!));
    }

    // --- Helpers --------------------------------------------------------------------------

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

    private static LikeActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence, new DefaultLocalActorResolver(persistence));

    private static void SeedLocalActor(IPersistenceProvider persistence, Iri actorIri)
        => SeedPersonAsync(persistence, actorIri).GetAwaiter().GetResult();

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

    private static Like BuildLike(Iri likerIri, Iri objectIri) => new()
    {
        Id = $"{likerIri}/activities/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(likerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };
}
