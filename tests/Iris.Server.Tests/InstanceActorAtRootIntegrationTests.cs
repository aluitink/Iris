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
/// 138.11 integration test for the <strong>site actor at the instance root</strong>. Lemmy's
/// <c>objects::instance</c> dereferences the site actor from the instance's root URL before it will
/// resolve any object on the instance, and requires that document to be an <c>Application</c>-type
/// actor (the ActivityPub site-actor convention Mastodon/Pleroma/Friendica follow). These tests pin
/// the new behavior: when <c>InstanceActorIri</c> names a dedicated <c>Application</c> (the site actor),
/// the root serves <em>that</em> actor's document (type <c>Application</c>) while the seeded
/// <c>Person</c> (alice) is still served, unchanged, at her own versioned IRI.
/// </summary>
public sealed class InstanceActorAtRootIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string SiteHandle = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _http;

    public InstanceActorAtRootIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        // alice remains a Person at her versioned IRI (the seeded local account).
        TestSeeder.SeedPerson(persistence, AHost, Alice);
        // The site actor: an Application at the bare instance base (the instance root).
        var siteActorIri = $"https://{AHost}";
        TestSeeder.SeedApplicationWithKey(persistence, siteActorIri, "iris-test", SiteHandle);

        _server = StartServer(persistence, new Iri(siteActorIri));
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{AHost}"),
        };
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Root_ServesSiteActorDocument_TypeIsApplication()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The root serves the SITE actor (the Application at the bare base), NOT alice.
        Assert.Equal($"https://{AHost}", root.GetProperty("id").GetString());
        Assert.Equal("Application", root.GetProperty("type").GetString());
        Assert.Equal(SiteHandle, root.GetProperty("preferredUsername").GetString());
        // BuildActorDocument auto-fills the standard collection endpoints (Lemmy dereferences these).
        Assert.Equal($"https://{AHost}/inbox", root.GetProperty("inbox").GetString());
        Assert.Equal($"https://{AHost}/outbox", root.GetProperty("outbox").GetString());
        // The site actor carries a public key (so a peer can verify the instance's outbound signatures).
        Assert.True(root.TryGetProperty("publicKey", out _), "root document must carry a publicKey");
    }

    [Fact]
    public async Task Alice_RemainsPerson_AtHerVersionedIri()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{Alice}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // alice is untouched: still a Person at her own IRI (only the instance root changed to the site actor).
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", root.GetProperty("id").GetString());
        Assert.Equal("Person", root.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Root_DoesNotLeakPrivateKey()
    {
        // The root always serves the PUBLIC form — never the owner-only privateKey extension.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("privateKey", body);
    }

    private static TestServer StartServer(
        Iris.Server.InMemory.InMemoryPersistenceProvider persistence,
        Iri siteActorIri)
    {
        // The site actor's key (it is InstanceActorId, so it signs outbound federation).
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(new Iri($"{siteActorIri.Value}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(siteActorIri, key.KeyId);
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
                    // 138.11: the instance signs outbound federation as the site actor AND serves it at
                    // the root — both point at the Application at the bare base.
                    opts.InstanceActorId = siteActorIri;
                    opts.InstanceActorIri = siteActorIri;
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
