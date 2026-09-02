using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Inbox;

/// <summary>
/// Unit tests for the <see cref="MoveActivityHandler"/> — the handler for an inbound <see cref="Move"/>
/// (an actor migrating to a new IRI). When the moving actor is the activity's <c>actor</c> (the old IRI)
/// and the new IRI is the activity's <c>object</c>, the handler re-points every <em>local</em> follow edge
/// that targets the old IRI at the new IRI (person followers via the <see cref="IFollowStore"/>, community
/// follows via the <see cref="ICommunityStore"/> follows set), and invalidates the moving actor's outbound
/// key/actor-document cache entries (F-25). The handler does not gate on the recipient (a Move is delivered
/// to the moving actor's followers, which may be local or remote) — it re-points local edges by target.
/// Covers: person-edge re-pointing, community-follow re-pointing, a remote (non-local) follower left
/// untouched, a no-op when no local edge targets the moving actor, cache invalidation, the malformed
/// (missing actor/object) no-op, and the null-guard contract.
/// </summary>
public sealed class MoveActivityHandlerTests
{
    private static readonly Iri OldActor = new("https://a.domain.local/ap/v1/u/alice");
    private static readonly Iri NewActor = new("https://b.domain.local/ap/v1/u/alice");
    private static readonly Iri LocalFollower = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri RemoteFollower = new("https://c.domain.local/ap/v1/u/carol");
    private static readonly Iri Community = new("https://b.domain.local/ap/v1/c/iris");

    // --- Person follow edge: re-pointed -----------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalPersonFollows_MovingActor_RepointsFollowEdge()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        // The follow edge is re-pointed: bob no longer follows the old IRI, and now follows the new IRI.
        Assert.False(await persistence.Follows.IsFollowingAsync(LocalFollower, OldActor));
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, NewActor));
    }

    [Fact]
    public async Task HandleAsync_PersonFollowsBoth_MovingActorAndOther_OnlyMovingActorRepointed()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        var other = new Iri("https://a.domain.local/ap/v1/u/dave");
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);
        await persistence.Follows.RecordFollowAsync(LocalFollower, other);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        // Only the edge to the moving actor is re-pointed; the other follow remains.
        Assert.False(await persistence.Follows.IsFollowingAsync(LocalFollower, OldActor));
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, NewActor));
        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, other));
    }

    [Fact]
    public async Task HandleAsync_MultipleLocalFollowers_AllRepointed()
    {
        var persistence = new InMemoryPersistenceProvider();
        var follower2 = new Iri("https://b.domain.local/ap/v1/u/eve");
        await SeedLocalActorAsync(persistence, LocalFollower);
        await SeedLocalActorAsync(persistence, follower2);
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);
        await persistence.Follows.RecordFollowAsync(follower2, OldActor);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, NewActor));
        Assert.True(await persistence.Follows.IsFollowingAsync(follower2, NewActor));
    }

    // --- Community follow set: re-pointed ---------------------------------------------------

    [Fact]
    public async Task HandleAsync_LocalCommunityFollows_MovingActor_RepointsCommunityFollow()
    {
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddFollowAsync(Community, OldActor);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(Community, move), move);

        var follows = await persistence.Communities.GetFollowsAsync(Community);
        Assert.DoesNotContain(OldActor, follows);
        Assert.Contains(NewActor, follows);
    }

    [Fact]
    public async Task HandleAsync_CommunityFollowsOther_NotRepointed()
    {
        var persistence = new InMemoryPersistenceProvider();
        var other = new Iri("https://a.domain.local/ap/v1/u/dave");
        await persistence.Communities.PutCommunityAsync(BuildCommunity());
        await persistence.Communities.AddFollowAsync(Community, other);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(Community, move), move);

        // The community does not follow the moving actor, so its follow set is untouched.
        var follows = await persistence.Communities.GetFollowsAsync(Community);
        Assert.Contains(other, follows);
        Assert.DoesNotContain(OldActor, follows);
        Assert.DoesNotContain(NewActor, follows);
    }

    // --- Remote follower: not this instance's concern --------------------------------------

    [Fact]
    public async Task HandleAsync_RemoteFollowerFollows_MovingActor_LeftUntouched()
    {
        // A remote follower (carol, on c.domain.local) follows the moving actor. The edge is recorded
        // (a remote follower can be in the follow store), but carol is not a LOCAL actor, so the handler
        // must not re-point it (carol's instance owns that edge).
        var persistence = new InMemoryPersistenceProvider();
        await persistence.Follows.RecordFollowAsync(RemoteFollower, OldActor);
        var sut = BuildHandler(persistence);

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(RemoteFollower, move), move);

        // The remote follower's edge is untouched (neither removed nor re-pointed).
        Assert.True(await persistence.Follows.IsFollowingAsync(RemoteFollower, OldActor));
        Assert.False(await persistence.Follows.IsFollowingAsync(RemoteFollower, NewActor));
    }

    // --- No local edge targets the moving actor: no-op -------------------------------------

    [Fact]
    public async Task HandleAsync_NoLocalFollowers_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        var sut = BuildHandler(persistence);
        // No follow edge to OldActor was recorded.

        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        Assert.Empty(await persistence.Follows.GetFollowersAsync(OldActor));
        Assert.Empty(await persistence.Follows.GetFollowersAsync(NewActor));
    }

    // --- Cache invalidation (F-25) ---------------------------------------------------------

    [Fact]
    public async Task HandleAsync_InvalidatesMovingActorsRemoteKeyAndActorDocCaches()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);

        var actorCache = new RemoteActorCache();
        var keyCache = new RemoteKeyCache();
        var keyIri = new Iri($"{OldActor}#key-1");
        // Pre-populate both caches with the moving actor's (stale) entries.
        await actorCache.GetAsync(OldActor, bypassCache: false, async _ => { await Task.Yield(); return BuildActorObject(OldActor); });
        await keyCache.GetAsync(keyIri, bypassCache: false, async _ => { await Task.Yield(); return new JwkKey("{}", "ecdsa-p256"); });
        Assert.Equal(1, actorCache.Count);
        Assert.Equal(1, keyCache.Count);

        var sut = new MoveActivityHandler(persistence, await persistence.Communities.GetAllCommunityIrisAsync(), keyCache, actorCache);
        var move = BuildMove(OldActor, NewActor);
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        // Both stale entries are invalidated, so the next resolution refetches the new key/doc.
        Assert.Equal(0, actorCache.Count);
        Assert.Equal(0, keyCache.Count);
    }

    // --- Malformed: no resolvable actor or object ------------------------------------------

    [Fact]
    public async Task HandleAsync_NoActor_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);
        var sut = BuildHandler(persistence);

        // A Move with no actor (the old IRI is unknown) is malformed; nothing is re-pointed.
        var move = new Move
        {
            Id = $"{OldActor}/moves/{Guid.NewGuid():N}",
            Object = [new Link { Href = new Uri(NewActor.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, OldActor));
    }

    [Fact]
    public async Task HandleAsync_NoObject_NoOp()
    {
        var persistence = new InMemoryPersistenceProvider();
        await SeedLocalActorAsync(persistence, LocalFollower);
        await persistence.Follows.RecordFollowAsync(LocalFollower, OldActor);
        var sut = BuildHandler(persistence);

        // A Move with no object (the new IRI is unknown) is malformed; nothing is re-pointed.
        var move = new Move
        {
            Id = $"{OldActor}/moves/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(OldActor.Value) }],
        };
        await sut.HandleAsync(new InboxDelivery(LocalFollower, move), move);

        Assert.True(await persistence.Follows.IsFollowingAsync(LocalFollower, OldActor));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static Group BuildCommunity() => new()
    {
        Id = Community.Value,
        Name = ["Iris"],
        PreferredUsername = "iris",
    };

    private static MoveActivityHandler BuildHandler(IPersistenceProvider persistence)
        => new(persistence, new[] { Community });

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

    private static IObject BuildActorObject(Iri actorIri) => new Person
    {
        Id = actorIri.Value,
        PreferredUsername = new Uri(actorIri.Value).AbsolutePath.Trim('/').Split('/').Last(),
    };

    private static Move BuildMove(Iri oldActorIri, Iri newActorIri) => new()
    {
        Id = $"{oldActorIri}/moves/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(oldActorIri.Value) }],
        Object = [new Link { Href = new Uri(newActorIri.Value) }],
    };
}
