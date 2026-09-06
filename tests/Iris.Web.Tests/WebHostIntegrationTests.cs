using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Server;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the Iris.Web production host's <em>backend/API surface</em> (per the
/// library's integration-first convention): they boot the real app — via
/// <see cref="WebAppFactory.CreateWebApplication"/> — in an in-process <see cref="TestServer"/> and
/// assert the federation routes (WebFinger, actor document, inbox, outbox, NodeInfo) work inside the
/// new Blazor Web App process, plus that the Blazor landing page is served.
/// </summary>
/// <remarks>
/// The host is built with a fixed advertised base (<c>https://web.test.local</c>) so the actor IRI,
/// the WebFinger <c>subject</c>, and the namespace assertions are deterministic. The UI/component
/// layer is deliberately not covered here — it is verified with Playwright MCP (no bUnit project yet,
/// by design; see production-app-web-host.md §6).
/// </remarks>
public class WebHostIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";
    private const string Handle = WebAppFactory.SeedHandle;

    private readonly TestServer _server;
    private readonly HttpClient _client;

    public WebHostIntegrationTests()
    {
        // Configure the real service graph through the same composition root the production entry point
        // uses, then host it in-process. builder.WebHost cannot be passed to TestServer directly
        // (TestServer's UseTestServer mutates the shared service collection, which WebApplicationBuilder
        // has already frozen), so an independent IWebHostBuilder reuses the same configured collection and
        // re-runs the same endpoint pipeline. TestServer never binds a real port, so the app's default
        // http://localhost:8088 URL is irrelevant here.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapActivityPubEndpoints();
                    endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
                });
            });

        _server = new TestServer(webHostBuilder);
        // Seed + key registration run AFTER the host has started (outside the synchronous Configure
        // delegate, which would deadlock resolving a singleton whose factory awaits an async startup
        // service on the captured sync context).
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task WebFinger_ResolvesSeededActor()
    {
        var response = await _client.GetAsync($"/.well-known/webfinger?resource=acct:{Handle}@web.test.local");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(document);
        // JRD document: subject is the acct URI; the self link's href is the actor document IRI.
        Assert.Equal($"acct:{Handle}@web.test.local", json.RootElement.GetProperty("subject").GetString());
        var link = json.RootElement.GetProperty("links")[0];
        Assert.Equal("self", link.GetProperty("rel").GetString());
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}", link.GetProperty("href").GetString());
    }

    [Fact]
    public async Task ActorDocument_IsServedWithHandleAndPublicKey()
    {
        var response = await _client.GetAsync($"/ap/v1/u/{Handle}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(document);
        var root = json.RootElement;
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}", root.GetProperty("id").GetString());
        Assert.Equal(Handle, root.GetProperty("preferredUsername").GetString());
        // The public signing key is part of the public document (a remote resolver reads it to verify
        // signatures); the owner-only privateKey extension is absent on the unauthenticated read.
        Assert.True(root.TryGetProperty("publicKey", out _), "the actor document should carry its publicKey extension");
        Assert.False(root.TryGetProperty("privateKey", out _), "the privateKey extension must not be on the public document");
    }

    [Fact]
    public async Task Inbox_CollectionIsServedToOwner()
    {
        // The actor document advertises the per-actor inbox IRI (Decision 056: the inbox is a
        // first-class, private, per-actor collection).
        var actorResponse = await _client.GetAsync($"/ap/v1/u/{Handle}");
        var actorDocument = await actorResponse.Content.ReadAsStringAsync();
        using var actorJson = JsonDocument.Parse(actorDocument);
        var inboxIri = actorJson.RootElement.GetProperty("inbox").GetString();
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}/inbox", inboxIri);

        // The inbox is owner-only (Basic auth). Unauthenticated reads are rejected (403).
        var anonymousResponse = await _client.GetAsync(inboxIri!);
        Assert.Equal(HttpStatusCode.Forbidden, anonymousResponse.StatusCode);

        // An owner-authenticated read is served. The bare host registers the seeded actor's
        // credential validator (handle/handle), so the owner read returns the inbox collection.
        using var ownerRequest = new HttpRequestMessage(HttpMethod.Get, inboxIri!);
        ownerRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:{Handle}")));
        var ownerResponse = await _client.SendAsync(ownerRequest);
        var ownerBody = await ownerResponse.Content.ReadAsStringAsync();
        Assert.True(
            ownerResponse.StatusCode == HttpStatusCode.OK,
            $"expected 200 but got {(int)ownerResponse.StatusCode}: {ownerBody}");
        using var inboxJson = JsonDocument.Parse(ownerBody);
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}/inbox", inboxJson.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Outbox_CollectionIsServed()
    {
        var actorResponse = await _client.GetAsync($"/ap/v1/u/{Handle}");
        var actorDocument = await actorResponse.Content.ReadAsStringAsync();
        using var actorJson = JsonDocument.Parse(actorDocument);
        var outboxIri = actorJson.RootElement.GetProperty("outbox").GetString();
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}/outbox", outboxIri);

        var outboxResponse = await _client.GetAsync(outboxIri!);
        var outboxDocument = await outboxResponse.Content.ReadAsStringAsync();
        Assert.True(
            outboxResponse.StatusCode == HttpStatusCode.OK,
            $"expected 200 but got {(int)outboxResponse.StatusCode}: {outboxDocument}");
        using var outboxJson = JsonDocument.Parse(outboxDocument);
        Assert.Equal($"https://web.test.local/ap/v1/u/{Handle}/outbox", outboxJson.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task NodeInfo_IsServed()
    {
        // NodeInfo discovery root lives under the versioned prefix: /ap/v1/.well-known/nodeinfo.
        var response = await _client.GetAsync("/ap/v1/.well-known/nodeinfo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(document);
        // The discovery root links to the version-2.0 document at {base}/ap/v1/nodeinfo/2.0.
        var link = json.RootElement.GetProperty("links")[0];
        Assert.Equal("http://nodeinfo.dpl.dev/ns/1.0/nodeinfo", link.GetProperty("rel").GetString());
        Assert.Equal("https://web.test.local/ap/v1/nodeinfo/2.0", link.GetProperty("href").GetString());
    }

    [Fact]
    public async Task LandingPage_IsServedByBlazorHost()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Iris", html);
        Assert.Contains("blazor.web.js", html);
    }
}
