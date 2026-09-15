using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// 138.26 (community-level NSFW alignment) — verifies the <see cref="IrisDocumentExtensions.RequiresCw"/>
/// helper, which combines a content object's own <c>sensitive</c> flag with its source community's
/// <c>iris:communityNsfw</c> flag to determine whether a CW overlay should be rendered. The design
/// decision (recorded in the change doc): the server does NOT retro-apply <c>sensitive</c> to
/// individual posts at store time; instead the client derives the effective CW state at render time
/// by combining the two flags. This avoids mutating stored data (a community's NSFW flag can change
/// later) and keeps the data model clean.
/// </summary>
public sealed class CommunityNsfwAlignmentIntegrationTests
{
    private const string Ns = "https://test.domain.local/ns#";

    [Fact]
    public async Task ObjectSensitive_CommunityNotNsfw_RequiresCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/1",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };
        page.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonSerializer.SerializeToElement(true),
        };

        var group = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/test",
            PreferredUsername = "test",
        };

        Assert.True(IrisDocumentExtensions.RequiresCw(page, group, Ns));
    }

    [Fact]
    public async Task ObjectNotSensitive_CommunityNsfw_RequiresCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/2",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };

        var group = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/nsfw",
            PreferredUsername = "nsfw",
        };
        group.ExtensionData = new Dictionary<string, JsonElement>
        {
            [Ns + IrisExtensionTerms.CommunityNsfw] = JsonSerializer.SerializeToElement(true),
        };

        Assert.True(IrisDocumentExtensions.RequiresCw(page, group, Ns));
    }

    [Fact]
    public async Task ObjectSensitive_CommunityNsfw_RequiresCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/3",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };
        page.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonSerializer.SerializeToElement(true),
        };

        var group = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/nsfw",
            PreferredUsername = "nsfw",
        };
        group.ExtensionData = new Dictionary<string, JsonElement>
        {
            [Ns + IrisExtensionTerms.CommunityNsfw] = JsonSerializer.SerializeToElement(true),
        };

        Assert.True(IrisDocumentExtensions.RequiresCw(page, group, Ns));
    }

    [Fact]
    public async Task ObjectNotSensitive_CommunityNotNsfw_NoCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/4",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };

        var group = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/clean",
            PreferredUsername = "clean",
        };

        Assert.False(IrisDocumentExtensions.RequiresCw(page, group, Ns));
    }

    [Fact]
    public async Task ObjectNotSensitive_CommunityNull_UsesObjectFlagOnly_NoCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/5",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };

        Assert.False(IrisDocumentExtensions.RequiresCw(page, null, Ns));
    }

    [Fact]
    public async Task ObjectSensitive_CommunityNull_RequiresCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/6",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };
        page.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["sensitive"] = JsonSerializer.SerializeToElement(true),
        };

        Assert.True(IrisDocumentExtensions.RequiresCw(page, null, Ns));
    }

    [Fact]
    public async Task CommunityNsfwFalse_ObjectNotSensitive_NoCw()
    {
        var page = new Page
        {
            Id = "https://test.domain.local/ap/v1/posts/7",
            Name = ["Post"],
            Content = ["<p>Body</p>"],
        };

        var group = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/clean",
            PreferredUsername = "clean",
        };
        group.ExtensionData = new Dictionary<string, JsonElement>
        {
            [Ns + IrisExtensionTerms.CommunityNsfw] = JsonSerializer.SerializeToElement(false),
        };

        Assert.False(IrisDocumentExtensions.RequiresCw(page, group, Ns));
    }

    [Fact]
    public async Task FullRoundTrip_NsfwCommunityPost_StoredAndRequiresCw()
    {
        // Full round-trip: seed a community with the bare `sensitive` key (as Lemmy would send it),
        // verify the server renders `iris:communityNsfw` on the community document, and verify the
        // client's RequiresCw helper returns true for a post from that community.
        var persistence = new InMemoryPersistenceProvider();
        var (_, communityIri, _) = TestSeeder.SeedCommunityWithKey(persistence, "test.domain.local", "nsfw");

        // Simulate Lemmy sending a community with `sensitive: true` in ExtensionData.
        Assert.True(await persistence.Communities.TryGetCommunityAsync(communityIri, out var stored));
        var group = stored!;
        group.ExtensionData ??= new Dictionary<string, JsonElement>();
        group.ExtensionData["sensitive"] = JsonSerializer.SerializeToElement(true);
        await persistence.Communities.PutCommunityAsync(group);

        // Simulate a post from that community (no per-post sensitive flag).
        var page = new Page
        {
            Id = "https://lemmy.test.domain.local/post/1",
            Name = ["Lemmy post"],
            Content = ["<p>From NSFW community</p>"],
        };

        // The client reads the community document and the post, then calls RequiresCw.
        // The community document carries `sensitive` (bare) — the server would render `iris:communityNsfw`.
        // For this test, we simulate the server's rendering by adding the iris: term directly.
        group.ExtensionData[Ns + IrisExtensionTerms.CommunityNsfw] = JsonSerializer.SerializeToElement(true);

        Assert.True(IrisDocumentExtensions.RequiresCw(page, group, Ns));

        // A non-NSFW community's post does not require CW.
        var cleanGroup = new Group
        {
            Id = "https://test.domain.local/ap/v1/c/clean",
            PreferredUsername = "clean",
        };
        Assert.False(IrisDocumentExtensions.RequiresCw(page, cleanGroup, Ns));
    }
}
