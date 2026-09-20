using Iris.Core;
using Iris.Server.Data.Stores;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// 139.3-F2 (EF Core / Postgres persistence): every edge read path filters out edges whose source was
/// a *locally-provisioned-then-deleted* actor, while keeping edges whose source is a remote actor or a
/// local actor that was never provisioned. The filter is wired from <see cref="EfActorStore.SourceSurvives"/>
/// into the shared <see cref="EdgeStore"/>, so after <see cref="IActorStore.RemoveActorAsync"/> a read
/// excludes the deleted actor's edges but a remote actor's edges are unaffected.
/// </summary>
public sealed class DeletedActorEdgeFilterTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DeletedActorEdgeFilterTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static Iri Iri(string value) => new(value);

    private static Person Person(string id, string handle) => new() { Id = id, PreferredUsername = handle, Name = [handle] };

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
        return services.BuildServiceProvider().GetRequiredService<IPersistenceProvider>();
    }

    [Fact]
    public async Task DeletedFollower_IsExcluded_FromFollowers_RemoteKept()
    {
        var p = NewProvider();
        var ns = Guid.NewGuid().ToString("N");
        var baseUri = $"https://{ns}.example.org/ap/v1";
        var alice = Iri($"{baseUri}/u/alice-{ns}");
        var deleted = Iri($"{baseUri}/u/deleted-{ns}");
        var remote = Iri($"https://remote-{ns}.example.org/ap/v1/u/remote-{ns}");

        await p.Actors.PutActorAsync(Person(deleted.Value, $"deleted-{ns}"));
        await p.Follows.RecordFollowAsync(deleted, alice);
        await p.Follows.RecordFollowAsync(remote, alice);

        Assert.Contains(deleted, await p.Follows.GetFollowersAsync(alice));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var followers = await p.Follows.GetFollowersAsync(alice);
        Assert.DoesNotContain(deleted, followers);
        Assert.Contains(remote, followers);
    }

    [Fact]
    public async Task DeletedMember_IsExcluded_FromMembers_RemoteKept()
    {
        var p = NewProvider();
        var ns = Guid.NewGuid().ToString("N");
        var baseUri = $"https://{ns}.example.org/ap/v1";
        var community = Iri($"{baseUri}/c/community-{ns}");
        var deleted = Iri($"{baseUri}/u/deleted-{ns}");
        var remote = Iri($"https://remote-{ns}.example.org/ap/v1/u/remote-{ns}");

        await p.Actors.PutActorAsync(Person(deleted.Value, $"deleted-{ns}"));
        await p.Communities.AddFollowerAsync(community, deleted);
        await p.Communities.AddFollowerAsync(community, remote);

        Assert.Contains(deleted, await p.Communities.GetFollowersAsync(community));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var members = await p.Communities.GetFollowersAsync(community);
        Assert.DoesNotContain(deleted, members);
        Assert.Contains(remote, members);
    }

    [Fact]
    public async Task DeletedLiker_IsExcluded_FromLikers_RemoteKept()
    {
        var p = NewProvider();
        var ns = Guid.NewGuid().ToString("N");
        var baseUri = $"https://{ns}.example.org/ap/v1";
        var note = Iri($"{baseUri}/objects/note-{ns}");
        var deleted = Iri($"{baseUri}/u/deleted-{ns}");
        var remote = Iri($"https://remote-{ns}.example.org/ap/v1/u/remote-{ns}");

        await p.Actors.PutActorAsync(Person(deleted.Value, $"deleted-{ns}"));
        await p.Likes.RecordLikeAsync(deleted, note);
        await p.Likes.RecordLikeAsync(remote, note);

        Assert.Contains(deleted, await p.Likes.GetLikersAsync(note));

        Assert.True(await p.Actors.RemoveActorAsync(deleted));

        var likers = await p.Likes.GetLikersAsync(note);
        Assert.DoesNotContain(deleted, likers);
        Assert.Contains(remote, likers);
    }

    [Fact]
    public async Task UnprovisionedLocalActor_IsNotExcluded()
    {
        // An edge whose source is a local IRI that was never provisioned (no actor row was ever created)
        // is NOT a deleted actor, so it must surface. Only a locally-provisioned-then-removed actor is
        // hidden — this guards against the filter over-matching (dropping remote / never-provisioned
        // sources, which the in-memory sibling also guards).
        var p = NewProvider();
        var ns = Guid.NewGuid().ToString("N");
        var baseUri = $"https://{ns}.example.org/ap/v1";
        var alice = Iri($"{baseUri}/u/alice-{ns}");
        var neverProvisioned = Iri($"{baseUri}/u/never-{ns}");

        // No PutActorAsync for neverProvisioned: it has no actor row and was never removed.
        await p.Follows.RecordFollowAsync(neverProvisioned, alice);

        var followers = await p.Follows.GetFollowersAsync(alice);
        Assert.Contains(neverProvisioned, followers);
    }
}
