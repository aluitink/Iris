using Iris.Core.Identity;

namespace Iris.Server.Media;

/// <summary>
/// Fetches an external media URL's bytes + content-type (the media proxy, Phase 20.4 (d)): an
/// unsigned, outbound GET of a remote attachment <c>url</c> so the server can store it and serve it
/// back from the same origin (the browser never loads a cross-origin media host).
/// </summary>
/// <remarks>
/// A seam so the proxy route (<c>GET /ap/v1/media/proxy?url=…</c>) and the eager-warm hook can fetch
/// remote media without depending on a concrete HTTP client. The default implementation
/// (<see cref="DefaultMediaFetcher"/>) wraps an <see cref="System.Net.Http.IHttpClientFactory"/>
/// (per the coding style: no <see cref="HttpClient"/> ownership assumptions in library code). Fetch
/// failures (4xx/5xx, network error, timeout) are an expected condition — return
/// <see langword="null"/>; the caller (the proxy route) maps that to <c>502 Bad Gateway</c> so the
/// client's <c>&lt;img onerror&gt;</c> falls back to a link-out to the raw URL.
/// </remarks>
public interface IMediaFetcher
{
    /// <summary>
    /// Fetches the bytes of an external media URL.
    /// </summary>
    /// <param name="sourceUrl">The absolute URL of the remote media (the originator's attachment
    /// <c>url</c>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The fetched bytes and the remote <c>Content-Type</c>, or <see langword="null"/> when
    /// the fetch fails (4xx/5xx, network error, timeout).</returns>
    /// <remarks>
    /// Fetch failures are expected (the remote may be down or the URL dead); this method returns
    /// <see langword="null"/> rather than throwing, so the caller can decide the HTTP policy (a
    /// <c>502</c>). The bytes are read into memory (a media attachment is bounded by the same cap as
    /// an upload, enforced by the caller).
    /// </remarks>
    public Task<FetchedMedia?> FetchAsync(Iri sourceUrl, CancellationToken ct = default);
}

/// <summary>
/// The result of a successful <see cref="IMediaFetcher.FetchAsync"/>: the fetched bytes and the
/// remote server's <c>Content-Type</c>.
/// </summary>
/// <param name="Content">The fetched media bytes.</param>
/// <param name="ContentType">The remote <c>Content-Type</c> (e.g. <c>image/png</c>);
/// <c>application/octet-stream</c> when the remote did not report one.</param>
public sealed record FetchedMedia(byte[] Content, string ContentType);
