using System.Net;
using System.Net.Http;
using Iris.Core.Identity;

namespace Iris.Server.Media;

/// <summary>
/// The default <see cref="IMediaFetcher"/>: an unsigned, outbound GET of a remote media URL using a
/// pre-built <see cref="HttpClient"/> (Phase 20.4 (d)).
/// </summary>
/// <remarks>
/// Per the coding style, library code takes a pre-built <see cref="HttpClient"/> (or an
/// <see cref="IHttpClientFactory"/>), never owns one. The client is created once by
/// <see cref="ActivityPubServerExtensions"/> (an <c>IHttpClientFactory</c>-backed client with a
/// generous timeout) and handed here. A successful fetch returns the bytes + the remote
/// <c>Content-Type</c>; any failure (a non-success status, a network error, or a timeout) returns
/// <see langword="null"/> — a dead or unreachable remote URL is an expected condition, not an error.
/// </remarks>
public sealed class DefaultMediaFetcher : IMediaFetcher
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new media fetcher over a pre-built <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="http">The HTTP client used for outbound media fetches. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="http"/> is null.</exception>
    public DefaultMediaFetcher(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <inheritdoc/>
    public async Task<FetchedMedia?> FetchAsync(Iri sourceUrl, CancellationToken ct = default)
    {
        // The URL must be absolute (a remote attachment's url always is). A relative or non-http(s)
        // URL is not fetchable; treat it as a fetch failure (the proxy maps it to a 502).
        if (!Uri.TryCreate(sourceUrl.Value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType
                ?? "application/octet-stream";
            return new FetchedMedia(content, contentType);
        }
        catch (HttpRequestException)
        {
            // A network error (DNS failure, connection refused, TLS failure) is an expected condition
            // for an outbound fetch of an arbitrary remote URL: report it as a failed fetch.
            return null;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested == false)
        {
            // A timeout (a TaskCanceledException not caused by the caller's token) is also an
            // expected fetch failure.
            return null;
        }
    }
}
