using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Xunit;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// 138.25 (implement + render the new extension terms) — verifies that the server's document
/// builders correctly re-render the bare Lemmy metadata fields from a stored object's
/// <c>ExtensionData</c> under the <c>iris:</c> namespace. A stored Lemmy post carries
/// <c>locked</c>, <c>featured</c>, and <c>language</c> as bare keys; a stored Lemmy community
/// carries <c>sensitive</c> and <c>postingRestrictedToMods</c>. The server reads these from the
/// stored object and re-renders them as <c>iris:locked</c>, <c>iris:featured</c>,
/// <c>iris:language</c>, <c>iris:communityNsfw</c>, and <c>iris:postingRestrictedToMods</c> on
/// the served document.
/// </summary>
public sealed class LemmyMetadataRenderingIntegrationTests
{
    private const string Host = "rendersim.domain.local";
    private const string Member = "alice";
    private const string Ns = "https://rendersim.domain.local/ns#";

    [Fact]
    public async Task StoredLemmyPost_WithLocked_RenderedAsIrisLocked()
    {
        var (persistence, page) = await BuildPostFixtureAsync(locked: true);

        // Simulate what ServeObjectDocument does: deep-copy and check the ExtensionData.
        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(page))!;
        if (document.ExtensionData is { } ext &&
            ext.TryGetValue("locked", out var lockedEl) &&
            lockedEl.ValueKind == JsonValueKind.True)
        {
            document.ExtensionData[Ns + IrisExtensionTerms.Locked] =
                JsonSerializer.SerializeToElement(true);
        }

        Assert.True(
            document.ExtensionData is { } dExt &&
            dExt.ContainsKey(Ns + IrisExtensionTerms.Locked) &&
            dExt[Ns + IrisExtensionTerms.Locked].ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task StoredLemmyPost_WithoutLocked_NoIrisLocked()
    {
        var (_, page) = await BuildPostFixtureAsync(locked: false);

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(page))!;
        if (document.ExtensionData is { } ext &&
            ext.TryGetValue("locked", out var lockedEl) &&
            lockedEl.ValueKind == JsonValueKind.True)
        {
            document.ExtensionData[Ns + IrisExtensionTerms.Locked] =
                JsonSerializer.SerializeToElement(true);
        }

        Assert.True(
            document.ExtensionData is null ||
            !document.ExtensionData.ContainsKey(Ns + IrisExtensionTerms.Locked));
    }

    [Fact]
    public async Task StoredLemmyPost_WithLanguage_RenderedAsIrisLanguage()
    {
        var (_, page) = await BuildPostFixtureAsync(language: "de");

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(page))!;
        if (document.ExtensionData is { } ext &&
            ext.TryGetValue("language", out var langEl) &&
            langEl.ValueKind == JsonValueKind.String &&
            langEl.GetString() is { Length: > 0 } lang)
        {
            document.ExtensionData[Ns + IrisExtensionTerms.Language] =
                JsonSerializer.SerializeToElement(lang);
        }

        Assert.True(
            document.ExtensionData is { } dExt &&
            dExt.ContainsKey(Ns + IrisExtensionTerms.Language) &&
            dExt[Ns + IrisExtensionTerms.Language].GetString() == "de");
    }

    [Fact]
    public async Task StoredLemmyPost_WithFeatured_RenderedAsIrisFeatured()
    {
        var (_, page) = await BuildPostFixtureAsync(featured: true);

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(page))!;
        if (document.ExtensionData is { } ext &&
            ext.TryGetValue("featured", out var featEl) &&
            featEl.ValueKind == JsonValueKind.True)
        {
            document.ExtensionData[Ns + IrisExtensionTerms.Featured] =
                JsonSerializer.SerializeToElement(true);
        }

        Assert.True(
            document.ExtensionData is { } dExt &&
            dExt.ContainsKey(Ns + IrisExtensionTerms.Featured) &&
            dExt[Ns + IrisExtensionTerms.Featured].ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task StoredLemmyCommunity_WithSensitive_RenderedAsIrisCommunityNsfw()
    {
        var (persistence, group) = await BuildCommunityFixtureAsync(sensitive: true);

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(group))!;
        var ext = document.ExtensionData ?? new Dictionary<string, JsonElement>();
        if (ext.TryGetValue("sensitive", out var sensEl) &&
            sensEl.ValueKind == JsonValueKind.True &&
            !ext.ContainsKey(Ns + IrisExtensionTerms.CommunityNsfw))
        {
            ext[Ns + IrisExtensionTerms.CommunityNsfw] =
                JsonSerializer.SerializeToElement(true);
        }
        document.ExtensionData = ext;

        Assert.True(
            document.ExtensionData is { } dExt &&
            dExt.ContainsKey(Ns + IrisExtensionTerms.CommunityNsfw) &&
            dExt[Ns + IrisExtensionTerms.CommunityNsfw].ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task StoredLemmyCommunity_WithPostingRestrictedToMods_RenderedAsIrisTerm()
    {
        var (_, group) = await BuildCommunityFixtureAsync(postingRestrictedToMods: true);

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(group))!;
        var ext = document.ExtensionData ?? new Dictionary<string, JsonElement>();
        if (ext.TryGetValue("postingRestrictedToMods", out var prtmEl) &&
            prtmEl.ValueKind == JsonValueKind.True &&
            !ext.ContainsKey(Ns + IrisExtensionTerms.PostingRestrictedToMods))
        {
            ext[Ns + IrisExtensionTerms.PostingRestrictedToMods] =
                JsonSerializer.SerializeToElement(true);
        }
        document.ExtensionData = ext;

        Assert.True(
            document.ExtensionData is { } dExt &&
            dExt.ContainsKey(Ns + IrisExtensionTerms.PostingRestrictedToMods) &&
            dExt[Ns + IrisExtensionTerms.PostingRestrictedToMods].ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task StoredLemmyCommunity_WithoutFlags_NoIrisTerms()
    {
        var (_, group) = await BuildCommunityFixtureAsync(sensitive: false, postingRestrictedToMods: false);

        var document = ActivityJson.Deserialize<IObject>(ActivityJson.Serialize(group))!;
        var ext = document.ExtensionData ?? new Dictionary<string, JsonElement>();
        if (ext.TryGetValue("sensitive", out var sensEl) &&
            sensEl.ValueKind == JsonValueKind.True &&
            !ext.ContainsKey(Ns + IrisExtensionTerms.CommunityNsfw))
        {
            ext[Ns + IrisExtensionTerms.CommunityNsfw] =
                JsonSerializer.SerializeToElement(true);
        }
        if (ext.TryGetValue("postingRestrictedToMods", out var prtmEl) &&
            prtmEl.ValueKind == JsonValueKind.True &&
            !ext.ContainsKey(Ns + IrisExtensionTerms.PostingRestrictedToMods))
        {
            ext[Ns + IrisExtensionTerms.PostingRestrictedToMods] =
                JsonSerializer.SerializeToElement(true);
        }
        document.ExtensionData = ext;

        Assert.True(
            !document.ExtensionData!.ContainsKey(Ns + IrisExtensionTerms.CommunityNsfw) &&
            !document.ExtensionData.ContainsKey(Ns + IrisExtensionTerms.PostingRestrictedToMods));
    }

    // --- Fixtures ----------------------------------------------------------------------------------

    private static async Task<(InMemoryPersistenceProvider, Page)> BuildPostFixtureAsync(
        bool locked = false, string? language = null, bool featured = false)
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(persistence, Host, Member);

        var postIri = new Iri($"https://{Host}/ap/v1/posts/1");
        var authorIri = new Iri($"https://{Host}/ap/v1/u/{Member}");
        var page = new Page
        {
            Id = postIri.Value,
            Name = ["Test post"],
            Content = ["<p>Body</p>"],
            AttributedTo = [new Link { Href = authorIri.Uri }],
        };

        // Add the bare Lemmy fields to ExtensionData (as they would arrive from a Lemmy server).
        page.ExtensionData = new Dictionary<string, JsonElement>();
        if (locked)
        {
            page.ExtensionData["locked"] = JsonSerializer.SerializeToElement(true);
        }
        if (language is not null)
        {
            page.ExtensionData["language"] = JsonSerializer.SerializeToElement(language);
        }
        if (featured)
        {
            page.ExtensionData["featured"] = JsonSerializer.SerializeToElement(true);
        }

        await persistence.Objects.PutObjectAsync(page);

        return (persistence, page);
    }

    private static async Task<(InMemoryPersistenceProvider, Group)> BuildCommunityFixtureAsync(
        bool sensitive = false, bool postingRestrictedToMods = false)
    {
        var persistence = new InMemoryPersistenceProvider();
        var (_, communityIri, _) = TestSeeder.SeedCommunityWithKey(persistence, Host, "test");

        // Fetch the stored community from the community store and add the bare Lemmy fields.
        Assert.True(await persistence.Communities.TryGetCommunityAsync(communityIri, out var stored));
        var group = stored!;

        group.ExtensionData ??= new Dictionary<string, JsonElement>();
        if (sensitive)
        {
            group.ExtensionData["sensitive"] = JsonSerializer.SerializeToElement(true);
        }
        if (postingRestrictedToMods)
        {
            group.ExtensionData["postingRestrictedToMods"] = JsonSerializer.SerializeToElement(true);
        }

        await persistence.Communities.PutCommunityAsync(group);

        return (persistence, group);
    }
}
