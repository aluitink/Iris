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

    [Fact]
    public void Equality_IsFragmentAware_DistinctFragmentsAreDistinct()
    {
        // ActivityPub IRIs use semantically significant fragments: a key IRI (#key-1 / #main-key) and
        // the public-audience IRI (#Public) are distinct resources from the bare actor IRI and from
        // each other. Equality must therefore keep the fragment (unlike System.Uri, which is
        // fragment-insensitive by design).
        var actor = new Iri("https://a.domain.local/u/alice");
        var key1 = new Iri("https://a.domain.local/u/alice#key-1");
        var key2 = new Iri("https://a.domain.local/u/alice#key-2");
        var mainKey = new Iri("https://a.domain.local/u/alice#main-key");

        Assert.NotEqual(actor, key1);
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, mainKey);
        Assert.NotEqual(actor, mainKey);

        // The == / != operators agree with Equals.
        Assert.True(key1 != key2);
        Assert.True(actor != key1);
        Assert.True(key1 == new Iri("https://a.domain.local/u/alice#key-1"));
    }

    [Fact]
    public void Equality_FragmentAware_SameFragmentIsEqual()
    {
        // Two IRIs with the same fragment (and same scheme/host/path) are the same resource.
        var a = new Iri("https://a.domain.local/u/alice#key-1");
        var b = new Iri("https://a.domain.local/u/alice#key-1");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_FragmentAware_DiffersFromBareActorOnlyByFragment()
    {
        // The single most important case for key management: a key IRI is the bare actor IRI plus a
        // fragment, and the two must NOT compare equal (the old fragment-blind System.Uri equality did
        // conflate them, which corrupted the in-memory + file-backed key stores).
        var actor = new Iri("https://a.domain.local/u/alice");
        var key = new Iri("https://a.domain.local/u/alice#key-1");

        Assert.NotEqual(actor, key);
        Assert.NotEqual(key, actor);
        Assert.NotEqual(actor.GetHashCode(), key.GetHashCode());
    }

    [Fact]
    public void Equality_PublicAudienceIsDistinctFromBareIri()
    {
        // The #Public audience IRI shares no host/path with an actor IRI, but the point here is that a
        // fragment-bearing IRI is never equal to its fragment-stripped form.
        var withFragment = new Iri("https://a.domain.local/n/1#anchor");
        var bare = new Iri("https://a.domain.local/n/1");

        Assert.NotEqual(withFragment, bare);
    }

    [Fact]
    public void Equality_DefaultIri_IsNullSafeAndDistinctFromRealIris()
    {
        // default(Iri) has no underlying Uri; equality/hash must not throw and a default must be
        // distinct from any real IRI (two defaults are equal to each other).
        var d1 = default(Iri);
        var d2 = default(Iri);
        var real = new Iri("https://a.domain.local/u/alice");

        Assert.Equal(d1, d2);
        Assert.Equal(d1.GetHashCode(), d2.GetHashCode());
        Assert.NotEqual(d1, real);
        Assert.NotEqual(real, d1);
        Assert.True(d1 != real);
    }

    [Fact]
    public void Equals_Object_FragmentAware()
    {
        // object? equality routes through the fragment-aware Equals (not System.Uri's).
        var key1 = new Iri("https://a.domain.local/u/alice#key-1");
        var key2 = new Iri("https://a.domain.local/u/alice#key-2");

        Assert.False(key1.Equals((object?)key2));
        Assert.True(key1.Equals((object?)new Iri("https://a.domain.local/u/alice#key-1")));
    }

    [Fact]
    public void GetHashCode_IsStableAndFragmentSensitive()
    {
        // IRIs that differ only by fragment must (with overwhelming probability) hash differently, and
        // identical IRIs must hash the same — the precondition for correct Dictionary<Iri, T> behavior.
        var actor = new Iri("https://a.domain.local/u/alice");
        var key1 = new Iri("https://a.domain.local/u/alice#key-1");
        var key1Copy = new Iri("https://a.domain.local/u/alice#key-1");

        Assert.Equal(key1.GetHashCode(), key1Copy.GetHashCode());
        Assert.NotEqual(actor.GetHashCode(), key1.GetHashCode());
    }
}
