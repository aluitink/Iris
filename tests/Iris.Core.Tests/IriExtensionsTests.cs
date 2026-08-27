using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="IriExtensions"/> inbox/outbox derivation and boundary conversions.
/// </summary>
public class IriExtensionsTests
{
    [Fact]
    public void InboxOf_AppendsInbox()
    {
        var inbox = new Iri("https://a.domain.local/u/alice").InboxOf();

        Assert.Equal("https://a.domain.local/u/alice/inbox", inbox.Value);
    }

    [Fact]
    public void OutboxOf_AppendsOutbox()
    {
        var outbox = new Iri("https://a.domain.local/u/alice").OutboxOf();

        Assert.Equal("https://a.domain.local/u/alice/outbox", outbox.Value);
    }

    [Fact]
    public void FollowersOf_AppendsFollowers()
    {
        var followers = new Iri("https://a.domain.local/u/alice").FollowersOf();

        Assert.Equal("https://a.domain.local/u/alice/followers", followers.Value);
    }

    [Fact]
    public void FollowingOf_AppendsFollowing()
    {
        var following = new Iri("https://a.domain.local/u/alice").FollowingOf();

        Assert.Equal("https://a.domain.local/u/alice/following", following.Value);
    }

    [Fact]
    public void InboxOf_TrailingSlashIsNotDuplicated()
    {
        var inbox = new Iri("https://a.domain.local/u/alice/").InboxOf();

        Assert.Equal("https://a.domain.local/u/alice/inbox", inbox.Value);
    }

    [Fact]
    public void InboxOf_RelativeIri_Throws()
    {
        var relative = new Iri("/u/alice");

        Assert.Throws<ArgumentException>(() => relative.InboxOf());
    }

    [Fact]
    public void ToIri_FromString_Converts()
    {
        Iri? iri = "https://a.domain.local/n/1".ToIri();

        Assert.Equal(new Iri("https://a.domain.local/n/1"), iri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToIri_FromNullOrBlankString_ReturnsNull(string? value)
    {
        Assert.Null(value.ToIri());
    }

    [Fact]
    public void ToIri_FromUri_Converts()
    {
        Iri? iri = new Uri("https://b.domain.local/u/bob").ToIri();

        Assert.Equal(new Iri("https://b.domain.local/u/bob"), iri);
    }

    [Fact]
    public void ToIri_FromNullUri_ReturnsNull()
    {
        Uri? nullUri = null;
        Assert.Null(nullUri.ToIri());
    }

    [Fact]
    public void ToLibraryId_RoundTrips()
    {
        Iri iri = new("https://a.domain.local/n/1");
        Iri? nullableIri = iri;

        Assert.Equal("https://a.domain.local/n/1", nullableIri.ToLibraryId());
        Assert.Null(default(Iri?).ToLibraryId());
    }

    [Fact]
    public void ToLinkHref_RoundTrips()
    {
        Iri iri = new("https://a.domain.local/n/1");
        Iri? nullableIri = iri;

        Assert.Equal(new Uri("https://a.domain.local/n/1"), nullableIri.ToLinkHref());
        Assert.Null(default(Iri?).ToLinkHref());
    }
}
