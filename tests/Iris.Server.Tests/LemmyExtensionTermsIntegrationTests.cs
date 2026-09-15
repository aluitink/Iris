using Iris.Client;
using Iris.Core;
using Xunit;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// 138.24 (Lemmy-specific metadata inventory + extension-term design) — verifies that the new
/// <c>iris:</c> extension terms for Lemmy-specific metadata are declared in the namespace document
/// and that the client-side readers correctly extract them from a document's <c>ExtensionData</c>.
/// </summary>
public sealed class LemmyExtensionTermsIntegrationTests
{
    private const string Ns = "https://test.example/ns#";

    [Fact]
    public void ClientReader_CommunityNsfw_ReadsBoolFromExtensionData()
    {
        var group = new Group
        {
            Id = "https://lemmy.example/c/nsfw-test",
        };

        // No extension data — reader returns null.
        Assert.Null(group.GetCommunityNsfw(Ns));

        // Present and true.
        group.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.CommunityNsfw] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };
        Assert.True(group.GetCommunityNsfw(Ns));

        // Present and false.
        group.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.CommunityNsfw] = System.Text.Json.JsonDocument.Parse("false").RootElement.Clone(),
        };
        Assert.False(group.GetCommunityNsfw(Ns));
    }

    [Fact]
    public void ClientReader_Locked_ReadsBoolFromExtensionData()
    {
        var page = new Page
        {
            Id = "https://lemmy.example/post/100",
        };

        Assert.Null(page.GetLocked(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Locked] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };
        Assert.True(page.GetLocked(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Locked] = System.Text.Json.JsonDocument.Parse("false").RootElement.Clone(),
        };
        Assert.False(page.GetLocked(Ns));
    }

    [Fact]
    public void ClientReader_Featured_ReadsBoolFromExtensionData()
    {
        var page = new Page
        {
            Id = "https://lemmy.example/post/200",
        };

        Assert.Null(page.GetFeatured(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Featured] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };
        Assert.True(page.GetFeatured(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Featured] = System.Text.Json.JsonDocument.Parse("false").RootElement.Clone(),
        };
        Assert.False(page.GetFeatured(Ns));
    }

    [Fact]
    public void ClientReader_Language_ReadsStringFromExtensionData()
    {
        var page = new Page
        {
            Id = "https://lemmy.example/post/300",
        };

        Assert.Null(page.GetLanguage(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Language] = System.Text.Json.JsonDocument.Parse("\"de\"").RootElement.Clone(),
        };
        Assert.Equal("de", page.GetLanguage(Ns));

        page.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.Language] = System.Text.Json.JsonDocument.Parse("\"en\"").RootElement.Clone(),
        };
        Assert.Equal("en", page.GetLanguage(Ns));
    }

    [Fact]
    public void ClientReader_PostingRestrictedToMods_ReadsBoolFromExtensionData()
    {
        var group = new Group
        {
            Id = "https://lemmy.example/c/mod-only",
        };

        Assert.Null(group.GetPostingRestrictedToMods(Ns));

        group.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.PostingRestrictedToMods] = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone(),
        };
        Assert.True(group.GetPostingRestrictedToMods(Ns));

        group.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.PostingRestrictedToMods] = System.Text.Json.JsonDocument.Parse("false").RootElement.Clone(),
        };
        Assert.False(group.GetPostingRestrictedToMods(Ns));
    }

    [Fact]
    public void ClientReader_RemovedBy_ReadsIriFromExtensionData()
    {
        var tombstone = new Tombstone
        {
            Id = "https://lemmy.example/post/400",
        };

        Assert.Null(tombstone.GetRemovedBy(Ns));

        tombstone.ExtensionData = new()
        {
            [Ns + IrisExtensionTerms.RemovedBy] = System.Text.Json.JsonDocument.Parse("\"https://lemmy.example/u/moderator\"").RootElement.Clone(),
        };
        var removedBy = tombstone.GetRemovedBy(Ns);
        Assert.NotNull(removedBy);
        Assert.Equal("https://lemmy.example/u/moderator", removedBy!.ToString());
    }

    [Fact]
    public void AllNewTerms_HaveDistinctWireKeys()
    {
        // Guard against accidental term-name collisions (a compile-time check would be ideal,
        // but this catches copy-paste errors at test time).
        var terms = new[]
        {
            IrisExtensionTerms.CommunityNsfw,
            IrisExtensionTerms.Locked,
            IrisExtensionTerms.Featured,
            IrisExtensionTerms.Language,
            IrisExtensionTerms.PostingRestrictedToMods,
            IrisExtensionTerms.RemovedBy,
            // Pre-existing Lemmy-related terms (for collision check):
            IrisExtensionTerms.Score,
            IrisExtensionTerms.IsDisliked,
            IrisExtensionTerms.DislikedCount,
            IrisExtensionTerms.DislikeActivityIri,
        };

        var distinct = terms.Distinct().ToList();
        Assert.Equal(terms.Length, distinct.Count);
    }
}
