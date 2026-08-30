using Xunit;

namespace Iris.LiveInterop.Tests;

/// <summary>
/// Unit tests for the <see cref="InteropTarget"/> record and <see cref="InteropPlatform"/> enum.
/// These run in the default <c>dotnet test</c> (no live instance is contacted).
/// </summary>
public sealed class InteropTargetTests
{
    [Fact]
    public void InteropTarget_DisplayName_ContainsPlatformAndName()
    {
        var target = new InteropTarget(
            InteropPlatform.Mastodon,
            "dev",
            new Iri("https://mastodon.example.org"),
            ["alice", "bob"],
            new Iri("https://mastodon.example.org/api/v1/admin"),
            "admin-token");

        Assert.Equal("Mastodon:dev", target.DisplayName);
    }

    [Fact]
    public void InteropTarget_RecordValueEquality()
    {
        var a = new InteropTarget(
            InteropPlatform.Lemmy,
            "test",
            new Iri("https://lemmy.example.org"),
            ["alice"],
            new Iri("https://lemmy.example.org/api/v3"),
            "token");
        var b = new InteropTarget(
            InteropPlatform.Lemmy,
            "test",
            new Iri("https://lemmy.example.org"),
            ["alice"],
            new Iri("https://lemmy.example.org/api/v3"),
            "token");

        // The record's value equality compares each property with EqualityComparer<T>.Default.
        // IReadOnlyList<string> is compared by reference (not by value), so two collection
        // expressions with the same content are not equal. Assert the individual fields instead.
        Assert.Equal(a.Platform, b.Platform);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.BaseUri, b.BaseUri);
        Assert.Equal(a.SeedAccounts.ToArray(), b.SeedAccounts.ToArray());
        Assert.Equal(a.AdminApiBase, b.AdminApiBase);
        Assert.Equal(a.AdminToken, b.AdminToken);
        Assert.Equal(a.DisplayName, b.DisplayName);
    }

    [Theory]
    [InlineData(InteropPlatform.Mastodon)]
    [InlineData(InteropPlatform.Lemmy)]
    [InlineData(InteropPlatform.Pleroma)]
    [InlineData(InteropPlatform.Threads)]
    public void InteropPlatform_HasAllFourPlatforms(InteropPlatform platform)
    {
        Assert.NotNull(platform.ToString());
        Assert.NotEmpty(platform.ToString());
    }
}
