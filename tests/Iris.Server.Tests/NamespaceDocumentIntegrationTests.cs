using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 31.8 integration tests: the deployment's <c>iris:</c> extension namespace is derived from the
/// instance's public (advertised) base URI and is <em>hosted</em> — the JSON-LD context declared as
/// <c>@vocab</c> in every public actor/community document is served at the namespace base, so the
/// advertised namespace is resolvable rather than a dangling IRI.
/// <list type="bullet">
/// <item>when <c>NamespaceIri</c> is unset, the namespace base is <c>{BaseUri}/ns#</c> (derived, not the
/// canonical <c>iris.example</c> default) — so each instance's namespace lives on its own host;</item>
/// <item>when <c>NamespaceIri</c> is explicitly set, it is used verbatim (an operator may pin a shared
/// namespace);</item>
/// <item>the namespace document is served at <c>{BaseUri}/ns</c> (the <c>GET /ns</c> route) with a
/// JSON-LD content type, declaring the core AS <c>@vocab</c> and the <c>iris:</c> extension terms;</item>
/// <item>the <c>@vocab</c> in an actor document resolves to the hosted document (the base is
/// <c>{BaseUri}/ns#</c>, the document is at <c>{BaseUri}/ns</c>).</item>
/// </list>
/// </summary>
public sealed class NamespaceDocumentIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;

    public NamespaceDocumentIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _aliceIri = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice).ActorIri;

        // The host sets BaseUri = https://a.domain.local and leaves NamespaceIri unset (the factory's
        // default pin is disabled via PinDefaultNamespace = false), so the namespace is DERIVED as
        // https://a.domain.local/ns# (the point of 31.8).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            PinDefaultNamespace = false,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task NamespaceIsDerivedFromPublicBaseUri_WhenNotConfigured()
    {
        var doc = await FetchJsonAsync(_aliceIri.Value);

        // @context is [core AS, { "@vocab": derived namespace }]; the derived base is {BaseUri}/ns#.
        var context = doc.GetProperty("@context");
        Assert.Equal(JsonValueKind.Array, context.ValueKind);
        var vocab = context[1].GetProperty("@vocab").GetString();
        Assert.Equal($"https://{AHost}/ns#", vocab);
        Assert.DoesNotContain("iris.example", vocab!);
    }

    [Fact]
    public async Task NamespaceDocument_IsServedAtNamespaceBase()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{AHost}/ns");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The hosted document is a JSON-LD context: it declares the core AS @vocab and the iris: namespace
        // object (keyed by its base, # stripped) mapping the extension terms to their ranges.
        var context = doc.RootElement.GetProperty("@context");
        Assert.Equal("https://www.w3.org/ns/activitystreams", context.GetProperty("@vocab").GetString());

        var ns = context.GetProperty($"https://{AHost}/ns");
        Assert.Equal($"https://{AHost}/ns#", ns.GetProperty("@id").GetString());
        // The collection endpoints and the IRI extensions are @id-valued; the search query is a string.
        Assert.Equal("@id", ns.GetProperty(Iris.Core.CollectionExtensionNames.Feed).GetString());
        Assert.Equal("@id", ns.GetProperty(Iris.Core.IrisExtensionTerms.Settings).GetString());
        Assert.Equal("string", ns.GetProperty(Iris.Core.IrisExtensionTerms.SearchQuery).GetString());
    }

    [Fact]
    public async Task NamespaceDocument_HasJsonLdContentTypeAndCacheHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{AHost}/ns");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("application/ld+json", response.Content.Headers.ContentType!.MediaType);
        // The vocabulary is immutable per deployment base URI; it is long-cacheable.
        var cacheControl = string.Join(";", response.Headers.GetValues("Cache-Control") ?? []);
        Assert.Contains("max-age=", cacheControl);
    }

    [Fact]
    public async Task ActorDocVocab_ResolvesToHostedNamespaceDocument()
    {
        var actorDoc = await FetchJsonAsync(_aliceIri.Value);
        var vocab = actorDoc.GetProperty("@context")[1].GetProperty("@vocab").GetString();

        // The @vocab is the namespace base with a # fragment; the hosted document lives at the base with
        // the fragment stripped (a JSON-LD processor fetching the vocab resolves the fragment against the
        // base document). Both must point at the same host's /ns.
        var baseUri = vocab!.TrimEnd('#');
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The hosted document's iris: namespace @id is the same IRI the actor declared as @vocab.
        using var nsDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ns = nsDoc.RootElement.GetProperty("@context").GetProperty(baseUri);
        Assert.Equal(vocab, ns.GetProperty("@id").GetString());
    }

    [Fact]
    public async Task ExplicitlyConfiguredNamespaceIri_IsUsedVerbatim()
    {
        // A deployment that pins a shared namespace (e.g. a canonical cross-instance vocabulary) keeps it:
        // the derivation (from BaseUri) must NOT override an explicit NamespaceIri.
        const string pinned = "https://shared.example/iris#";
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(persistence, AHost, Alice);
        using var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            PinDefaultNamespace = false,
            ExtraServices = s =>
                s.Configure<Iris.Server.ActivityPubServerOptions>(o => o.NamespaceIri = new Iri(pinned)),
        });
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        var doc = await FetchJsonAsync(http, $"https://{AHost}/ap/v1/u/{Alice}");
        var vocab = doc.GetProperty("@context")[1].GetProperty("@vocab").GetString();
        Assert.True(pinned == vocab, $"an explicit NamespaceIri must win over the derived base (got {vocab})");
    }

    private async Task<JsonElement> FetchJsonAsync(string url)
        => await FetchJsonAsync(_http, url);

    private static async Task<JsonElement> FetchJsonAsync(HttpClient http, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
