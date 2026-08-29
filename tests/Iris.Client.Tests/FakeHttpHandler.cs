using System.Net;
using System.Text;

namespace Iris.Client.Tests;

/// <summary>
/// A recording <see cref="HttpMessageHandler"/> that returns a pre-configured response and
/// captures the outgoing request for assertions. Used in place of a live server so the
/// <see cref="Iris.Client.Pipeline.SigningHandler"/> pipeline can be exercised end-to-end in unit tests.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>
    /// The last request passed to <see cref="SendAsync"/> (after the handler pipeline has run).
    /// </summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>
    /// The last request body bytes.
    /// </summary>
    public byte[] LastBody { get; private set; } = [];

    /// <summary>
    /// The last request URI.
    /// </summary>
    public Uri? LastUri => LastRequest?.RequestUri;

    /// <summary>
    /// Initializes a new <see cref="FakeHttpHandler"/> that always returns <paramref name="response"/>.
    /// </summary>
    /// <param name="response">The response to return for every request.</param>
    public FakeHttpHandler(HttpResponseMessage response)
        : this(_ => response)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="FakeHttpHandler"/> with a custom responder.
    /// </summary>
    /// <param name="responder">Invoked with the outgoing request; returns the response.</param>
    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        return _responder(request);
    }
}
