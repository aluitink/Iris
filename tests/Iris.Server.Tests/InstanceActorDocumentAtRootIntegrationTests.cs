using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// 138.4 integration test for the <strong>instance-actor document at the instance root</strong>
/// (<c>GET /</c>). A remote platform (notably Lemmy) dereferences the site actor from the instance's
/// root URL before it will resolve any object on the instance; when the root served only the SPA
/// shell (HTML), that dereference failed and every remote→local resolve was blocked. These tests pin
/// the content-negotiated root: an ActivityPub client (an <c>Accept</c> header naming an
/// ActivityStreams / JSON-LD media type) gets the instance actor's public document, while a
/// non-ActivityPub request (no Accept, or a browser text/html) gets a 404 so the host's SPA fallback
/// can serve the shell.
/// </summary>
public sealed class InstanceActorDocumentAtRootIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _http;

    public InstanceActorDocumentAtRootIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);

        _server = StartServer(persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{AHost}"),
        };
        _ = aliceIri;
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Root_WithActivityPlusJsonAccept_ReturnsInstanceActorDocument()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The instance actor (alice) is what the root serves; its IRI, type, and collection endpoints
        // are present so a remote site-actor dereference (Lemmy) succeeds.
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", root.GetProperty("id").GetString());
        Assert.Equal("Person", root.GetProperty("type").GetString());
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/inbox", root.GetProperty("inbox").GetString());
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/outbox", root.GetProperty("outbox").GetString());
    }

    [Fact]
    public async Task Root_WithLdJsonAccept_ReturnsInstanceActorDocument()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/ld+json"));

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Root_WithHtmlAccept_ReturnsNotFound_ForSpaFallback()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await _http.SendAsync(request);

        // A browser navigating home is not an ActivityPub client: the root 404s so the host's SPA
        // fallback (mapped after the ActivityPub endpoints) serves the shell.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Root_WithoutAcceptHeader_ReturnsNotFound()
    {
        // No Accept header (a bare curl) is not an ActivityPub client either.
        using var response = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Root_DoesNotLeakPrivateKey()
    {
        // The root always serves the PUBLIC form — never the owner-only privateKey extension, even to
        // an ActivityPub client (the owner-only form requires authentication for the instance actor,
        // which a bare root GET is not).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));

        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("privateKey", body);
    }

    private static TestServer StartServer(Iris.Server.InMemory.InMemoryPersistenceProvider persistence)
    {
        var instanceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(new Iri($"{instanceActorIri.Value}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{AHost}");
                    opts.InstanceName = "iris-test";
                    opts.InstanceActorId = instanceActorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
                s.AddSingleton<ISignatureSigner>(signer);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }
}
