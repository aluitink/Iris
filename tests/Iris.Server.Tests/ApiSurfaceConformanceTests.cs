using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 22.7 integration tests: the API-surface conformance contract. Every property the server emits
/// on a public actor/community document is classified as either a <em>core ActivityStreams/ActivityPub</em>
/// term (emitted <strong>bare</strong>) or an <em>Iris-invented extension</em> (emitted under the
/// <c>iris:</c> namespace, the full IRI key <c>{NamespaceIri}{term}</c>). These tests pin that
/// classification on the actual wire:
/// <list type="bullet">
/// <item>the <c>@context</c> declares the core AS context <em>and</em> the <c>iris:</c> namespace
/// (<c>@vocab</c>), so the Iris extensions are resolvable JSON-LD terms;</item>
/// <item>the core-AP terms (<c>id</c>/<c>type</c>/<c>inbox</c>/<c>outbox</c>/<c>followers</c>/<c>following</c>,
/// plus <c>members</c> on a community) are present bare;</item>
/// <item>the Iris-invented collection endpoints (<c>feed</c>/<c>blocks</c>/<c>flags</c>/<c>mutes</c>/<c>star</c>
/// on a person, <c>feed</c>/<c>search</c>/<c>blocks</c>/<c>flags</c>/<c>mutes</c> on a community) are
/// present namespaced — and their bare forms are <em>absent</em> (no un-namespaced Iris extension term);</item>
/// <item>the <c>iris:capabilities</c> / <c>iris:settings</c> extensions are namespaced.</item>
/// </list>
/// </summary>
public sealed class ApiSurfaceConformanceTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Community = "devs";
    private const string AsContext = "https://www.w3.org/ns/activitystreams";
    private const string IrisNs = "https://iris.example/ns#";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _communityIri;

    public ApiSurfaceConformanceTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _aliceIri = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice).ActorIri;
        _communityIri = TestSeeder.SeedCommunityWithKey(_persistence, AHost, Community).CommunityIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Person document -----------------------------------------------------------------

    [Fact]
    public async Task PersonDoc_Context_DeclaresCoreAsAndIrisNamespace()
    {
        var doc = await FetchAsync(_aliceIri);

        // @context is an array: [core AS context, { "@vocab": iris namespace }].
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("@context").ValueKind);
        var context = doc.RootElement.GetProperty("@context");
        Assert.Equal(AsContext, context[0].GetString());
        Assert.Equal(IrisNs, context[1].GetProperty("@vocab").GetString());
    }

    [Fact]
    public async Task PersonDoc_CoreApTerms_AreBare()
    {
        var doc = await FetchAsync(_aliceIri);
        var el = doc.RootElement;

        Assert.Equal(_aliceIri.Value, el.GetProperty("id").GetString());
        Assert.Equal("Person", el.GetProperty("type").GetString());
        Assert.Equal($"{_aliceIri.Value}/inbox", el.GetProperty("inbox").GetString());
        Assert.Equal($"{_aliceIri.Value}/outbox", el.GetProperty("outbox").GetString());
        Assert.Equal($"{_aliceIri.Value}/followers", el.GetProperty("followers").GetString());
        Assert.Equal($"{_aliceIri.Value}/following", el.GetProperty("following").GetString());
    }

    [Fact]
    public async Task PersonDoc_IrisExtensions_AreNamespacedNotBare()
    {
        var doc = await FetchAsync(_aliceIri);
        var el = doc.RootElement;

        // Each Iris-invented collection endpoint is present under the iris: namespace ...
        Assert.Equal($"{_aliceIri.Value}/feed", el.GetProperty(IrisNs + CollectionExtensionNames.Feed).GetString());
        Assert.Equal($"{_aliceIri.Value}/blocks", el.GetProperty(IrisNs + CollectionExtensionNames.Blocks).GetString());
        Assert.Equal($"{_aliceIri.Value}/flags", el.GetProperty(IrisNs + CollectionExtensionNames.Flags).GetString());
        Assert.Equal($"{_aliceIri.Value}/mutes", el.GetProperty(IrisNs + CollectionExtensionNames.Mutes).GetString());
        Assert.Equal($"{_aliceIri.Value}/relays", el.GetProperty(IrisNs + CollectionExtensionNames.Star).GetString());

        // ... and the bare (un-namespaced) forms are ABSENT — no un-namespaced Iris extension term on the wire.
        Assert.False(el.TryGetProperty("feed", out _), "a bare 'feed' term must not be present");
        Assert.False(el.TryGetProperty("blocks", out _), "a bare 'blocks' term must not be present");
        Assert.False(el.TryGetProperty("flags", out _), "a bare 'flags' term must not be present");
        Assert.False(el.TryGetProperty("mutes", out _), "a bare 'mutes' term must not be present");
        Assert.False(el.TryGetProperty("star", out _), "a bare 'star' term must not be present");

        // publicKey is a core-AP/AS ecosystem-convention term — present BARE (not namespaced), unlike the
        // Iris-invented extensions above (a remote instance such as Mastodon expects exactly this shape).
        var publicKey = el.GetProperty(ActivityPubExtensionNames.PublicKey);
        Assert.Equal(JsonValueKind.Object, publicKey.ValueKind);
        Assert.StartsWith(_aliceIri.Value, publicKey.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PersonDoc_Capabilities_IsNamespaced()
    {
        var doc = await FetchAsync(_aliceIri);
        var el = doc.RootElement;

        // iris:capabilities is a namespaced array; the bare 'capabilities' term is absent.
        var caps = el.GetProperty(IrisNs + ActivityPubServerConstants.CapabilitiesTerm);
        Assert.Equal(JsonValueKind.Array, caps.ValueKind);
        Assert.Contains(ActivityPubServerConstants.CapabilityMute, caps.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(ActivityPubServerConstants.CapabilityRelay, caps.EnumerateArray().Select(e => e.GetString()));
        Assert.False(el.TryGetProperty("capabilities", out _), "a bare 'capabilities' term must not be present");
    }

    // --- Community document ----------------------------------------------------------------

    [Fact]
    public async Task CommunityDoc_CoreApAndAsTerms_AreBare()
    {
        var doc = await FetchAsync(_communityIri);
        var el = doc.RootElement;

        Assert.Equal(_communityIri.Value, el.GetProperty("id").GetString());
        Assert.Equal("Group", el.GetProperty("type").GetString());
        Assert.Equal($"{_communityIri.Value}/inbox", el.GetProperty("inbox").GetString());
        Assert.Equal($"{_communityIri.Value}/outbox", el.GetProperty("outbox").GetString());
        Assert.Equal($"{_communityIri.Value}/followers", el.GetProperty("followers").GetString());
        Assert.Equal($"{_communityIri.Value}/following", el.GetProperty("following").GetString());
        // members is a core ActivityStreams Group term — emitted bare (NOT namespaced).
        Assert.Equal($"{_communityIri.Value}/members", el.GetProperty("members").GetString());
    }

    [Fact]
    public async Task CommunityDoc_IrisExtensions_AreNamespacedNotBare()
    {
        var doc = await FetchAsync(_communityIri);
        var el = doc.RootElement;

        // feed/search/blocks/flags/mutes are Iris extensions — namespaced ...
        Assert.Equal($"{_communityIri.Value}/feed", el.GetProperty(IrisNs + CollectionExtensionNames.Feed).GetString());
        Assert.Equal($"{_communityIri.Value}/search", el.GetProperty(IrisNs + CollectionExtensionNames.Search).GetString());
        Assert.Equal($"{_communityIri.Value}/blocks", el.GetProperty(IrisNs + CollectionExtensionNames.Blocks).GetString());
        Assert.Equal($"{_communityIri.Value}/flags", el.GetProperty(IrisNs + CollectionExtensionNames.Flags).GetString());
        Assert.Equal($"{_communityIri.Value}/mutes", el.GetProperty(IrisNs + CollectionExtensionNames.Mutes).GetString());

        // ... and their bare forms are ABSENT.
        Assert.False(el.TryGetProperty("feed", out _), "a bare 'feed' term must not be present");
        Assert.False(el.TryGetProperty("search", out _), "a bare 'search' term must not be present");
        Assert.False(el.TryGetProperty("blocks", out _), "a bare 'blocks' term must not be present");
        Assert.False(el.TryGetProperty("flags", out _), "a bare 'flags' term must not be present");
        Assert.False(el.TryGetProperty("mutes", out _), "a bare 'mutes' term must not be present");

        // The community also carries the bare core-AS publicKey ecosystem-convention term (see the
        // person document test above).
        Assert.Equal(JsonValueKind.Object, el.GetProperty(ActivityPubExtensionNames.PublicKey).ValueKind);
    }

    // --- Search page document --------------------------------------------------------------

    [Fact]
    public async Task CommunitySearchPage_QueryIsNamespacedNotBare()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_communityIri.Value}/search?q=alice");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var el = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The query is recorded under the iris: namespace (namespaced) ...
        Assert.Equal("alice", el.GetProperty(IrisNs + IrisExtensionTerms.SearchQuery).GetString());

        // ... and the bare (un-namespaced) form is ABSENT.
        Assert.False(el.TryGetProperty(IrisExtensionTerms.SearchQuery, out _),
            "a bare 'searchQuery' term must not be present");

        // The document itself stays a standard AS collection (page 1 is an OrderedCollection).
        Assert.Equal("OrderedCollection", el.GetProperty("type").GetString());
    }

    // --- Helpers ---------------------------------------------------------------------------

    private async Task<JsonDocument> FetchAsync(Iri iri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, iri.Value);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
