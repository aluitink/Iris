using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Security;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Unit tests for <see cref="RemoteActorPersister"/>: it persists remote actors to the durable
/// store on first encounter, skips local actors (IRI prefix match), and is idempotent (a
/// re-persist of an already-stored actor is a no-op).
/// </summary>
public class RemoteActorPersisterTests
{
    private static readonly Iri LocalBase = new("https://iris.luit.ink/ap/v1");
    private static readonly Iri RemoteHost = new("https://mastodon.social/ap/users");

    private static Actor MakeActor(string id, string? preferredUsername = null, string? name = null)
    {
        var actor = new Person { Id = id };
        if (preferredUsername is not null)
        {
            actor.PreferredUsername = preferredUsername;
        }
        if (name is not null)
        {
            actor.Name = [name];
        }
        return actor;
    }

    [Fact]
    public async Task PersistIfNew_RemoteActor_StoresInDurableStore()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var remote = MakeActor($"{RemoteHost.Value}/12345", "remoteuser", "Remote User");
        var result = await sut.PersistIfNewAsync(remote);

        Assert.True(result);
        var found = await store.TryGetActorAsync(new Iri($"{RemoteHost.Value}/12345"), out var stored, default);
        Assert.True(found);
        Assert.Equal("remoteuser", stored!.PreferredUsername);
    }

    [Fact]
    public async Task PersistIfNew_LocalActor_Skips()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var local = MakeActor($"{LocalBase.Value}/u/alice", "alice", "Alice");
        var result = await sut.PersistIfNewAsync(local);

        Assert.False(result);
        var found = await store.TryGetActorAsync(new Iri($"{LocalBase.Value}/u/alice"), out _, default);
        Assert.False(found);
    }

    [Fact]
    public async Task PersistIfNew_AlreadyStored_IsIdempotent()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var remote = MakeActor($"{RemoteHost.Value}/12345", "remoteuser", "Remote User");
        await store.PutActorAsync(remote, default);

        var result = await sut.PersistIfNewAsync(remote);

        Assert.False(result);
    }

    [Fact]
    public async Task PersistIfNew_NullActor_ReturnsFalse()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var result = await sut.PersistIfNewAsync(null);

        Assert.False(result);
    }

    [Fact]
    public async Task PersistIfNew_ActorWithoutId_ReturnsFalse()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var noId = new Person { PreferredUsername = "ghost" };
        var result = await sut.PersistIfNewAsync(noId);

        Assert.False(result);
    }

    [Fact]
    public async Task PersistIfNew_NullInstanceBase_PersistsAll()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, instanceBase: null);

        var actor = MakeActor($"{LocalBase.Value}/u/alice", "alice", "Alice");
        var result = await sut.PersistIfNewAsync(actor);

        Assert.True(result);
    }

    [Fact]
    public async Task PersistIfNew_GroupActor_Persists()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var group = new Group { Id = $"{RemoteHost.Value}/groups/tech" };
        group.PreferredUsername = "tech";
        group.Name = ["Tech Community"];

        var result = await sut.PersistIfNewAsync(group);

        Assert.True(result);
        var found = await store.TryGetActorAsync(new Iri($"{RemoteHost.Value}/groups/tech"), out var stored, default);
        Assert.True(found);
        Assert.Equal("tech", stored!.PreferredUsername);
    }

    [Fact]
    public async Task PersistIfNew_MultipleActors_AllPersisted()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var actors = new[]
        {
            MakeActor($"{RemoteHost.Value}/1", "user1"),
            MakeActor($"{RemoteHost.Value}/2", "user2"),
            MakeActor($"{RemoteHost.Value}/3", "user3"),
        };

        foreach (var actor in actors)
        {
            var result = await sut.PersistIfNewAsync(actor);
            Assert.True(result);
        }

        var all = await store.ListActorsAsync(default);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task PersistIfNew_DoesNotOverwriteExisting()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var original = MakeActor($"{RemoteHost.Value}/1", "user1", "Original Name");
        await sut.PersistIfNewAsync(original);

        var updated = MakeActor($"{RemoteHost.Value}/1", "user1", "Updated Name");
        var result = await sut.PersistIfNewAsync(updated);

        Assert.False(result);

        var found = await store.TryGetActorAsync(new Iri($"{RemoteHost.Value}/1"), out var stored, default);
        Assert.True(found);
        Assert.Equal("Original Name", stored!.Name!.First());
    }

    [Fact]
    public async Task PersistIfNew_DifferentInstanceBase_SkipsThatInstance()
    {
        var otherBase = new Iri("https://other.example/ap/v1");
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, otherBase);

        var otherLocal = MakeActor($"{otherBase.Value}/u/bob", "bob", "Bob");
        var result = await sut.PersistIfNewAsync(otherLocal);

        Assert.False(result);
        var found = await store.TryGetActorAsync(new Iri($"{otherBase.Value}/u/bob"), out _, default);
        Assert.False(found);
    }

    [Fact]
    public async Task PersistIfNew_PrefixBoundary_DoesNotFalseMatch()
    {
        // An IRI that contains the local base as a substring but is not actually under it
        // (e.g. a different path segment) should still be persisted.
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        // "https://iris.luit.ink/ap/v1x/user" — note the "1" is followed by "x", not "/u/"
        // This is a contrived case; the real check is StartsWith on the prefix.
        // A more realistic case: a remote host that happens to start with the same string.
        var tricky = MakeActor("https://iris.luit.ink.ap/v1/u/evil", "evil", "Evil");
        var result = await sut.PersistIfNewAsync(tricky);

        // "https://iris.luit.ink/ap/v1" is NOT a prefix of "https://iris.luit.ink.ap/v1/u/evil"
        // (the dot vs slash difference), so it should be persisted.
        Assert.True(result);
    }

    // --- The general IObject overload (the proxy's "archive any remote object we fetch" seam) ----

    [Fact]
    public async Task PersistIfNew_IObject_RemoteActor_Stores()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        // A remote actor passed as a general IObject (the shape the proxy sees after deserializing
        // the relayed body) is archived to the durable actor store.
        IObject obj = MakeActor($"{RemoteHost.Value}/777", "browsed", "Browsed User");
        var result = await sut.PersistIfNewAsync(obj);

        Assert.True(result);
        Assert.True(await store.TryGetActorAsync(new Iri($"{RemoteHost.Value}/777"), out var stored, default));
        Assert.Equal("browsed", stored!.PreferredUsername);
    }

    [Fact]
    public async Task PersistIfNew_IObject_Group_DoesNotStoreInActorStore()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        // A Group (a remote community) is NOT archived here: it belongs to the community store (the
        // proxy routes Group documents to RemoteCommunityPersister). Archiving it into the actor store
        // would duplicate it and could fail (a Group is not an Actor).
        IObject obj = new Group { Id = $"{RemoteHost.Value}/groups/tech", PreferredUsername = "tech" };
        var result = await sut.PersistIfNewAsync(obj);

        Assert.False(result);
        Assert.False(await store.TryGetActorAsync(new Iri($"{RemoteHost.Value}/groups/tech"), out _, default));
    }

    [Fact]
    public async Task PersistIfNew_IObject_Note_DoesNotStore()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        // A content object (a Note) is not an actor; the overload skips it (content is archived to the
        // object store by the proxy, not the actor store).
        IObject obj = new Note { Id = $"{RemoteHost.Value}/notes/1", Content = ["<p>hi</p>"] };
        var result = await sut.PersistIfNewAsync(obj);

        Assert.False(result);
    }

    [Fact]
    public async Task PersistIfNew_IObject_LocalActor_Skips()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        // A local actor (IRI under the instance base) is never archived by this class.
        IObject obj = MakeActor($"{LocalBase.Value}/u/alice", "alice", "Alice");
        var result = await sut.PersistIfNewAsync(obj);

        Assert.False(result);
        Assert.False(await store.TryGetActorAsync(new Iri($"{LocalBase.Value}/u/alice"), out _, default));
    }

    [Fact]
    public async Task PersistIfNew_IObject_Null_ReturnsFalse()
    {
        var store = new InMemoryActorStore();
        var sut = new RemoteActorPersister(store, LocalBase);

        var result = await sut.PersistIfNewAsync(null);

        Assert.False(result);
    }
}
