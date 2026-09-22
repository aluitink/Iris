using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// S40 regression: deleting a community must remove the inbound <c>Follow</c> (kind 0)
/// edge the community-creation path (S21, <c>RecordCreateLocalAsync</c>) records from the creator to the
/// new community (via the <c>Follows</c> store). <c>DeleteCommunityAsync</c> removes the community's
/// <c>ActorEntity</c> row and the community-scoped edges (incl. the <c>CommunityFollower</c> kind-10 edge),
/// but — before this fix — left the inbound <c>Follow</c> (kind 0) edge behind. The orphaned edge points
/// at a <c>Group</c> whose actor row no longer exists, so the deleted community lingers in the creator's
/// <c>/following</c> collection and Communities "Following" tab (with a 404 re-fetch). This test proves
/// the deletion now cleans up that edge.
/// </summary>
public sealed class CommunityDeletionFollowEdgeTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture _fixture;
    // Held for the life of the test: the EF EdgeStore singleton captures this provider to resolve the
    // deleted-actor (SourceSurvives) filter, so read paths like GetFollowersAsync throw if it is disposed
    // before they run (mirrors AccountDeletionRetentionTests).
    private readonly IPersistenceProvider _p;
    private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider? _provider;

    public CommunityDeletionFollowEdgeTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = _fixture.ConnectionString,
                ["Iris:MediaBlobDir"] = Path.Combine(_fixture.BlobRoot, Guid.NewGuid().ToString("N")),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        var sp = services.BuildServiceProvider();
        _provider = sp;
        _p = sp.GetRequiredService<IPersistenceProvider>();
    }

    public void Dispose()
    {
        _provider?.Dispose();
    }

    private static Iri Iri(string value) => new(value);

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

    [Fact]
    public async Task DeleteCommunity_RemovesInboundFollowEdge_CreatorNoLongerFollows()
    {
        var p = _p;
        var ns = $"s40-{Guid.NewGuid():N}";
        var baseUri = $"https://{ns}.example.org/ap/v1";

        var alice = Iri($"{baseUri}/u/alice");
        var community = Iri($"{baseUri}/c/{ns}");

        // Seed: the creator (alice) + the community (a local Group, as the creation path stores it).
        await p.Actors.PutActorAsync(new Person
        {
            Id = alice.Value,
            PreferredUsername = $"alice-{ns}",
            Name = [$"Alice {ns}"],
            Summary = ["creator of the s40 community"],
        });
        await p.Communities.PutCommunityAsync(new Group
        {
            Id = community.Value,
            PreferredUsername = $"c-{ns}",
            Name = [$"S40 Community {ns}"],
            AttributedTo = [Link(alice.Value)],
        });

        // The S21 auto-follow edge: creator -> community (EdgeKind.Follow, kind 0), recorded via the
        // Follows store exactly as RecordCreateLocalAsync does on community creation.
        await p.Follows.RecordFollowAsync(alice, community);
        Assert.True(await p.Follows.IsFollowingAsync(alice, community),
            "precondition: the creator auto-follows the newly created community (S21)");

        // Delete the community.
        var deleted = await p.Communities.DeleteCommunityAsync(community);
        Assert.True(deleted, "the existing community must be deleted");

        // The community is gone.
        Assert.False(await p.Communities.TryGetCommunityAsync(community, out _),
            "the community must be removed from the community store");

        // S40: the inbound Follow (kind 0) edge (creator -> community) is removed — the creator no
        // longer "follows" the (now-deleted) community, and the community has no followers via the
        // actor-follow store. Before the fix this edge survived, leaving the deleted community in the
        // creator's /following + Following tab.
        Assert.False(await p.Follows.IsFollowingAsync(alice, community),
            "S40: deleting the community must remove the inbound Follow (kind 0) edge creator -> community");
        Assert.Empty(await p.Follows.GetFollowersAsync(community)); // S40: no inbound Follow (kind 0) edges remain
    }

    [Fact]
    public async Task DeleteCommunity_RemovesInboundFollowEdge_LeavesOtherActorsAndEdgesUntouched()
    {
        var p = _p;
        var ns = $"s40b-{Guid.NewGuid():N}";
        var baseUri = $"https://{ns}.example.org/ap/v1";

        var alice = Iri($"{baseUri}/u/alice");
        var bob = Iri($"{baseUri}/u/bob");
        var community = Iri($"{baseUri}/c/{ns}");

        await p.Actors.PutActorAsync(new Person
        {
            Id = alice.Value,
            PreferredUsername = $"alice-{ns}",
            Name = [$"Alice {ns}"],
            Summary = ["creator"],
        });
        await p.Actors.PutActorAsync(new Person
        {
            Id = bob.Value,
            PreferredUsername = $"bob-{ns}",
            Name = [$"Bob {ns}"],
            Summary = ["a follower of both the community and alice"],
        });
        await p.Communities.PutCommunityAsync(new Group
        {
            Id = community.Value,
            PreferredUsername = $"c-{ns}",
            Name = [$"S40b Community {ns}"],
            AttributedTo = [Link(alice.Value)],
        });

        // Two inbound Follow (kind 0) edges to the community (alice + bob) + one Follow edge from bob to
        // alice (unrelated to the community) + a CommunityFollower (kind 10) edge for alice.
        await p.Follows.RecordFollowAsync(alice, community);
        await p.Follows.RecordFollowAsync(bob, community);
        await p.Follows.RecordFollowAsync(bob, alice);
        await p.Communities.AddFollowerAsync(community, alice);

        Assert.True(await p.Communities.DeleteCommunityAsync(community));

        // Both inbound Follow (kind 0) edges to the community are gone...
        Assert.False(await p.Follows.IsFollowingAsync(alice, community), "alice's Follow edge to the community must be removed");
        Assert.False(await p.Follows.IsFollowingAsync(bob, community), "bob's Follow edge to the community must be removed");
        // ...the CommunityFollower (kind 10) edge is gone too...
        Assert.Empty(await p.Communities.GetFollowersAsync(community)); // the CommunityFollower (kind 10) edge is removed
        // ...but bob's unrelated Follow edge to alice (a person, not the community) is untouched.
        Assert.True(await p.Follows.IsFollowingAsync(bob, alice), "an unrelated Follow edge (bob -> alice) must survive");
        Assert.Contains(bob, await p.Follows.GetFollowersAsync(alice)); // alice still lists bob as a follower
    }
}
