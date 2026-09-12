using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Services;
using Iris.Testing;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Services;

/// <summary>
/// Unit tests for the community feed's <strong>peering</strong> behavior (Phase 89): in addition to the
/// members' community-tagged posts, the feed now merges the outbox of every actor the community follows
/// (its "following" set). A followed actor's content is attributed to <em>that</em> actor, so it is
/// admitted without the community-tag filter (unlike a member's posts, which must be tagged to the
/// community). This is what makes the community's unified feed a federated feed: a community that
/// follows another community (or a person) surfaces that actor's content to its members.
/// </summary>
public sealed class CommunityFeedPeeringTests
{
    private const string Host = "a.domain.local";
    private const string Community = "iris";

    private readonly InMemoryPersistenceProvider _persistence = new();
    private readonly Iri _community;
    private readonly Iri _member;
    private readonly Iri _followedCommunity;
    private readonly Iri _followedPerson;
    private readonly ICommunityFeedService _feed;

    public CommunityFeedPeeringTests()
    {
        _community = TestSeeder.SeedCommunity(_persistence, Host, Community);
        _member = TestSeeder.SeedPerson(_persistence, Host, "member");
        _followedCommunity = TestSeeder.SeedCommunity(_persistence, Host, "peer");
        _followedPerson = TestSeeder.SeedPerson(_persistence, Host, "friend");

        _persistence.Communities.AddMemberAsync(_community, _member).GetAwaiter().GetResult();

        // No local-actor resolver: every contributor is read from the local activity store.
        _feed = new CommunityFeedService(_persistence, _persistence.Communities);
    }

    private static List<string> ItemIds(IReadOnlyList<IObjectOrLink> feed)
        => feed
            .Where(i => i is IObject { Id: { Length: > 0 } })
            .Select(i => (i as IObject)!.Id!)
            .ToList();

    // --- A followed actor's (untagged) content appears in the feed ----------------

    [Fact]
    public async Task Feed_IncludesFollowedActorContent_NotCommunityTagged()
    {
        // The community follows "peer". peer's post is NOT tagged to "iris" (it is attributed to peer),
        // so only the peering branch (requireCommunityTagged: false) admits it.
        await _persistence.Communities.AddFollowAsync(_community, _followedCommunity);
        TestSeeder.AddCreateActivity(_persistence, _followedCommunity, $"{_followedCommunity.Value}/activities/peer-1", "peer post", attributedTo: null);

        var feed = await _feed.GetFeedAsync(_community);
        var ids = ItemIds(feed);

        // The followed community's untagged post is in the feed.
        Assert.Contains($"{_followedCommunity.Value}/activities/peer-1", ids);
    }

    // --- A member's untagged post is still EXCLUDED (the member branch is unchanged) --

    [Fact]
    public async Task Feed_StillExcludesMemberContentNotTaggedToCommunity()
    {
        // The member's post is NOT tagged to the community. The member branch (requireCommunityTagged:
        // true) must still exclude it (40.3) — peering must not leak members' personal posts into the
        // community feed.
        TestSeeder.AddCreateActivity(_persistence, _member, $"{_member.Value}/activities/m-untagged", "member personal", attributedTo: null);
        TestSeeder.AddCreateActivity(_persistence, _member, $"{_member.Value}/activities/m-tagged", "member tagged", new[] { _community });

        var feed = await _feed.GetFeedAsync(_community);
        var ids = ItemIds(feed);

        Assert.Contains($"{_member.Value}/activities/m-tagged", ids);
        Assert.DoesNotContain($"{_member.Value}/activities/m-untagged", ids);
    }

    // --- Following a person surfaces their content too ---------------------------

    [Fact]
    public async Task Feed_FollowingAPerson_SurfacesTheirUntaggedContent()
    {
        await _persistence.Communities.AddFollowAsync(_community, _followedPerson);
        TestSeeder.AddCreateActivity(_persistence, _followedPerson, $"{_followedPerson.Value}/activities/f-1", "friend post", attributedTo: null);

        var feed = await _feed.GetFeedAsync(_community);
        var ids = ItemIds(feed);

        Assert.Contains($"{_followedPerson.Value}/activities/f-1", ids);
    }

    // --- Unfollowing removes the followed actor's content from the feed -----------

    [Fact]
    public async Task Feed_Unfollow_RemovesFollowedActorContent()
    {
        await _persistence.Communities.AddFollowAsync(_community, _followedCommunity);
        TestSeeder.AddCreateActivity(_persistence, _followedCommunity, $"{_followedCommunity.Value}/activities/peer-1", "peer post", attributedTo: null);

        Assert.Contains(_followedCommunity.Value + "/activities/peer-1", ItemIds(await _feed.GetFeedAsync(_community)));

        // Unfollow: the edge is removed, so the followed actor's content drops out of the feed.
        await _persistence.Communities.RemoveFollowAsync(_community, _followedCommunity);
        var ids = ItemIds(await _feed.GetFeedAsync(_community));
        Assert.DoesNotContain($"{_followedCommunity.Value}/activities/peer-1", ids);
    }

    // --- A member the community also follows contributes only once (dedup) --------

    [Fact]
    public async Task Feed_MemberAlsoFollowed_ContributesOnce()
    {
        // The member is BOTH a member and followed. The member branch admits the tagged post; the
        // follows branch would admit it again — dedup by activity IRI must collapse it to one.
        await _persistence.Communities.AddFollowAsync(_community, _member);
        TestSeeder.AddCreateActivity(_persistence, _member, $"{_member.Value}/activities/m-1", "member tagged", new[] { _community });

        var ids = ItemIds(await _feed.GetFeedAsync(_community));
        Assert.Equal(1, ids.Count(i => i == $"{_member.Value}/activities/m-1"));
    }

    // --- No follows: the feed is unchanged (members only) -------------------------

    [Fact]
    public async Task Feed_NoFollows_IsMembersOnly()
    {
        TestSeeder.AddCreateActivity(_persistence, _member, $"{_member.Value}/activities/m-1", "member tagged", new[] { _community });

        var ids = ItemIds(await _feed.GetFeedAsync(_community));
        Assert.Single(ids);
        Assert.Equal($"{_member.Value}/activities/m-1", ids[0]);
    }
}
