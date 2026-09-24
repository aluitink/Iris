using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Unit tests for <see cref="ActorCountRefreshService"/> — the background service that pre-computes
/// the per-actor counters (<c>iris:postsCount</c>, <c>iris:followersCount</c>, <c>iris:followingCount</c>)
/// and persists them onto the stored actor documents.
/// </summary>
public sealed class ActorCountRefreshServiceTests
{
    private const string Ns = "https://a.test/ns#";
    private static readonly Iri Alice = new("https://a.test/ap/v1/u/alice");
    private static readonly Iri Bob = new("https://a.test/ap/v1/u/bob");
    private static readonly Iri Carol = new("https://a.test/ap/v1/u/carol");
    private static readonly Iri Note1 = new("https://a.test/ap/v1/o/note-1");
    private static readonly Iri Note2 = new("https://a.test/ap/v1/o/note-2");
    private static readonly Iri Community = new("https://a.test/ap/v1/c/test-community");

    private static (ActorCountRefreshService SUT, InMemoryPersistenceProvider Persistence) CreateSut()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            NamespaceIri = new Iri(Ns),
            ActorCountRefreshInterval = TimeSpan.Zero,
        });
        var persistence = new InMemoryPersistenceProvider();
        var sut = new ActorCountRefreshService(persistence, options, NullLogger<ActorCountRefreshService>.Instance);
        return (sut, persistence);
    }

    private static Actor MakeActor(Iri iri, string name) => new()
    {
        Id = iri.Value,
        Name = [name],
    };

    private static Group MakeGroup(Iri iri, string name) => new()
    {
        Id = iri.Value,
        Name = [name],
    };

    private static void AddPostToOutbox(IPersistenceProvider p, Iri actorIri, Iri noteIri)
    {
        var note = new Note { Id = noteIri.Value, Content = ["hello"] };
        var create = new Create { Object = [note] };
        _ = p.Activities.AddToOutboxAsync(actorIri, (IObjectOrLink)create);
    }

    private static void AddCreateOfToOutbox(IPersistenceProvider p, Iri actorIri, IObject obj)
    {
        var create = new Create { Object = [obj] };
        _ = p.Activities.AddToOutboxAsync(actorIri, (IObjectOrLink)create);
    }

    private static void AddAnnounceToOutbox(IPersistenceProvider p, Iri actorIri, Iri targetIri)
    {
        var announce = new Announce { Object = [new Link { Href = new Uri(targetIri.Value) }] };
        _ = p.Activities.AddToOutboxAsync(actorIri, (IObjectOrLink)announce);
    }

    private static void AddFollow(IPersistenceProvider p, Iri from, Iri to)
    {
        _ = p.Follows.RecordFollowAsync(from, to);
    }

    [Fact]
    public async Task RefreshOnce_NoActors_IsNoOp()
    {
        var (sut, persistence) = CreateSut();

        await sut.RefreshOnceAsync(CancellationToken.None);

        Assert.Empty(await persistence.Actors.ListActorsAsync());
    }

    [Fact]
    public async Task RefreshOnce_NullPersistence_IsInert()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            NamespaceIri = new Iri(Ns),
            ActorCountRefreshInterval = TimeSpan.Zero,
        });
        var sut = new ActorCountRefreshService(null, options, NullLogger<ActorCountRefreshService>.Instance);

        await sut.RefreshOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefreshOnce_EmptyOutboxAndFollows_WritesZeroCounts()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
        Assert.NotNull(stored);
        Assert.Equal(0, GetExtInt(stored, Ns + IrisExtensionTerms.PostsCount));
        Assert.Equal(0, GetExtInt(stored, Ns + IrisExtensionTerms.FollowersCount));
        Assert.Equal(0, GetExtInt(stored, Ns + IrisExtensionTerms.FollowingCount));
    }

    [Fact]
    public async Task RefreshOnce_WithPosts_WritesPostsCount()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);
        AddPostToOutbox(persistence, Alice, Note1);
        AddPostToOutbox(persistence, Alice, Note2);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
        Assert.Equal(2, GetExtInt(stored, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_WithAnnounce_CountsAsPost()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);
        AddAnnounceToOutbox(persistence, Alice, Note1);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
        Assert.Equal(1, GetExtInt(stored, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_WithPageAndQuestion_CountsAsPosts()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);
        AddCreateOfToOutbox(persistence, Alice, new Page { Id = "https://a.test/ap/v1/o/page-1", Content = ["<p>cross-post</p>"] });
        AddCreateOfToOutbox(persistence, Alice, new Question { Id = "https://a.test/ap/v1/o/poll-1", Content = ["poll?"] });
        AddPostToOutbox(persistence, Alice, Note1);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
        Assert.Equal(3, GetExtInt(stored, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_WithFollows_WritesFollowerAndFollowingCounts()
    {
        var (sut, persistence) = CreateSut();
        var alice = MakeActor(Alice, "Alice");
        var bob = MakeActor(Bob, "Bob");
        var carol = MakeActor(Carol, "Carol");
        await persistence.Actors.PutActorAsync(alice);
        await persistence.Actors.PutActorAsync(bob);
        await persistence.Actors.PutActorAsync(carol);

        AddFollow(persistence, Bob, Alice);
        AddFollow(persistence, Carol, Alice);
        AddFollow(persistence, Alice, Bob);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (foundAlice, storedAlice) = await GetActorAsync(persistence, Alice);
        Assert.True(foundAlice);
        Assert.Equal(2, GetExtInt(storedAlice, Ns + IrisExtensionTerms.FollowersCount));
        Assert.Equal(1, GetExtInt(storedAlice, Ns + IrisExtensionTerms.FollowingCount));

        var (foundBob, storedBob) = await GetActorAsync(persistence, Bob);
        Assert.True(foundBob);
        Assert.Equal(1, GetExtInt(storedBob, Ns + IrisExtensionTerms.FollowersCount));
        Assert.Equal(1, GetExtInt(storedBob, Ns + IrisExtensionTerms.FollowingCount));
    }

    [Fact]
    public async Task RefreshOnce_Group_FollowersCount_UsesCommunityMembership()
    {
        // S71: a community (Group) stores membership under EdgeKind.CommunityFollower (ICommunityStore),
        // not the actor-follow edges (EdgeKind.Follow) that IFollowStore reads. The refresh must count the
        // community's members via the community store, so the document's followersCount matches the
        // /followers collection. Without the fix the Group's followersCount is taken from the Follow
        // store (0 here) and disagrees with the 3 members.
        var (sut, persistence) = CreateSut();
        var community = MakeGroup(Community, "Test Community");
        await persistence.Actors.PutActorAsync(community);
        // Store the members as actors too (so ListActorsAsync / filterDeletedActors can see them).
        await persistence.Actors.PutActorAsync(MakeActor(Alice, "Alice"));
        await persistence.Actors.PutActorAsync(MakeActor(Bob, "Bob"));
        await persistence.Actors.PutActorAsync(MakeActor(Carol, "Carol"));

        // Membership is recorded via the community store (EdgeKind.CommunityFollower), NOT the Follow store.
        _ = await persistence.Communities.AddFollowerAsync(Community, Alice);
        _ = await persistence.Communities.AddFollowerAsync(Community, Bob);
        _ = await persistence.Communities.AddFollowerAsync(Community, Carol);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Community);
        Assert.True(found);
        Assert.Equal(3, GetExtInt(stored, Ns + IrisExtensionTerms.FollowersCount));
    }

    [Fact]
    public async Task RefreshOnce_Idempotent_SecondPassDoesNotRewrite()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);
        AddPostToOutbox(persistence, Alice, Note1);

        await sut.RefreshOnceAsync(CancellationToken.None);
        var (found1, stored1) = await GetActorAsync(persistence, Alice);
        Assert.True(found1);
        Assert.Equal(1, GetExtInt(stored1, Ns + IrisExtensionTerms.PostsCount));

        // Second pass: counts haven't changed, so no rewrite (the stored instance is the same).
        await sut.RefreshOnceAsync(CancellationToken.None);
        var (found2, stored2) = await GetActorAsync(persistence, Alice);
        Assert.True(found2);
        Assert.Same(stored1, stored2);
        Assert.Equal(1, GetExtInt(stored2, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_UpdatesCounts_WhenActivityChanges()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);
        AddPostToOutbox(persistence, Alice, Note1);

        await sut.RefreshOnceAsync(CancellationToken.None);
        var (_, stored1) = await GetActorAsync(persistence, Alice);
        Assert.Equal(1, GetExtInt(stored1, Ns + IrisExtensionTerms.PostsCount));

        // Add another post, refresh again.
        AddPostToOutbox(persistence, Alice, Note2);
        await sut.RefreshOnceAsync(CancellationToken.None);
        var (_, stored2) = await GetActorAsync(persistence, Alice);
        Assert.Equal(2, GetExtInt(stored2, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_MultipleActors_UpdatesEach()
    {
        var (sut, persistence) = CreateSut();
        var alice = MakeActor(Alice, "Alice");
        var bob = MakeActor(Bob, "Bob");
        await persistence.Actors.PutActorAsync(alice);
        await persistence.Actors.PutActorAsync(bob);

        AddPostToOutbox(persistence, Alice, Note1);
        AddPostToOutbox(persistence, Bob, Note2);
        AddPostToOutbox(persistence, Bob, Note1);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (_, storedAlice) = await GetActorAsync(persistence, Alice);
        Assert.Equal(1, GetExtInt(storedAlice, Ns + IrisExtensionTerms.PostsCount));

        var (_, storedBob) = await GetActorAsync(persistence, Bob);
        Assert.Equal(2, GetExtInt(storedBob, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_PreservesExistingExtensions()
    {
        var (sut, persistence) = CreateSut();
        var actor = MakeActor(Alice, "Alice");
        actor.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["customProp"] = JsonSerializer.SerializeToElement("custom-value"),
        };
        await persistence.Actors.PutActorAsync(actor);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
        Assert.NotNull(stored);
        Assert.Equal("custom-value", stored!.ExtensionData?["customProp"].GetString());
        Assert.Equal(0, GetExtInt(stored, Ns + IrisExtensionTerms.PostsCount));
    }

    [Fact]
    public async Task RefreshOnce_NoNamespace_IsNoOp()
    {
        var options = Options.Create(new ActivityPubServerOptions
        {
            NamespaceIri = null,
            BaseUri = null,
            ActorCountRefreshInterval = TimeSpan.Zero,
        });
        // When neither NamespaceIri nor BaseUri is set, ResolveIrisNamespace falls back to
        // DefaultCapabilitiesNamespaceIri (a non-null value), so the service is not a no-op.
        // This test verifies the service doesn't crash when options are minimal.
        var persistence = new InMemoryPersistenceProvider();
        var actor = MakeActor(Alice, "Alice");
        await persistence.Actors.PutActorAsync(actor);

        var sut = new ActorCountRefreshService(persistence, options, NullLogger<ActorCountRefreshService>.Instance);
        await sut.RefreshOnceAsync(CancellationToken.None);

        // Should not throw; the default namespace is used.
        var (found, stored) = await GetActorAsync(persistence, Alice);
        Assert.True(found);
    }

    [Fact]
    public async Task RefreshOnce_FollowActivityInOutbox_IsNotCountedAsPost()
    {
        var (sut, persistence) = CreateSut();
        var alice = MakeActor(Alice, "Alice");
        var bob = MakeActor(Bob, "Bob");
        await persistence.Actors.PutActorAsync(alice);
        await persistence.Actors.PutActorAsync(bob);

        // A Follow activity in the outbox should NOT count as a post.
        var follow = new Follow { Object = [new Link { Href = new Uri(Bob.Value) }] };
        await persistence.Activities.AddToOutboxAsync(Alice, (IObjectOrLink)follow);

        await sut.RefreshOnceAsync(CancellationToken.None);

        var (_, storedAlice) = await GetActorAsync(persistence, Alice);
        Assert.Equal(0, GetExtInt(storedAlice, Ns + IrisExtensionTerms.PostsCount));
    }

    private static async Task<(bool Found, Actor? Actor)> GetActorAsync(IPersistenceProvider p, Iri iri)
    {
        var found = await p.Actors.TryGetActorAsync(iri, out var actor);
        return (found, actor);
    }

    private static int GetExtInt(Actor? actor, string key)
    {
        if (actor is not null &&
            actor.ExtensionData is { } ext &&
            ext.TryGetValue(key, out var el) &&
            el.ValueKind == JsonValueKind.Number)
        {
            return el.GetInt32();
        }

        return -1;
    }
}
