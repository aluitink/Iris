using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 5 integration test for the <strong>community endpoints</strong> slice: <c>GET /ap/v1/c/{name}</c>
/// (the community/<c>Group</c> document), <c>GET /ap/v1/c/{name}/members</c> (member IRIs as a paged
/// collection), and <c>POST /ap/v1/c/{name}/inbox</c> (signed federation activities addressed to the
/// community). These are public read endpoints plus the signature-gated inbox, so no federation is
/// required to exercise them — a single instance hosts the real endpoints over the in-process HTTP stack.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts a community <c>iris</c> with two local members
/// (alice, bob). The test asserts the community document (type <c>Group</c>, the standard collection
/// links, and the <c>members</c> extension), the members page (page 1 <c>OrderedCollection</c> with the
/// member IRIs + <c>totalItems</c>; page 2 an <c>OrderedCollectionPage</c> with <c>prev</c>/<c>next</c>),
/// the 404 for an unknown community, and the 401 for an unsigned inbox POST (the inbox's signature
/// policy). The happy-path inbox delivery (a signed activity stored + dispatched) is exercised by the
/// federation tests; here we assert the endpoint's auth policy and 404.
/// </remarks>
public sealed class CommunityEndpointIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider Persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunityEndpointIntegrationTests()
    {
        Persistence = new InMemoryPersistenceProvider();
        Seed(Persistence);
        _server = StartServer(Persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- GET /c/{name} is the Group document ------------------------------------

    [Fact]
    public async Task CommunityDocument_IsGroup_WithCollectionLinksAndMembersExtension()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Group", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(Community, doc.RootElement.GetProperty("preferredUsername").GetString());

        // The standard collection endpoints are present on the document.
        Assert.Equal($"{_base}/ap/v1/c/{Community}/inbox", doc.RootElement.GetProperty("inbox").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/outbox", doc.RootElement.GetProperty("outbox").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/followers", doc.RootElement.GetProperty("followers").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/following", doc.RootElement.GetProperty("following").GetString());

        // The members and feed extensions point at their endpoints.
        Assert.Equal($"{_base}/ap/v1/c/{Community}/members", doc.RootElement.GetProperty("members").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed", doc.RootElement.GetProperty("feed").GetString());
    }

    [Fact]
    public async Task CommunityDocument_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- GET /c/{name}/members is a paged collection of member IRIs -------------

    [Fact]
    public async Task Members_Page1_IsOrderedCollection_WithMemberIris()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/members");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/members", doc.RootElement.GetProperty("id").GetString());

        // Both local members are present (as bare IRI strings — the one-or-multiple converter renders a
        // single-Link item as its IRI), and totalItems reflects the full member count.
        var memberIris = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToHashSet();
        Assert.Equal(2, memberIris.Count);
        Assert.Contains($"{_base}/ap/v1/u/{Alice}", memberIris);
        Assert.Contains($"{_base}/ap/v1/u/{Bob}", memberIris);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Members_Page2_IsOrderedCollectionPage_WithPrevAndNext()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/members?limit=1&page=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/members/?page=2",
            doc.RootElement.GetProperty("id").GetString());

        // partOf points back at the collection; prev points to page 1. There is no page 3 (2 members,
        // limit 1), so there is no next.
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/members",
            doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/members/?page=1",
            doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _));
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Members_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody/members");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- GET /c/{name}/{following|followers} is the community's follow collections

    [Fact]
    public async Task Following_Page1_IsOrderedCollection_WithFollowedIris()
    {
        // Seed the community's follows set: the community follows two remote actors/communities.
        var communityIri = new Iri($"{_base}/ap/v1/c/{Community}");
        var remote1 = new Iri($"https://remote1.example/ap/v1/u/carol");
        var remote2 = new Iri($"https://remote2.example/ap/v1/c/hub");
        await Persistence.Communities.AddFollowAsync(communityIri, remote1);
        await Persistence.Communities.AddFollowAsync(communityIri, remote2);

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/following");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/following", doc.RootElement.GetProperty("id").GetString());

        // The followed IRIs are present (as bare IRI strings), and totalItems reflects the count.
        var followedIris = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToHashSet();
        Assert.Equal(2, followedIris.Count);
        Assert.Contains(remote1.Value, followedIris);
        Assert.Contains(remote2.Value, followedIris);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Followers_IsEmpty_WhenCommunityHasNoFollowers()
    {
        // A follow of a community records an edge in the community's *follows* set, not a followers set,
        // so a community being followed has no followers recorded: the followers collection is empty.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/followers");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/followers", doc.RootElement.GetProperty("id").GetString());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Following_UnknownCommunity_Returns404()
    {
        var following = await _http.GetAsync($"{_base}/ap/v1/c/nobody/following");
        Assert.Equal(HttpStatusCode.NotFound, following.StatusCode);
        var followers = await _http.GetAsync($"{_base}/ap/v1/c/nobody/followers");
        Assert.Equal(HttpStatusCode.NotFound, followers.StatusCode);
    }

    // --- POST /c/{name}/inbox requires a valid signature ------------------------

    [Fact]
    public async Task Inbox_Undersigned_Returns401()
    {
        var content = new StringContent(
            "{\"type\":\"Follow\",\"id\":\"" + _base + "/ap/v1/u/remote/follows/1\",\"object\":\"" +
            _base + "/ap/v1/c/" + Community + "\"}",
            Encoding.UTF8,
            "application/activity+json");
        var response = await _http.PostAsync($"{_base}/ap/v1/c/{Community}/inbox", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Inbox_UnknownCommunity_Unsigned_Returns401()
    {
        // The signature check runs before the community lookup, so an unsigned POST to an unknown
        // community's inbox is rejected with 401 (not 404 and not 500). This confirms the route exists
        // and the endpoint's signature policy is enforced regardless of the community's existence. The
        // signed happy path (store + dispatch) is covered by the federation tests.
        var response = await _http.PostAsync(
            $"{_base}/ap/v1/c/nobody/inbox",
            new StringContent("{}", Encoding.UTF8, "application/activity+json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds the persistence provider: a community <c>iris</c> (a <c>Group</c> actor) with two local
    /// members (alice, bob), via the shared <see cref="TestSeeder"/>.
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var communityIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        TestSeeder.AddMember(persistence, communityIri, aliceIri);
        TestSeeder.AddMember(persistence, communityIri, bobIri);
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> hosting the real community endpoints.
    /// </summary>
    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
    {
        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{AHost}");
                    opts.InstanceName = $"iris-{AHost}";
                    opts.InstanceActorId = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
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
