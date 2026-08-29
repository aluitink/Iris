using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Phase 5 unit tests: the <see cref="InMemoryCommunityStore"/> — the community (the library's
/// <c>Group</c> actor) read/write surface plus its membership set (add/remove/contains/list), the
/// foundation for the community feed and the <c>/c/{name}/members</c> endpoint. Covers the
/// <see cref="ICommunityStore"/> contract: group store/retrieve, membership add (idempotent),
/// remove, contains, list, per-community isolation, and thread-safety of concurrent membership adds.
/// </summary>
public class InMemoryCommunityStoreTests
{
    private static readonly Iri Community = new Iri("https://a.test/ap/v1/c/iris");
    private static readonly Iri Alice = new Iri("https://a.test/ap/v1/u/alice");
    private static readonly Iri Bob = new Iri("https://a.test/ap/v1/u/bob");

    private static Group NewCommunity(string id) =>
        new()
        {
            Id = id,
            Name = ["Iris"],
            PreferredUsername = "iris",
        };

    private static Group NewCommunity(string id, string name) =>
        new()
        {
            Id = id,
            Name = [name],
            PreferredUsername = "iris",
        };

    [Fact]
    public async Task PutCommunity_ThenTryGet_ReturnsTheGroup()
    {
        var sut = new InMemoryCommunityStore();
        var group = NewCommunity("https://a.test/ap/v1/c/iris");

        await sut.PutCommunityAsync(group);

        var found = await sut.TryGetCommunityAsync(Community, out var retrieved);
        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("Iris", retrieved!.Name!.First());
        Assert.Equal("iris", retrieved.PreferredUsername);
    }

    [Fact]
    public async Task TryGet_UnknownCommunity_ReturnsFalseAndNull()
    {
        var sut = new InMemoryCommunityStore();

        var found = await sut.TryGetCommunityAsync(Community, out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void PutCommunity_Null_Throws()
    {
        var sut = new InMemoryCommunityStore();
        Assert.Throws<ArgumentNullException>(() => sut.PutCommunityAsync(null!).GetAwaiter().GetResult());
    }

    [Fact]
    public void PutCommunity_MissingId_Throws()
    {
        var sut = new InMemoryCommunityStore();
        var group = new Group { Name = ["No Id"] };
        Assert.Throws<ArgumentException>(() => sut.PutCommunityAsync(group).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task PutCommunity_ReplacesExistingGroup()
    {
        var sut = new InMemoryCommunityStore();
        await sut.PutCommunityAsync(NewCommunity("https://a.test/ap/v1/c/iris", "Iris"));
        await sut.PutCommunityAsync(NewCommunity("https://a.test/ap/v1/c/iris", "Iris v2"));

        var found = await sut.TryGetCommunityAsync(Community, out var retrieved);
        Assert.True(found);
        Assert.Equal("Iris v2", retrieved!.Name!.First());
    }

    [Fact]
    public async Task AddMember_NewMember_ReturnsTrue()
    {
        var sut = new InMemoryCommunityStore();
        await sut.PutCommunityAsync(NewCommunity("https://a.test/ap/v1/c/iris"));

        var added = await sut.AddMemberAsync(Community, Alice);

        Assert.True(added);
        Assert.True(await sut.IsMemberAsync(Community, Alice));
    }

    [Fact]
    public async Task AddMember_ExistingMember_IsIdempotent()
    {
        var sut = new InMemoryCommunityStore();

        var first = await sut.AddMemberAsync(Community, Alice);
        var second = await sut.AddMemberAsync(Community, Alice);

        Assert.True(first);
        Assert.False(second);
        // A single membership despite two adds.
        Assert.Single(await sut.GetMembersAsync(Community));
    }

    [Fact]
    public async Task RemoveMember_Present_ReturnsTrueAndRemoves()
    {
        var sut = new InMemoryCommunityStore();
        await sut.AddMemberAsync(Community, Alice);

        var removed = await sut.RemoveMemberAsync(Community, Alice);

        Assert.True(removed);
        Assert.False(await sut.IsMemberAsync(Community, Alice));
    }

    [Fact]
    public async Task RemoveMember_Absent_ReturnsFalse()
    {
        var sut = new InMemoryCommunityStore();

        var removed = await sut.RemoveMemberAsync(Community, Alice);

        Assert.False(removed);
    }

    [Fact]
    public async Task IsMember_UnknownCommunity_ReturnsFalse()
    {
        var sut = new InMemoryCommunityStore();

        Assert.False(await sut.IsMemberAsync(Community, Alice));
    }

    [Fact]
    public async Task GetMembers_UnknownCommunity_ReturnsEmpty()
    {
        var sut = new InMemoryCommunityStore();

        Assert.Empty(await sut.GetMembersAsync(Community));
    }

    [Fact]
    public async Task Memberships_AreIsolatedPerCommunity()
    {
        var sut = new InMemoryCommunityStore();
        var other = new Iri("https://a.test/ap/v1/c/other");
        await sut.AddMemberAsync(Community, Alice);

        Assert.True(await sut.IsMemberAsync(Community, Alice));
        Assert.False(await sut.IsMemberAsync(other, Alice));
        Assert.Empty(await sut.GetMembersAsync(other));
    }

    [Fact]
    public async Task ConcurrentAddMember_IsThreadSafe()
    {
        var sut = new InMemoryCommunityStore();
        var actors = Enumerable.Range(0, 50).Select(i => new Iri($"https://a.test/ap/v1/u/u{i}")).ToArray();

        await Task.WhenAll(actors.Select(a => sut.AddMemberAsync(Community, a)));

        Assert.Equal(50, (await sut.GetMembersAsync(Community)).Count);
        Assert.All(actors, a => Assert.True(sut.IsMemberAsync(Community, a).GetAwaiter().GetResult()));
    }

    // --- The follows set (community follows a remote actor) -------------------------

    [Fact]
    public async Task AddFollow_NewFollow_ReturnsTrue()
    {
        var sut = new InMemoryCommunityStore();
        await sut.PutCommunityAsync(NewCommunity("https://a.test/ap/v1/c/iris"));

        var added = await sut.AddFollowAsync(Community, Bob);

        Assert.True(added);
        Assert.Contains(Bob, await sut.GetFollowsAsync(Community));
    }

    [Fact]
    public async Task AddFollow_ExistingFollow_IsIdempotent()
    {
        var sut = new InMemoryCommunityStore();

        var first = await sut.AddFollowAsync(Community, Bob);
        var second = await sut.AddFollowAsync(Community, Bob);

        Assert.True(first);
        Assert.False(second);
        Assert.Single(await sut.GetFollowsAsync(Community));
    }

    [Fact]
    public async Task RemoveFollow_Present_ReturnsTrueAndRemoves()
    {
        var sut = new InMemoryCommunityStore();
        await sut.AddFollowAsync(Community, Bob);

        var removed = await sut.RemoveFollowAsync(Community, Bob);

        Assert.True(removed);
        Assert.Empty(await sut.GetFollowsAsync(Community));
    }

    [Fact]
    public async Task RemoveFollow_Absent_ReturnsFalse()
    {
        var sut = new InMemoryCommunityStore();

        var removed = await sut.RemoveFollowAsync(Community, Bob);

        Assert.False(removed);
    }

    [Fact]
    public async Task GetFollows_UnknownCommunity_ReturnsEmpty()
    {
        var sut = new InMemoryCommunityStore();

        Assert.Empty(await sut.GetFollowsAsync(Community));
    }

    [Fact]
    public async Task Follows_AreIsolatedPerCommunity()
    {
        var sut = new InMemoryCommunityStore();
        var other = new Iri("https://a.test/ap/v1/c/other");
        await sut.AddFollowAsync(Community, Bob);

        Assert.Contains(Bob, await sut.GetFollowsAsync(Community));
        Assert.Empty(await sut.GetFollowsAsync(other));
    }

    [Fact]
    public async Task MembersAndFollows_AreIndependentSets()
    {
        // Membership and following are disjoint sets: a community can follow an actor that is not a
        // member, and have a member that it does not follow.
        var sut = new InMemoryCommunityStore();
        await sut.AddMemberAsync(Community, Alice);
        await sut.AddFollowAsync(Community, Bob);

        Assert.Contains(Alice, await sut.GetMembersAsync(Community));
        Assert.DoesNotContain(Alice, await sut.GetFollowsAsync(Community));
        Assert.Contains(Bob, await sut.GetFollowsAsync(Community));
        Assert.DoesNotContain(Bob, await sut.GetMembersAsync(Community));
    }
}
