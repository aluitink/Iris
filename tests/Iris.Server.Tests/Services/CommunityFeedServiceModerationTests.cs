using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Services;
using Iris.Testing;

namespace Iris.Server.Tests.Services;

/// <summary>
/// Unit test for the <see cref="CommunityFeedService"/> community-moderation filtering (19.5.4): when
/// the service is constructed with a community store, a member the community has <em>blocked</em> or
/// <em>muted</em> is excluded from the unified feed, while a member it has only <em>flagged</em> is
/// <em>not</em> (a flag is a moderation report, not a content exclusion). When the service is constructed
/// without a community store (the default), no moderation filtering is applied (every member is merged).
/// The moderation is community-scoped: only the community being read's own moderation edges are applied.
/// </summary>
public sealed class CommunityFeedServiceModerationTests
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _communityIri;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;

    public CommunityFeedServiceModerationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _aliceIri = TestSeeder.SeedPerson(_persistence, AHost, Alice);
        _bobIri = TestSeeder.SeedPerson(_persistence, AHost, Bob);
        _communityIri = TestSeeder.SeedCommunity(_persistence, AHost, Community);
        TestSeeder.AddMember(_persistence, _communityIri, _aliceIri);
        TestSeeder.AddMember(_persistence, _communityIri, _bobIri);
        TestSeeder.AddCreateActivity(_persistence, _aliceIri, $"{_aliceIri.Value}/activities/create-1", "a GARDEN post");
        TestSeeder.AddCreateActivity(_persistence, _bobIri, $"{_bobIri.Value}/activities/create-1", "about weather");
    }

    private CommunityFeedService ServiceWithModeration()
        => new(_persistence, _persistence.Communities);

    private CommunityFeedService ServiceWithoutModeration()
        => new(_persistence);

    // --- A blocked member's content is excluded from the feed -------------------------

    [Fact]
    public async Task BlockedMember_IsExcludedFromFeed()
    {
        await _persistence.Communities.AddBlockAsync(_communityIri, _bobIri);

        var feed = await ServiceWithModeration().GetFeedAsync(_communityIri);

        // bob's post is excluded; alice's is present.
        Assert.DoesNotContain(feed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
        Assert.Contains(feed, HasActivityId($"{_aliceIri.Value}/activities/create-1"));
    }

    // --- A muted member's content is excluded (soft) ----------------------------------

    [Fact]
    public async Task MutedMember_IsExcludedFromFeed()
    {
        await _persistence.Communities.AddMuteAsync(_communityIri, _bobIri);

        var feed = await ServiceWithModeration().GetFeedAsync(_communityIri);

        Assert.DoesNotContain(feed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
        Assert.Contains(feed, HasActivityId($"{_aliceIri.Value}/activities/create-1"));
    }

    // --- A flagged member's content is NOT excluded (a report, not a filter) ----------

    [Fact]
    public async Task FlaggedMember_IsNotExcludedFromFeed()
    {
        await _persistence.Communities.AddFlagAsync(_communityIri, _bobIri);

        var feed = await ServiceWithModeration().GetFeedAsync(_communityIri);

        // The flag does not exclude bob's content (only blocks and mutes filter).
        Assert.Contains(feed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
    }

    // --- Removing a block/mute restores the member's content --------------------------

    [Fact]
    public async Task Unblocking_RestoresMemberContent()
    {
        await _persistence.Communities.AddBlockAsync(_communityIri, _bobIri);
        await _persistence.Communities.RemoveBlockAsync(_communityIri, _bobIri);

        var feed = await ServiceWithModeration().GetFeedAsync(_communityIri);

        Assert.Contains(feed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
    }

    // --- Without a community store, no moderation filtering is applied ----------------

    [Fact]
    public async Task WithoutCommunityStore_NoModerationFiltering()
    {
        // Record a block on the community, but the service (constructed without a community store) does
        // not apply it: both members' content is merged.
        await _persistence.Communities.AddBlockAsync(_communityIri, _bobIri);

        var feed = await ServiceWithoutModeration().GetFeedAsync(_communityIri);

        Assert.Contains(feed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
        Assert.Contains(feed, HasActivityId($"{_aliceIri.Value}/activities/create-1"));
    }

    // --- The moderation is scoped to the community being read -------------------------

    [Fact]
    public async Task Moderation_IsScopedToTheCommunityBeingRead()
    {
        // A second community (delta) that also has bob as a member, but has NOT blocked bob. Reading
        // delta's feed must include bob's content (iris's block is scoped to iris, not delta).
        var deltaIri = TestSeeder.SeedCommunity(_persistence, AHost, "delta");
        TestSeeder.AddMember(_persistence, deltaIri, _bobIri);
        await _persistence.Communities.AddBlockAsync(_communityIri, _bobIri);

        var deltaFeed = await ServiceWithModeration().GetFeedAsync(deltaIri);
        var irisFeed = await ServiceWithModeration().GetFeedAsync(_communityIri);

        Assert.Contains(deltaFeed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
        Assert.DoesNotContain(irisFeed, HasActivityId($"{_bobIri.Value}/activities/create-1"));
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// A feed-item predicate that matches an activity whose IRI equals
    /// <paramref name="activityIri"/> (the feed's items are the members' outbox <c>Create</c>s; each
    /// item's IRI is the activity IRI).
    /// </summary>
    private static Predicate<KristofferStrube.ActivityStreams.IObjectOrLink> HasActivityId(string activityIri)
        => item => item is KristofferStrube.ActivityStreams.IObject { Id: { Length: > 0 } id } && id == activityIri;
}
