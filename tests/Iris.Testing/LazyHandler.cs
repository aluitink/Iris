using Microsoft.AspNetCore.TestHost;

namespace Iris.Testing;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that defers to a <see cref="TestServer"/> (or an inner
/// <see cref="HttpMessageHandler"/>) created after this handler — the "chicken-and-egg" case where a
/// server's own fetcher/delivery must reach the in-process <see cref="TestServer"/>, which does not
/// exist yet while the server is being constructed. Wraps the inner handler in an
/// <see cref="HttpClient"/> (whose <c>SendAsync</c> is public) and clones the request, because the
/// in-process transport does not clone between sends (and <see cref="HttpClient"/> forbids sending
/// the same request message more than once, which a retry pipeline may attempt).
/// </summary>
/// <remarks>
/// This is the single shared copy of the per-test <c>LazyHandler</c> that was previously duplicated
/// (nearly verbatim) across the federation integration suites.
/// </remarks>
public sealed class LazyHandler : HttpMessageHandler
{
    private readonly Func<HttpMessageHandler> _innerFactory;
    private HttpMessageHandler? _inner;
    private HttpClient? _client;

    /// <summary>
    /// Creates a handler that defers to a <paramref name="server"/> created later.
    /// </summary>
    public LazyHandler(Func<TestServer> server)
        : this(() => server().CreateHandler())
    {
    }

    /// <summary>
    /// Creates a handler that defers to an inner <see cref="HttpMessageHandler"/> created later.
    /// </summary>
    public LazyHandler(Func<HttpMessageHandler> innerFactory)
    {
        ArgumentNullException.ThrowIfNull(innerFactory);
        _innerFactory = innerFactory;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);

        // Clone the request: the inner pipeline may retry (RetryHandler), and HttpClient forbids
        // sending the same request message more than once.
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is { } content)
        {
            clone.Content = new ByteArrayContent(
                content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            foreach (var header in content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return _client.SendAsync(clone, cancellationToken);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client?.Dispose();
        }

        base.Dispose(disposing);
    }
}
