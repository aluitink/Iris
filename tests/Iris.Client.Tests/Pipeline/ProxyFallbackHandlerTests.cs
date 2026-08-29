using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="ProxyFallbackHandler"/> (Phase 6's client-side proxy fallback). A
/// request to a remote instance rejected with 401/403 is retried through the home instance's proxy
/// endpoint (<c>POST {proxyBase}/ap/v1/proxy/{target}</c>) with Basic auth; a successful direct
/// response is returned unchanged.
/// </summary>
public sealed class ProxyFallbackHandlerTests
{
    private const string ProxyBase = "https://a.example";
    private const string Username = "alice";
    private const string Password = "s3cret!";

    // A recording handler that records the outgoing request and returns a pre-configured response.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status)
        => new(status) { Content = new StringContent("{\"id\":\"remote\"}") };

    private static ProxyFallbackHandler NewHandler(RecordingHandler inner)
        => new(new Iri(ProxyBase), new ProxyCredentials(Username, Password), inner);

    // --- A 401 on the direct attempt is retried through the proxy -------------------------

    [Fact]
    public async Task Direct401_RetriesThroughProxyWithBasicAuth()
    {
        var inner = new RecordingHandler(req =>
        {
            // The first request is the direct attempt (GET to the remote); the second is the proxy
            // POST. Return 401 for the direct, 200 for the proxy.
            var isProxy = req.RequestUri!.AbsolutePath.StartsWith("/ap/v1/proxy/", StringComparison.Ordinal);
            return JsonResponse(isProxy ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        });

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://b.example/ap/v1/u/bob");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The proxy request was a POST to /ap/v1/proxy/{target} with the Basic-auth Authorization.
        var proxyReq = inner.LastRequest!;
        Assert.Equal(HttpMethod.Post, proxyReq.Method);
        Assert.Equal(
            $"{ProxyBase}/ap/v1/proxy/https://b.example/ap/v1/u/bob",
            proxyReq.RequestUri!.ToString());
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        Assert.Equal(expected, proxyReq.Headers.Authorization?.ToString());
    }

    // --- A 403 on the direct attempt is also retried through the proxy -------------------

    [Fact]
    public async Task Direct403_RetriesThroughProxy()
    {
        var inner = new RecordingHandler(req =>
        {
            var isProxy = req.RequestUri!.AbsolutePath.StartsWith("/ap/v1/proxy/", StringComparison.Ordinal);
            return JsonResponse(isProxy ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        });

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://b.example/x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Post, inner.LastRequest!.Method);
    }

    // --- A successful direct response is returned unchanged (no proxy) -------------------

    [Fact]
    public async Task DirectSuccess_IsNotRoutedThroughProxy()
    {
        var inner = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK));

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://b.example/x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Only one request was made (the direct attempt), and it was the direct GET (not the proxy).
        var last = inner.LastRequest!;
        Assert.Equal(HttpMethod.Get, last.Method);
        Assert.Equal("https://b.example/x", last.RequestUri!.ToString());
    }

    // --- A non-401/403 failure (e.g. 404) is returned as-is ------------------------------

    [Fact]
    public async Task Direct404_IsNotRoutedThroughProxy()
    {
        var inner = new RecordingHandler(_ => JsonResponse(HttpStatusCode.NotFound));

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://b.example/missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpMethod.Get, inner.LastRequest!.Method);
    }

    // --- The proxy's own failure is returned (no loop) -----------------------------------

    [Fact]
    public async Task ProxyFailure_IsReturnedAsIs()
    {
        // Direct 401 → fallback to proxy → proxy itself returns 401 (no further fallback).
        var inner = new RecordingHandler(req =>
        {
            var isProxy = req.RequestUri!.AbsolutePath.StartsWith("/ap/v1/proxy/", StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.Unauthorized); // both direct and proxy 401
        });

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://b.example/x"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The client's Accept header is relayed to the proxy ------------------------------

    [Fact]
    public async Task AcceptHeader_IsRelayedToProxy()
    {
        var inner = new RecordingHandler(req =>
        {
            var isProxy = req.RequestUri!.AbsolutePath.StartsWith("/ap/v1/proxy/", StringComparison.Ordinal);
            return JsonResponse(isProxy ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        });

        var handler = NewHandler(inner);
        using var http = new HttpClient(handler, disposeHandler: false);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://b.example/x");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json"));
        await http.SendAsync(request);

        Assert.Contains(
            inner.LastRequest!.Headers.Accept,
            h => h.MediaType == "application/activity+json");
    }
}
