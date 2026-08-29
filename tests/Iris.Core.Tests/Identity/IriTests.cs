using Iris.Core;

namespace Iris.Core.Tests.Identity;

/// <summary>
/// Unit tests for the <see cref="Iri"/> value type.
/// </summary>
public class IriTests
{
    [Fact]
    public void Ctor_FromString_WrapsUri()
    {
        var iri = new Iri("https://a.domain.local/u/alice");

        Assert.Equal("https://a.domain.local/u/alice", iri.Value);
        Assert.True(iri.IsAbsolute);
        Assert.Equal("https://a.domain.local/u/alice", iri.ToString());
    }

    [Fact]
    public void Ctor_FromUri_WrapsUri()
    {
        var uri = new Uri("https://a.domain.local/n/1");
        var iri = new Iri(uri);

        Assert.Same(uri, iri.Uri);
        Assert.Equal("https://a.domain.local/n/1", iri.Value);
    }

    [Fact]
    public void Ctor_FromNullUri_Throws()
    {
        // The null is intentional: this test proves the guard clause rejects null.
#nullable disable
        Assert.Throws<ArgumentNullException>(() => new Iri((Uri)null));
#nullable restore
    }

    [Fact]
    public void Public_IsWellKnownIri()
    {
        Assert.Equal("https://www.w3.org/ns/activitystreams#Public", Iri.Public.Value);
        Assert.True(Iri.Public.IsPublic);
    }

    [Fact]
    public void IsPublic_TrueOnlyForPublic()
    {
        Assert.True(Iri.Public.IsPublic);
        Assert.False(new Iri("https://a.domain.local/u/alice").IsPublic);
    }

    [Fact]
    public void Equality_ByUriValue()
    {
        var a = new Iri("https://a.domain.local/u/alice");
        var b = new Iri("https://a.domain.local/u/alice");
        var c = new Iri("https://a.domain.local/u/bob");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("https://a.domain.local/u/alice", true)]
    [InlineData("/relative/path", true)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_ValidatesInput(string? input, bool expected)
    {
        string value = input ?? string.Empty;
        var ok = Iri.TryParse(value, out var iri);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal(input?.Trim(), iri.ToString().Trim());
        }
        else
        {
            Assert.Equal(default, iri);
        }
    }
}
