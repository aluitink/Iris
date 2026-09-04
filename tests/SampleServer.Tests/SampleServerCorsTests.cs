using System.Net;
using Iris.Samples.SampleServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Phase 22.10 integration tests: the documented local (no-Docker) log-on path works out of the box.
/// The Blazor WebAssembly explorer dials the SampleServer cross-origin, so the server must answer CORS
/// preflights for the browser's origin. The default <c>Iris__CorsOrigins</c> (no env var set) must allow
/// both documented local UI origins — <c>http://localhost:8090</c> (the Docker <c>iris-ui</c> static
/// host) and <c>http://localhost:8080</c> (the local no-Docker Blazor dev server) — otherwise the
/// browser blocks the webfinger / actor-document requests and log-on fails (change 226, finding B).
/// </summary>
/// <remarks>
/// These tests host the real <see cref="SampleServer"/> in-process (via
/// <see cref="SampleServer.CreateWebHostBuilder"/> + a <see cref="TestServer"/>) and drive the full CORS
/// middleware with genuine cross-origin requests — a preflight <c>OPTIONS</c> and a simple
/// <c>GET</c> — so they prove the end-to-end behavior, not just the registered policy.
/// </remarks>
public sealed class SampleServerCorsTests : IDisposable
{
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public SampleServerCorsTests()
    {
        var builder = SampleServer.CreateWebHostBuilder();
        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose()
        => _server.Dispose();

    [Theory]
    [InlineData("http://localhost:8090")]
    [InlineData("http://localhost:8080")]
    public async Task Preflight_Options_CrossOrigin_AllowsTheDocumentedLocalOrigin(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/ap/v1/u/alice")
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
            },
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("http://localhost:8090")]
    [InlineData("http://localhost:8080")]
    public async Task Get_ActorDoc_CrossOrigin_AllowsTheDocumentedLocalOrigin(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ap/v1/u/alice")
        {
            Headers = { { "Origin", origin } },
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Get_ActorDoc_UnlistedOrigin_IsNotGrantedAccess()
    {
        // A non-allowlisted origin (not one of the documented local UI origins) must NOT be echoed —
        // the browser would then block the response. This guards against an accidental "allow any
        // origin" regression that would leak credentials.
        var request = new HttpRequestMessage(HttpMethod.Get, "/ap/v1/u/alice")
        {
            Headers = { { "Origin", "http://evil.example" } },
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "A non-allowlisted origin must not be granted Access-Control-Allow-Origin (that would leak credentials to an untrusted browser).");
    }
}
