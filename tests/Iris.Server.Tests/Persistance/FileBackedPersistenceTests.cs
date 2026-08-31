using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Persistance;

/// <summary>
/// Phase 16.4 tests for the persistent, file-backed <see cref="IPersistenceProvider"/>
/// (<see cref="FileBackedPersistenceProvider"/>): every store (actors, activities, follows, likes,
/// replies, moderation, relays, objects, communities) and the local instance's signing keys survive a
/// process restart — a new provider constructed over the same directory replays the state that was
/// written before the previous process stopped. The default in-memory provider is ephemeral; the
/// file-backed provider is the production-persistence swap for the <see cref="IPersistenceProvider"/>
/// seam, opt-in via <see cref="ActivityPubServerExtensions.UseFileBackedPersistence"/>.
/// </summary>
/// <remarks>
/// A "restart" is simulated by constructing a fresh <see cref="FileBackedPersistenceProvider"/> over the
/// same directory — exactly what happens when a host process stops and starts (the per-store JSON files
/// are replayed on construction). Each test uses a temp directory; cleanup is best-effort.
/// </remarks>
public sealed class FileBackedPersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iris-persist-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string Dir(string name) => Path.Combine(_dir, name);

    private static Iri IriOf(string iri) => new(iri);

    private static Actor BuildActor(string name) => new Person
    {
        Id = $"https://iris.example/ap/u/{name}",
        PreferredUsername = name,
        Name = [name],
    };

    private static Group BuildCommunity(string name) => new Group
    {
        Id = $"https://iris.example/ap/c/{name}",
        Name = [name],
    };

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

    private static IObject BuildNote(string id, string actorIri) => new Note
    {
        Id = id,
        AttributedTo = [Link(actorIri)],
        Content = ["hello"],
    };

    private static Create BuildCreate(string id, string actorIri, string objectId) => new Create
    {
        Id = id,
        Actor = [Link(actorIri)],
        Object = [Link(objectId)],
    };

    // --- Actor store: documents survive a restart ------------------------------------------

    [Fact]
    public async Task ActorStore_Documents_SurviveRestart()
    {
        var dir = Dir("actors");
        Directory.CreateDirectory(dir);
        var actor = BuildActor("alice");
        var actorIri = IriOf(actor.Id!);

        var p1 = new FileBackedPersistenceProvider(dir);
        await p1.Actors.PutActorAsync(actor);
        var found1 = await p1.Actors.TryGetActorAsync(actorIri, out Actor? a1);
        Assert.True(found1);
        Assert.Equal("alice", a1!.PreferredUsername);
    }

    [Fact]
    public async Task ActorStore_Restart_ReplaysDocuments()
    {
        var dir = Dir("actors-restart");
        Directory.CreateDirectory(dir);
        var alice = BuildActor("alice");
        var bob = BuildActor("bob");
        var aliceIri = IriOf(alice.Id!);

        // Process 1: put two actors, stop.
        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Actors.PutActorAsync(alice);
            await p1.Actors.PutActorAsync(bob);
        }

        // Process 2: a fresh provider over the same directory replays both actors.
        var p2 = new FileBackedPersistenceProvider(dir);
        var found = await p2.Actors.TryGetActorAsync(aliceIri, out Actor? actor);
        Assert.True(found);
        Assert.Equal("alice", actor!.PreferredUsername);
        Assert.Equal(2, (await p2.Actors.ListActorsAsync()).Count);
    }

    // --- Follow store: edges survive a restart ---------------------------------------------

    [Fact]
    public async Task FollowStore_Edges_SurviveRestart()
    {
        var dir = Dir("follows-restart");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var bob = IriOf("https://iris.example/ap/u/bob");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Follows.RecordFollowAsync(alice, bob);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.True(await p2.Follows.IsFollowingAsync(alice, bob));
        Assert.Contains(bob, await p2.Follows.GetFollowingAsync(alice));
        Assert.Contains(alice, await p2.Follows.GetFollowersAsync(bob));
    }

    // --- Like store: edges survive a restart -----------------------------------------------

    [Fact]
    public async Task LikeStore_Edges_SurviveRestart()
    {
        var dir = Dir("likes-restart");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var note = IriOf("https://iris.example/ap/n/note-1");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Likes.RecordLikeAsync(alice, note);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.True(await p2.Likes.HasLikedAsync(alice, note));
        Assert.Contains(note, await p2.Likes.GetLikedAsync(alice));
    }

    // --- Reply store: edges survive a restart ----------------------------------------------

    [Fact]
    public async Task ReplyStore_Edges_SurviveRestart()
    {
        var dir = Dir("replies-restart");
        Directory.CreateDirectory(dir);
        var parent = IriOf("https://iris.example/ap/n/parent");
        var child = IriOf("https://iris.example/ap/n/child");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Replies.RecordReplyAsync(parent, child);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.True(await p2.Replies.HasReplyAsync(parent, child));
        Assert.Contains(child, await p2.Replies.GetRepliesAsync(parent));
    }

    // --- Moderation store: block/flag/mute survive a restart --------------------------------

    [Fact]
    public async Task ModerationStore_Edges_SurviveRestart()
    {
        var dir = Dir("moderation-restart");
        Directory.CreateDirectory(dir);
        var a = IriOf("https://iris.example/ap/u/a");
        var b = IriOf("https://iris.example/ap/u/b");
        var c = IriOf("https://iris.example/ap/u/c");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Moderation.RecordBlockAsync(a, b);
            await p1.Moderation.RecordFlagAsync(a, c);
            await p1.Moderation.RecordMuteAsync(a, b);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.True(await p2.Moderation.IsBlockedAsync(a, b));
        Assert.Contains(a, await p2.Moderation.GetBlockersAsync(b)); // inverse
        Assert.True(await p2.Moderation.HasFlaggedAsync(a, c));
        Assert.True(await p2.Moderation.IsMutedAsync(a, b));
    }

    // --- Relay store: subscriptions survive a restart ----------------------------------------

    [Fact]
    public async Task RelayStore_Edges_SurviveRestart()
    {
        var dir = Dir("relays-restart");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var relay = IriOf("https://relay.example/#relay");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Relays.RecordRelayAsync(alice, relay);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.True(await p2.Relays.IsRelayAsync(alice, relay));
        Assert.Contains(relay, await p2.Relays.GetRelaysAsync(alice));
    }

    // --- Object store: documents survive a restart (polymorphic round-trip) -------------------

    [Fact]
    public async Task ObjectStore_Documents_SurviveRestart_PreservingType()
    {
        var dir = Dir("objects-restart");
        Directory.CreateDirectory(dir);
        var note = BuildNote("https://iris.example/ap/n/note-1", "https://iris.example/ap/u/alice");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Objects.PutObjectAsync(note);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        var found = await p2.Objects.TryGetObjectAsync(IriOf(note.Id!), out IObject? obj);
        Assert.True(found);
        // The concrete ActivityStreams type is preserved through the JSON round-trip (a Note stays a Note).
        Assert.IsType<Note>(obj);
        Assert.Equal("hello", ((Note)obj!).Content!.Single());
    }

    // --- Activity store: activities + outbox survive a restart --------------------------------

    [Fact]
    public async Task ActivityStore_Outbox_SurvivesRestart()
    {
        var dir = Dir("activities-restart");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var create = BuildCreate("https://iris.example/ap/act/create-1", alice.Value, "https://iris.example/ap/n/note-1");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Activities.PutActivityAsync(create);
            await p1.Activities.AddToOutboxAsync(alice, (IObjectOrLink)create);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        var found = await p2.Activities.TryGetActivityAsync(IriOf(create.Id!), out IObject? activity);
        Assert.True(found);
        Assert.IsType<Create>(activity);
        var outbox = await p2.Activities.GetOutboxAsync(alice);
        Assert.Single(outbox);
        Assert.IsType<Create>(outbox[0]);
    }

    [Fact]
    public async Task ActivityStore_Outbox_AddToOutbox_IsIdempotentByIri()
    {
        // F-1911-2: a re-recorded activity (at-least-once delivery, restart replay) is not duplicated.
        var dir = Dir("activities-outbox-dedup");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var create = BuildCreate("https://iris.example/ap/act/create-dedup", alice.Value, "https://iris.example/ap/n/note-dedup");

        using var p = new FileBackedPersistenceProvider(dir);
        await p.Activities.AddToOutboxAsync(alice, (IObjectOrLink)create);
        await p.Activities.AddToOutboxAsync(alice, (IObjectOrLink)create);
        await p.Activities.AddToOutboxAsync(alice, (IObjectOrLink)create);

        var outbox = await p.Activities.GetOutboxAsync(alice);
        Assert.Single(outbox);
        Assert.IsType<Create>(outbox[0]);
    }

    // --- Community store: document + members survive a restart --------------------------------

    [Fact]
    public async Task CommunityStore_DocumentsAndMembers_SurviveRestart()
    {
        var dir = Dir("communities-restart");
        Directory.CreateDirectory(dir);
        var community = BuildCommunity("general");
        var communityIri = IriOf(community.Id!);
        var alice = IriOf("https://iris.example/ap/u/alice");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Communities.PutCommunityAsync(community);
            await p1.Communities.AddMemberAsync(communityIri, alice);
            await p1.Communities.AddFollowerAsync(communityIri, alice);
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        var found = await p2.Communities.TryGetCommunityAsync(communityIri, out Group? c);
        Assert.True(found);
        Assert.IsType<Group>(c);
        Assert.True(await p2.Communities.IsMemberAsync(communityIri, alice));
        Assert.Contains(alice, await p2.Communities.GetFollowersAsync(communityIri));
        Assert.Contains(communityIri, await p2.Communities.GetAllCommunityIrisAsync());
    }

    // --- Key store: signing keys survive a restart (all three algorithms) ---------------------

    [Theory]
    [InlineData(KeyAlgorithm.Ed25519)]
    [InlineData(KeyAlgorithm.EcP256)]
    [InlineData(KeyAlgorithm.Rsa)]
    public async Task KeyStore_Keys_SurviveRestart_AllAlgorithms(KeyAlgorithm algorithm)
    {
        var dir = Dir($"keys-{algorithm}");
        Directory.CreateDirectory(dir);
        var keyId = IriOf($"https://iris.example/ap/u/alice#key-{algorithm}");

        ISigningKey key = algorithm switch
        {
            KeyAlgorithm.Ed25519 => Ed25519Key.Generate(keyId),
            _ => KeyPairGenerator.Generate(algorithm, keyId),
        };

        var data = "iris-federation-payload"u8.ToArray();
        var signatureBefore = key.Sign(data);

        // Process 1: store the key, stop.
        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            p1.Keys.PutKey(key);
        }

        // Process 2: a fresh provider over the same directory restores the key; a signature made
        // before the restart still verifies with the restored key.
        var p2 = new FileBackedPersistenceProvider(dir);
        var restored = p2.Keys.TryGetKey(keyId, out var k) ? k! : throw new Xunit.Sdk.XunitException("key not restored");
        Assert.Equal(algorithm, restored.Algorithm);
        Assert.Equal(keyId, restored.KeyId);
        Assert.True(restored.Verify(data, signatureBefore));
    }

    [Fact]
    public async Task KeyStore_RemovedKey_IsGoneAfterRestart()
    {
        var dir = Dir("keys-remove");
        Directory.CreateDirectory(dir);
        var keyId = IriOf("https://iris.example/ap/u/alice#key-x");
        var key = Ed25519Key.Generate(keyId);

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            p1.Keys.PutKey(key);
            Assert.True(p1.Keys.RemoveKey(keyId));
        }

        var p2 = new FileBackedPersistenceProvider(dir);
        Assert.False(p2.Keys.TryGetKey(keyId, out _));
    }

    // --- Aggregate: a single restart replays every store at once ------------------------------

    [Fact]
    public async Task Provider_AllStores_SurviveASingleRestart()
    {
        var dir = Dir("aggregate-restart");
        Directory.CreateDirectory(dir);
        var alice = IriOf("https://iris.example/ap/u/alice");
        var bob = IriOf("https://iris.example/ap/u/bob");
        var note = IriOf("https://iris.example/ap/n/note-1");

        using (var p1 = new FileBackedPersistenceProvider(dir))
        {
            await p1.Actors.PutActorAsync(BuildActor("alice"));
            await p1.Follows.RecordFollowAsync(alice, bob);
            await p1.Likes.RecordLikeAsync(alice, note);
            await p1.Moderation.RecordBlockAsync(bob, alice);
            p1.Keys.PutKey(Ed25519Key.Generate(IriOf("https://iris.example/ap/u/alice#key-1")));
        }

        // One restart replays every store.
        var p2 = new FileBackedPersistenceProvider(dir);
        var actorFound = await p2.Actors.TryGetActorAsync(alice, out Actor? actor);
        Assert.True(actorFound);
        Assert.True(await p2.Follows.IsFollowingAsync(alice, bob));
        Assert.True(await p2.Likes.HasLikedAsync(alice, note));
        Assert.True(await p2.Moderation.IsBlockedAsync(bob, alice));
        Assert.True(p2.Keys.TryGetKey(IriOf("https://iris.example/ap/u/alice#key-1"), out _));
    }

    // --- Corrupt / missing file: the host still starts (empty state) --------------------------

    [Fact]
    public async Task Store_MissingFile_StartsEmpty()
    {
        var dir = Dir("missing-file");
        Directory.CreateDirectory(dir);
        // No files written. A fresh provider over the (empty) directory has empty stores.
        var p = new FileBackedPersistenceProvider(dir);
        Assert.False(await p.Actors.TryGetActorAsync(IriOf("https://iris.example/ap/u/nobody"), out _));
        Assert.Empty(await p.Follows.GetFollowersAsync(IriOf("https://iris.example/ap/u/nobody")));
    }

    [Fact]
    public async Task Store_CorruptFile_TreatedAsEmpty()
    {
        var dir = Dir("corrupt-file");
        Directory.CreateDirectory(dir);
        // Write a malformed actor file by hand (simulates a torn/truncated file).
        File.WriteAllText(Path.Combine(dir, "actors.json"), "{ this is not valid json ");

        var p = new FileBackedPersistenceProvider(dir);
        // The host starts (no throw) with an empty actor store.
        Assert.False(await p.Actors.TryGetActorAsync(IriOf("https://iris.example/ap/u/nobody"), out _));
    }

    // --- DI: UseFileBackedPersistence is opt-in and swaps the seam ----------------------------

    private static IServiceCollection BuildServerServices(string dir)
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddActivityPubServer(opts =>
        {
            opts.BaseUri = new Iri("https://iris.example");
            opts.InstanceName = "test-iris";
        });
        services.AddInMemoryPersistence(); // default (in-memory) provider
        services.UseFileBackedPersistence(dir); // opt-in swap
        return services;
    }

    [Fact]
    public void DI_UseFileBackedPersistence_SwapsTheProvider()
    {
        var dir = Dir("di-swap");
        Directory.CreateDirectory(dir);

        var services = BuildServerServices(dir);
        using var provider = services.BuildServiceProvider();
        var persistence = provider.GetRequiredService<IPersistenceProvider>();
        // The last-registered IPersistenceProvider wins: the file-backed one.
        Assert.IsType<FileBackedPersistenceProvider>(persistence);
        Assert.IsType<FileBackedKeyStore>(provider.GetRequiredService<IKeyStore>());
    }

    [Fact]
    public void DI_WithoutOptIn_DefaultsToInMemory()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddActivityPubServer(opts =>
        {
            opts.BaseUri = new Iri("https://iris.example");
            opts.InstanceName = "test-iris";
        });
        services.AddInMemoryPersistence();

        using var provider = services.BuildServiceProvider();
        var persistence = provider.GetRequiredService<IPersistenceProvider>();
        Assert.IsType<InMemoryPersistenceProvider>(persistence);
    }

    [Fact]
    public void DI_UseFileBackedPersistence_MissingDirectory_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<DirectoryNotFoundException>(() => services.UseFileBackedPersistence(Path.Combine(_dir, "does-not-exist")));
    }
}
