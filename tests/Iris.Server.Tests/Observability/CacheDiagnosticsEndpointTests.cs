using System.Net;
using System.Text.Json;
using Iris.Core.Caching;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 116.6 integration tests for the <c>GET /ap/v1/diagnostics/caches</c> endpoint: the endpoint
/// returns 200 with per-cache hit/miss/stale counters and entry counts. No authentication (an operator's
/// monitoring scrape reaches it without a signature), like the health endpoint.
/// </summary>
public sealed class CacheDiagnosticsEndpointTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Base = $"https://{AHost}";

    private readonly TestServer _server;
    private readonly HttpClient _http;

    public CacheDiagnosticsEndpointTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task DiagnosticsCaches_Returns200WithAllCaches()
    {
        var response = await _http.GetAsync($"{Base}/ap/v1/diagnostics/caches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caches = doc.RootElement.GetProperty("caches");

        Assert.True(caches.TryGetProperty("remoteActors", out _));
        Assert.True(caches.TryGetProperty("remoteKeys", out _));
        Assert.True(caches.TryGetProperty("remoteCollectionPages", out _));
        Assert.True(caches.TryGetProperty("webFinger", out _));
        Assert.True(caches.TryGetProperty("localActorDocuments", out _));
        Assert.True(caches.TryGetProperty("localCollectionPages", out _));

        // Every cache has the expected counter fields.
        foreach (var name in new[] { "remoteActors", "remoteKeys", "remoteCollectionPages", "webFinger", "localActorDocuments", "localCollectionPages" })
        {
            var cache = caches.GetProperty(name);
            Assert.Equal(0, cache.GetProperty("hits").GetInt64());
            Assert.Equal(0, cache.GetProperty("misses").GetInt64());
            Assert.Equal(0, cache.GetProperty("staleHits").GetInt64());
            Assert.Equal(0.0, cache.GetProperty("hitRate").GetDouble(), 4);
            Assert.Equal(0, cache.GetProperty("entries").GetInt32());
        }
    }

    [Fact]
    public async Task DiagnosticsCaches_ResponseCarriesVersionHeader()
    {
        var response = await _http.GetAsync($"{Base}/ap/v1/diagnostics/caches");

        Assert.True(response.Headers.TryGetValues("Iris-Version", out var values));
        Assert.Contains("1", values!);
    }

    [Fact]
    public async Task DiagnosticsCaches_NoAuthRequired()
    {
        // No Authorization header, no ActivityPub signature — the endpoint must still return 200.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/ap/v1/diagnostics/caches");
        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
