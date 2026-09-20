using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// 139.3-F2 (in-memory persistence): every edge read path filters out edges whose source was a
/// *locally-stored-then-deleted* actor, while keeping edges whose source is a remote actor or a local
/// actor that was never provisioned. The filter is wired by <see cref="InMemoryPersistenceProvider"/>
/// from the actor store, so a store read after <see cref="Iris.Server.Stores.IActorStore.RemoveActorAsync"/>
/// excludes the deleted actor but a freshly-provisioned actor (or a remote actor) is unaffected.
/// </summary>
public sealed class DeletedActorEdgeFilterIntegrationTests
{
    private const string Local = "local.test";
    private const string Remote = "remote.test";

    private static Iri Actor(string host, string handle) => new($"https://{host}/ap/v1/u/{handle}");

    private static void PutActor(InMemoryPersistenceProvider p, Iri iri, string name)
        => p.Actors.PutActorAsync(new Person { Id = iri.Value, Name = [name] }).GetAwaiter().GetResult();

    [Fact]
    public async Task DeletedFollower_IsExcluded_FromFollowers_RemoteKept()
    {
        var p = new InMemoryPersistenceProvider();
        var alice = Actor(Local, "alice");
        var deleted = Actor(Local, "deleted");
        var remote = Actor(Remote, "remote");

        PutActor(p, deleted, "Deleted");
        await p.Follows.RecordFollowAsync(deleted, alice);
        await p.Follows.RecordFollowAsync(remote, alice);

        Assert.Contains(deleted, await p.Follows.GetFollowersAsync(alice));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var followers = await p.Follows.GetFollowersAsync(alice);
        Assert.DoesNotContain(deleted, followers);
        Assert.Contains(remote, followers);
    }

    [Fact]
    public async Task DeletedLiker_IsExcluded_FromLikers_RemoteKept()
    {
        var p = new InMemoryPersistenceProvider();
        var deleted = Actor(Local, "deleted");
        var remote = Actor(Remote, "remote");
        var note = new Iri($"https://{Local}/objects/note-1");

        PutActor(p, deleted, "Deleted");
        await p.Likes.RecordLikeAsync(deleted, note);
        await p.Likes.RecordLikeAsync(remote, note);

        Assert.Contains(deleted, await p.Likes.GetLikersAsync(note));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var likers = await p.Likes.GetLikersAsync(note);
        Assert.DoesNotContain(deleted, likers);
        Assert.Contains(remote, likers);
    }

    [Fact]
    public async Task DeletedAnnouncer_IsExcluded_FromAnnouncers()
    {
        var p = new InMemoryPersistenceProvider();
        var deleted = Actor(Local, "deleted");
        var note = new Iri($"https://{Local}/objects/note-1");

        PutActor(p, deleted, "Deleted");
        await p.Announces.RecordAnnounceAsync(deleted, note);

        Assert.Contains(deleted, await p.Announces.GetAnnouncersAsync(note));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        Assert.Empty(await p.Announces.GetAnnouncersAsync(note));
    }

    [Fact]
    public async Task DeletedMember_IsExcluded_FromMembers_RemoteKept()
    {
        var p = new InMemoryPersistenceProvider();
        var deleted = Actor(Local, "deleted");
        var remote = Actor(Remote, "remote");
        var community = new Iri($"https://{Local}/c/community-1");

        PutActor(p, deleted, "Deleted");
        await p.Communities.AddFollowerAsync(community, deleted);
        await p.Communities.AddFollowerAsync(community, remote);

        Assert.Contains(deleted, await p.Communities.GetFollowersAsync(community));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var members = await p.Communities.GetFollowersAsync(community);
        Assert.DoesNotContain(deleted, members);
        Assert.Contains(remote, members);
    }

    [Fact]
    public async Task DeletedCommunityFollower_IsExcluded_FromFollowers()
    {
        var p = new InMemoryPersistenceProvider();
        var deleted = Actor(Local, "deleted");
        var community = new Iri($"https://{Local}/c/community-1");

        PutActor(p, deleted, "Deleted");
        await p.Communities.AddFollowerAsync(community, deleted);

        Assert.Contains(deleted, await p.Communities.GetFollowersAsync(community));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        Assert.Empty(await p.Communities.GetFollowersAsync(community));
    }

    [Fact]
    public async Task DeletedBlocker_IsExcluded_FromBlockers()
    {
        var p = new InMemoryPersistenceProvider();
        var deleted = Actor(Local, "deleted");
        var alice = Actor(Local, "alice");

        PutActor(p, deleted, "Deleted");
        await p.Moderation.RecordBlockAsync(deleted, alice);

        Assert.Contains(deleted, await p.Moderation.GetBlockersAsync(alice));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        Assert.Empty(await p.Moderation.GetBlockersAsync(alice));
    }

    [Fact]
    public async Task ReprovisionedActor_SurfacesAgain()
    {
        var p = new InMemoryPersistenceProvider();
        var alice = Actor(Local, "alice");
        var bob = Actor(Local, "bob");

        PutActor(p, bob, "Bob");
        await p.Follows.RecordFollowAsync(bob, alice);

        Assert.True(await p.Actors.RemoveActorAsync(bob));
        Assert.Empty(await p.Follows.GetFollowersAsync(alice));

        // Re-provisioning the same IRI clears the removed marker: the edge surfaces again.
        PutActor(p, bob, "Bob again");
        Assert.Contains(bob, await p.Follows.GetFollowersAsync(alice));
    }
}
