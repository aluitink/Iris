using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="LikeActivityHandler"/> (F-04): a <c>Like</c> of a <em>local</em>
/// object (stored in the instance's object store) records the directed like edge (so the object's
/// <c>/likes</c> collection and like count are served), regardless of whether the liker is local or
/// remote; a <c>Like</c> delivered to a <em>local community</em>'s inbox is recorded in each of the
/// community's local members' outboxes (so the like appears in the community feed, delegating to the
/// same <see cref="CommunityContentRecorder"/> the catch-all handler used). Covers the no-op guards (a
/// like with no resolvable actor/object, a like of a remote object that is not recorded locally because
/// the edge is recorded on the object's author's home instance) and the null-guard contract.
/// </summary>
public sealed class LikeActivityHandlerTests
{
    private static readonly Iri LocalPerson = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteLiker = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri RemoteObject = new("https://a.domain.local/ap/v1/o/note-1");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");
    private static readonly Iri LocalMember = new("https://b.domain.local/ap/v1/u/bob");

    // --- Local object: the like edge is recorded ------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalLikerOfLocalObject_RecordsLikeEdge()
    {
        // A local actor liking a local object records the like edge (the object is stored locally, so
        // the object's /likes collection and like count reflect the like).
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var localObject = new Iri("https://b.domain.local/ap/v1/o/note-1");
        await SeedLocalObjectAsync(persistence, localObject);
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, localObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        // The like edge is recorded (bob liked the object) — the liked collection lists it.
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, localObject));
        Assert.Contains(localObject, await persistence.Likes.GetLikedAsync(LocalPerson));
    }

    [Fact]
    public async Task HandleAsync_LocalLiker_TwoLocalLikes_BothRecorded()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var localObject1 = new Iri("https://b.domain.local/ap/v1/o/note-1");
        var localObject2 = new Iri("https://b.domain.local/ap/v1/o/note-2");
        await SeedLocalObjectAsync(persistence, localObject1);
        await SeedLocalObjectAsync(persistence, localObject2);
        var sut = BuildHandler(persistence);
        var like1 = BuildLike(LocalPerson, localObject1);
        var like2 = BuildLike(LocalPerson, localObject2);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like1), like1);
        await sut.HandleAsync(new InboxDelivery(LocalPerson, like2), like2);

        // Both liked objects are in the liked collection.
        var liked = await persistence.Likes.GetLikedAsync(LocalPerson);
        Assert.Equal(2, liked.Count);
        Assert.Contains(localObject1, liked);
        Assert.Contains(localObject2, liked);
    }

    // --- Remote liker: the edge is recorded when the object is local, not when remote ------

    [Fact]
    public async Task HandleAsync_RemoteLikerOfLocalObject_RecordsLikeEdge()
    {
        // A remote actor's like of a LOCAL object is recorded: the remote actor's like is the object's
        // like, surfaced on the object's own /likes collection and like count (31.10). The edge is
        // recorded on this instance because the object is stored here.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var localObject = new Iri("https://b.domain.local/ap/v1/o/note-local");
        await SeedLocalObjectAsync(persistence, localObject);
        var sut = BuildHandler(persistence);
        var like = BuildLike(RemoteLiker, localObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        // The like edge is recorded (the remote liker liked the local object).
        Assert.True(await persistence.Likes.HasLikedAsync(RemoteLiker, localObject));
        // The object's /likes collection (reverse index) lists the remote liker's like.
        Assert.Contains(RemoteLiker, await persistence.Likes.GetLikersAsync(localObject));
    }

    [Fact]
    public async Task HandleAsync_RemoteLikerOfRemoteObject_DoesNotRecordEdge()
    {
        // A remote actor's like of a REMOTE object (not stored locally) is not recorded here: the edge is
        // recorded on the object's author's home instance instead (the object's liked collection is
        // home-instance-local), so recording it here would duplicate the edge.
        var persistence = new InMemoryPersistenceProvider();
        await SeedPersonAsync(persistence, LocalPerson);
        var sut = BuildHandler(persistence);
        var like = BuildLike(RemoteLiker, RemoteObject);

        await sut.HandleAsync(new InboxDelivery(LocalPerson, like), like);

        // No like edge is recorded (the object is not stored locally).
        Assert.False(await persistence.Likes.HasLikedAsync(RemoteLiker, RemoteObject));
        Assert.Empty(await persistence.Likes.GetLikedAsync(LocalPerson));
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
        // A local actor's like of a local object delivered to a community: the like edge is recorded
        // (the object is local) AND the like is recorded in the community's local members' outboxes
        // (community recipient) — both paths.
        var (persistence, _) = BuildWithLocalMember();
        var sut = BuildHandler(persistence);
        var localObject = new Iri("https://b.domain.local/ap/v1/o/note-local");
        await SeedLocalObjectAsync(persistence, localObject);
        var like = BuildLike(LocalPerson, localObject);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        // The like edge is recorded (the object is local) ...
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, localObject));
        // ... AND the like is recorded in the community's local member's outbox (community recipient).
        var outbox = await persistence.Activities.GetOutboxAsync(LocalMember);
        var ids = outbox.Where(o => o is IObject { Id: not null }).Select(o => ((IObject)o!).Id!).ToList();
        Assert.Contains(like.Id, ids);
    }

    [Fact]
    public async Task HandleAsync_LocalCommunityRecipient_NoMembers_NoMemberOutbox()
    {
        // A local community with no members → nothing to record in members' outboxes (but the like edge
        // is still recorded because the object is local).
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await SeedPersonAsync(persistence, LocalPerson);
        var localObject = new Iri("https://b.domain.local/ap/v1/o/note-local");
        await SeedLocalObjectAsync(persistence, localObject);
        var sut = BuildHandler(persistence);
        var like = BuildLike(LocalPerson, localObject);

        await sut.HandleAsync(new InboxDelivery(Community, like), like);

        // The like edge is recorded (the object is local) ...
        Assert.True(await persistence.Likes.HasLikedAsync(LocalPerson, localObject));
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

    private static Task SeedLocalObjectAsync(IPersistenceProvider persistence, Iri objectIri)
        => persistence.Objects.PutObjectAsync(new Note
        {
            Id = objectIri.Value,
            Content = ["a local object"],
        });

    private static Like BuildLike(Iri likerIri, Iri objectIri) => new()
    {
        Id = $"{likerIri}/activities/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(likerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };
}
