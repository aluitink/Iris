using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for the canonical local terms of the <c>iris:</c>-namespaced extension properties
/// (<see cref="IrisExtensionTerms"/>), the Iris-invented collection-endpoint terms
/// (<see cref="CollectionExtensionNames"/>), and the bare ecosystem-convention key terms
/// (<see cref="ActivityPubExtensionNames"/>). These constants are the single source of truth shared by
/// the <c>Iris.Server</c> render path and the <c>Iris.Client</c> reader path; the tests pin the exact
/// wire strings so a typo — which would silently break extension discovery on the wire — is caught.
/// </summary>
public sealed class IrisExtensionTermsTests
{
    [Fact]
    public void Capabilities_MatchesTheWireTerm()
    {
        Assert.Equal("capabilities", IrisExtensionTerms.Capabilities);
    }

    [Fact]
    public void Settings_MatchesTheWireTerm()
    {
        Assert.Equal("settings", IrisExtensionTerms.Settings);
    }

    [Fact]
    public void SearchQuery_MatchesTheWireTerm()
    {
        Assert.Equal("searchQuery", IrisExtensionTerms.SearchQuery);
    }

    [Fact]
    public void CollectionExtensionNames_MatchTheWireTerms()
    {
        // The Iris-invented collection-endpoint terms (namespaced on the wire) + the core-AS terms that
        // are emitted bare (members) / library-managed (liked) — all pinned to their wire strings.
        Assert.Equal("feed", CollectionExtensionNames.Feed);
        Assert.Equal("blocks", CollectionExtensionNames.Blocks);
        Assert.Equal("flags", CollectionExtensionNames.Flags);
        Assert.Equal("mutes", CollectionExtensionNames.Mutes);
        Assert.Equal("search", CollectionExtensionNames.Search);
        Assert.Equal("star", CollectionExtensionNames.Star);
        Assert.Equal("members", CollectionExtensionNames.Members);
        Assert.Equal("liked", CollectionExtensionNames.Liked);
    }

    [Fact]
    public void EcosystemConventionTerms_MatchTheWireTerms()
    {
        // The bare (ecosystem-convention) terms: the key object + the owner-only key fields.
        Assert.Equal("publicKey", ActivityPubExtensionNames.PublicKey);
        Assert.Equal("privateKey", ActivityPubExtensionNames.PrivateKey);
        Assert.Equal("keyAlgorithm", ActivityPubExtensionNames.KeyAlgorithm);
    }
}
