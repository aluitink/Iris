using System.Net;

namespace Iris.Core.Identity;

/// <summary>
/// The client's render boundary for browser-loadable external media (Phase 20.4 (d), Decision 057):
/// rewrites a cross-origin media IRI to a same-origin media-proxy IRI so the browser loads it from the
/// instance's own origin (never a cross-origin media host), and passes a same-origin IRI through
/// unchanged.
/// </summary>
/// <remarks>
/// The wire stays 100% AP-native/verbatim in every collection — the rewrite happens here, at the
/// render boundary, not in any server serializer. The media IRI (the originator's attachment
/// <c>url</c>) is the stable key: the client always has it, so a cold external object the content hash
/// could not pre-cover still works. A cross-origin IRI becomes
/// <c>{base}/ap/v1/media/proxy?url={percent-encode(originator-url)}</c>; a same-origin IRI (the
/// instance's own <c>/ap/v1/media/{id}</c>) is returned unchanged (it already loads from the same
/// origin). A relative or non-HTTP(S) IRI is returned unchanged (it cannot be classified as
/// cross-origin).
/// </remarks>
public static class IriMediaProxyExtensions
{
    /// <summary>
    /// Rewrites a media IRI to its browser-loadable form relative to an instance base (the render
    /// boundary, Phase 20.4 (d)).
    /// </summary>
    /// <param name="mediaIri">The media IRI (the originator's attachment <c>url</c>).</param>
    /// <param name="instanceBase">The instance's base IRI (e.g. <c>https://a.test</c>); a media IRI on
    /// this host is same-origin and passed through unchanged.</param>
    /// <returns>
    /// The same-origin media-proxy IRI (<c>{instanceBase}/ap/v1/media/proxy?url={mediaIri}</c>) when
    /// <paramref name="mediaIri"/> is absolute HTTP(S) on a different host; otherwise
    /// <paramref name="mediaIri"/> unchanged (same-origin, or not classifiable).
    /// </returns>
    public static Iri ToBrowserMediaIri(this Iri mediaIri, Iri instanceBase)
    {
        if (!mediaIri.IsAbsolute || !instanceBase.IsAbsolute)
        {
            return mediaIri;
        }

        if (!Uri.TryCreate(mediaIri.Value, UriKind.Absolute, out var mediaUri)
            || !Uri.TryCreate(instanceBase.Value, UriKind.Absolute, out var baseUri))
        {
            return mediaIri;
        }

        if (mediaUri.Scheme != Uri.UriSchemeHttp && mediaUri.Scheme != Uri.UriSchemeHttps)
        {
            return mediaIri;
        }

        // Same-origin (the instance's own media path) → pass through unchanged (it already loads from
        // the same origin).
        if (mediaUri.Host == baseUri.Host && mediaUri.Port == baseUri.Port)
        {
            return mediaIri;
        }

        // Cross-origin → the same-origin media-proxy IRI, the url percent-encoded as a query value.
        var basePrefix = instanceBase.Value.TrimEnd('/');
        return new Iri($"{basePrefix}/ap/v1/media/proxy?url={Uri.EscapeDataString(mediaIri.Value)}");
    }
}
