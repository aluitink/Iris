using Iris.Core.Identity;

namespace Iris.Core.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="IriMediaProxyExtensions.ToBrowserMediaIri"/>: the client's render boundary
/// for browser-loadable external media (Phase 20.4 (d), Decision 057). A cross-origin media IRI is
/// rewritten to a same-origin media-proxy IRI; a same-origin IRI, a relative IRI, and a non-HTTP(S) IRI
/// pass through unchanged.
/// </summary>
public sealed class IriMediaProxyExtensionsTests
{
    private static readonly Iri InstanceBase = new("https://a.test");

    [Fact]
    public void CrossOrigin_BecomesSameOriginProxyIri()
    {
        var media = new Iri("https://cdn.example.com/images/cat.png");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.Equal("https://a.test/ap/v1/media/proxy?url=https%3A%2F%2Fcdn.example.com%2Fimages%2Fcat.png", result.Value);
    }

    [Fact]
    public void CrossOrigin_Http_BecomesSameOriginProxyIri()
    {
        var media = new Iri("http://cdn.example.com/images/cat.png");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.StartsWith("https://a.test/ap/v1/media/proxy?url=", result.Value);
        Assert.Contains("http%3A%2F%2Fcdn.example.com", result.Value);
    }

    [Fact]
    public void SameOrigin_PassesThroughUnchanged()
    {
        // The instance's own /ap/v1/media/{id} already loads from the same origin.
        var media = new Iri("https://a.test/ap/v1/media/deadbeefdeadbeefdeadbeefdeadbeef");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.Equal(media, result);
    }

    [Fact]
    public void SameHostDifferentPort_IsCrossOrigin()
    {
        // Same host, different port → cross-origin (the origin includes the port).
        var media = new Iri("https://a.test:8080/images/cat.png");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.StartsWith("https://a.test/ap/v1/media/proxy?url=", result.Value);
    }

    [Fact]
    public void DifferentHost_SamePort_IsCrossOrigin()
    {
        var media = new Iri("https://b.test/images/cat.png");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.StartsWith("https://a.test/ap/v1/media/proxy?url=", result.Value);
    }

    [Fact]
    public void Relative_PassesThroughUnchanged()
    {
        // A relative IRI cannot be classified as cross-origin → passed through.
        var media = new Iri("/local/cat.png");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.Equal(media, result);
    }

    [Fact]
    public void NonHttpScheme_PassesThroughUnchanged()
    {
        // A non-HTTP(S) scheme (e.g. data:, ftp:) cannot be a cross-origin media host → passed through.
        var media = new Iri("data:image/png;base64,AAAA");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.Equal(media, result);
    }

    [Fact]
    public void UrlWithQueryIsPercentEncoded()
    {
        // The originator's URL may itself carry a query string; the whole URL is percent-encoded as the
        // proxy's ?url= value (the nested & becomes %26, so the proxy parses exactly one query param).
        var media = new Iri("https://cdn.example.com/cat.png?sig=abc&exp=123");

        var result = media.ToBrowserMediaIri(InstanceBase);

        Assert.StartsWith("https://a.test/ap/v1/media/proxy?url=", result.Value);
        Assert.Contains("sig%3Dabc%26exp%3D123", result.Value);
        // The raw & of the nested query must not appear (it would break the proxy's query parsing).
        var queryPart = result.Value[result.Value.IndexOf("?url=")..];
        Assert.DoesNotContain('&', queryPart);
    }

    [Fact]
    public void BaseWithTrailingSlash_IsNormalized()
    {
        var baseWithSlash = new Iri("https://a.test/");
        var media = new Iri("https://cdn.example.com/cat.png");

        var result = media.ToBrowserMediaIri(baseWithSlash);

        Assert.Equal("https://a.test/ap/v1/media/proxy?url=https%3A%2F%2Fcdn.example.com%2Fcat.png", result.Value);
    }
}
