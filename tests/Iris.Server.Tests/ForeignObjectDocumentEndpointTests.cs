using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// 139.3-s6 Finding 2 (follow-up) — foreign-IRI object pages via the object-document catch-all.
/// </summary>
/// <remarks>
/// The catch-all reconstructs the lookup IRI as <c>base + route prefix + path</c> (a LOCAL IRI), so a
/// stored FOREIGN object (e.g. <c>https://lemmy.luit.ink/post/1</c>) is unreachable by path — its IRI's
/// host is not this instance's, so the local-IRI lookup misses and the endpoint 404s. The fix: an
/// explicit <c>?iri=</c> query parameter overrides the path-based reconstruction, so a stored foreign
/// object can be served by its exact (absolute) IRI. These tests pin that behavior plus the
/// backward-compatible path-based lookup for local objects.
/// </remarks>
public sealed class ForeignObjectDocumentEndpointTests : IDisposable
{
    private const string BaseUri = "https://iris.luit.ink";
    private const string ForeignIri = "https://lemmy.luit.ink/post/1";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly HttpClient _http;

    public ForeignObjectDocumentEndpointTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = BaseUri,
                ["Iris:InstanceActorId"] = $"{BaseUri}/ap/v1/u/alice",
                ["Iris:SharedInboxIri"] = $"{BaseUri}/ap/v1/shared-inbox",
            })
            .Build();

        var persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(persistence, "iris.luit.ink", "alice");
        var key = seeded.Key;
        var actorIri = seeded.ActorIri;

        // A stored FOREIGN object (a different host than this instance) — the s6 F2 case.
        var foreignObject = new Note
        {
            Id = ForeignIri,
            Content = ["a foreign object served by ?iri="],
        };
        persistence.Objects.PutObjectAsync(foreignObject).GetAwaiter().GetResult();

        // A stored LOCAL object (this instance's host) — the backward-compat regression case.
        var localObject = new Note
        {
            Id = $"{BaseUri}/ap/v1/u/alice/notes/n1",
            Content = ["a local object served by path"],
        };
        persistence.Objects.PutObjectAsync(localObject).GetAwaiter().GetResult();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(services =>
            {
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                services.AddRouting();
                services.AddActivityPubServer(config);
                services.AddInMemoryPersistence();
                services.AddSingleton<IPersistenceProvider>(persistence);

                var keyStore = new InMemoryKeyStore();
                keyStore.PutKey(key);
                var keyProvider = new InMemoryKeyProvider(keyStore);
                keyProvider.RegisterKey(actorIri, key.KeyId);
                var signer = new HttpSignatureSigner(keyStore);

                services.AddSingleton<IKeyStore>(keyStore);
                services.AddSingleton<IKeyProvider>(keyProvider);
                services.AddSingleton<ISignatureSigner>(signer);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        _server = new TestServer(builder);
        _services = _server.Services;
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task ForeignObject_ByPath_Returns404()
    {
        // The catch-all reconstructs a LOCAL IRI (base + route prefix + path), so a foreign object's
        // IRI (a different host) is not found by path — the existing s6 F2 gap.
        var response = await _http.GetAsync($"{BaseUri}/ap/v1/lemmy.luit.ink/post/1");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignObject_ByExplicitIri_Returns200WithStoredObject()
    {
        // The fix: ?iri= overrides the path-based reconstruction, so the stored foreign object is
        // served by its exact (absolute) IRI.
        var response = await _http.GetAsync(
            $"{BaseUri}/ap/v1/object?iri={Uri.EscapeDataString(ForeignIri)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("a foreign object served by ?iri=", body);
        // The served document carries the foreign IRI as its id (not the local-IRI reconstruction).
        Assert.Contains(ForeignIri, body);
    }

    [Fact]
    public async Task LocalObject_ByPath_StillReturns200()
    {
        // Backward compatibility: a local object fetched by path (no ?iri=) still resolves via the
        // existing base + route prefix + path reconstruction.
        var response = await _http.GetAsync($"{BaseUri}/ap/v1/u/alice/notes/n1");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("a local object served by path", body);
    }

    [Fact]
    public async Task LocalObject_ByExplicitIri_Returns200()
    {
        // ?iri= works for a local object too (it just uses the absolute IRI directly).
        var localIri = $"{BaseUri}/ap/v1/u/alice/notes/n1";
        var response = await _http.GetAsync($"{BaseUri}/ap/v1/object?iri={Uri.EscapeDataString(localIri)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("a local object served by path", body);
    }

    [Fact]
    public async Task UnknownIri_ByExplicitIri_Returns404()
    {
        // An ?iri= that names a stored-but-unknown object 404s (the lookup misses cleanly).
        var response = await _http.GetAsync(
            $"{BaseUri}/ap/v1/object?iri={Uri.EscapeDataString("https://lemmy.luit.ink/post/999")}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RelativeIri_QueryParam_Ignored_FallsBackToPath()
    {
        // A relative ?iri= is not an absolute IRI, so it is ignored and the path-based reconstruction
        // is used (here: a path that names no local object → 404, not a crash).
        var response = await _http.GetAsync($"{BaseUri}/ap/v1/lemmy.luit.ink/post/1?iri=post%2F1");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
