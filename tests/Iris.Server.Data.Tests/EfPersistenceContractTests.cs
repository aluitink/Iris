using Iris.Server.Data;
using KristofferStrube.ActivityStreams;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// Integration tests for the EF Core (PostgreSQL) <see cref="EntityFrameworkPersistenceProvider"/>:
/// they boot a real PostgreSQL via Testcontainers, run the EF migrations, and assert the provider
/// implements the <see cref="IPersistenceProvider"/> contract for each store. A shared
/// <see cref="PostgresFixture"/> (one container for the whole class) keeps the tests fast; each test
/// uses a distinct IRI namespace so tests do not interfere.
/// </summary>
public sealed class EfPersistenceContractTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public EfPersistenceContractTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

    private IPersistenceProvider NewProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = _fixture.ConnectionString,
                ["Iris:MediaBlobDir"] = Path.Combine(_fixture.BlobRoot, Guid.NewGuid().ToString("N")),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        var provider = services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>();
        return provider;
    }

    [Fact]
    public async Task ActorStore_PutGetRemove_RoundTrips()
    {
        var p = NewProvider();
        var iri = new Iri($"https://test.local/ap/v1/u/actor-{Guid.NewGuid():N}");
        var actor = new Person { Id = iri.Value, PreferredUsername = "u" + Guid.NewGuid().ToString("N")[..8] };

        await p.Actors.PutActorAsync(actor);
        var found = await p.Actors.TryGetActorAsync(iri, out var got);
        Assert.True(found);
        Assert.Equal(iri.Value, got!.Id);
        Assert.Equal(actor.PreferredUsername, got.PreferredUsername);

        var removed = await p.Actors.RemoveActorAsync(iri);
        Assert.True(removed);
        var missing = await p.Actors.TryGetActorAsync(iri, out _);
        Assert.False(missing);
    }

    [Fact]
    public async Task ActivityStore_PutTryAddOutbox_RoundTrips()
    {
        var p = NewProvider();
        var actorIri = new Iri($"https://test.local/ap/v1/u/act-{Guid.NewGuid():N}");
        await p.Actors.PutActorAsync(new Person { Id = actorIri.Value, PreferredUsername = "a" });

        var note = new Note
        {
            Id = $"https://test.local/ap/v1/objects/{Guid.NewGuid():N}",
            Content = ["hello"],
            AttributedTo = [Link(actorIri.Value)],
        };
        var create = new Create
        {
            Id = $"https://test.local/ap/v1/activities/{Guid.NewGuid():N}",
            Actor = [Link(actorIri.Value)],
            Object = [note],
        };

        Assert.True(await p.Activities.TryAddActivityAsync(create));
        Assert.False(await p.Activities.TryAddActivityAsync(create)); // idempotent

        await p.Activities.AddToOutboxAsync(actorIri, create);
        var outbox = await p.Activities.GetOutboxAsync(actorIri);
        Assert.Single(outbox);

        Assert.True(await p.Activities.TryGetActivityAsync(new Iri(create.Id), out var got));
        Assert.Equal("Create", got!.Type?.FirstOrDefault());
    }

    [Fact]
    public async Task ObjectStore_PutGetDelete_RoundTrips()
    {
        var p = NewProvider();
        var iri = new Iri($"https://test.local/ap/v1/objects/obj-{Guid.NewGuid():N}");
        var note = new Note { Id = iri.Value, Content = ["persisted"] };

        await p.Objects.PutObjectAsync(note);
        Assert.True(await p.Objects.TryGetObjectAsync(iri, out var got));
        Assert.Equal("Note", got!.Type?.FirstOrDefault());
        Assert.Equal("persisted", ((Note)got).Content!.Single());

        Assert.True(await p.Objects.TryDeleteObjectAsync(iri));
        Assert.False(await p.Objects.TryGetObjectAsync(iri, out _));
    }

    [Fact]
    public async Task FollowStore_RecordRemove_Directions()
    {
        var p = NewProvider();
        var follower = new Iri($"https://test.local/ap/v1/u/f-{Guid.NewGuid():N}");
        var target = new Iri($"https://test.local/ap/v1/u/t-{Guid.NewGuid():N}");

        await p.Follows.RecordFollowAsync(follower, target);
        Assert.True(await p.Follows.IsFollowingAsync(follower, target));
        Assert.Contains(target, await p.Follows.GetFollowingAsync(follower));
        Assert.Contains(follower, await p.Follows.GetFollowersAsync(target));

        Assert.True(await p.Follows.RemoveFollowAsync(follower, target));
        Assert.False(await p.Follows.IsFollowingAsync(follower, target));
    }

    [Fact]
    public async Task KeyStore_PutGetRemove_RoundTrips()
    {
        var p = NewProvider();
        var keyIri = new Iri($"https://test.local/ap/v1/u/k-{Guid.NewGuid():N}/#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyIri);
        p.Keys.PutKey(key);

        Assert.True(p.Keys.TryGetKey(keyIri, out var got));
        Assert.NotNull(got);
        Assert.Equal(keyIri, got!.KeyId);

        Assert.True(p.Keys.RemoveKey(keyIri));
        Assert.False(p.Keys.TryGetKey(keyIri, out _));
    }

    [Fact]
    public async Task CommunityStore_MembershipAndFollows_RoundTrips()
    {
        var p = NewProvider();
        var community = new Iri($"https://test.local/ap/v1/c/comm-{Guid.NewGuid():N}");
        var member = new Iri($"https://test.local/ap/v1/u/m-{Guid.NewGuid():N}");
        var follower = new Iri($"https://test.local/ap/v1/u/cf-{Guid.NewGuid():N}");

        await p.Communities.PutCommunityAsync(new Group { Id = community.Value, PreferredUsername = "c" });
        Assert.True(await p.Communities.TryGetCommunityAsync(community, out var got));
        Assert.Equal(community.Value, got!.Id);

        Assert.True(await p.Communities.AddMemberAsync(community, member));
        Assert.False(await p.Communities.AddMemberAsync(community, member)); // idempotent
        Assert.True(await p.Communities.IsMemberAsync(community, member));
        Assert.Contains(member, await p.Communities.GetMembersAsync(community));

        Assert.True(await p.Communities.AddFollowAsync(community, follower));
        Assert.Contains(follower, await p.Communities.GetFollowsAsync(community));
    }

    [Fact]
    public async Task MediaStore_PutGet_RoundTrips()
    {
        var p = NewProvider();
        var baseUrl = new Iri("https://test.local");
        var bytes = System.Text.Encoding.UTF8.GetBytes("media-payload-" + Guid.NewGuid());

        var iri = await p.Media.PutAsync(bytes, "text/plain", "file.txt", baseUrl);
        Assert.True(await p.Media.TryGetAsync(iri, out var content, out var contentType, out var fileName));
        Assert.Equal(bytes, content);
        Assert.Equal("text/plain", contentType);
        Assert.Equal("file.txt", fileName);
    }
}
