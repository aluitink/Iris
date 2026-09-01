using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 5 integration test for the <strong>specialized collections</strong> slice: the
/// <c>GET /ap/v1/c/{name}/search</c> endpoint (a case-insensitive search over the community's content —
/// the feed surface — paged via the shared <c>limit</c>/<c>offset</c> shape) and the
/// <c>iris:capabilities</c> extension on the community document (the client-discovery mechanism that
/// declares the community's available specialized collections).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts a community <c>iris</c> with two local members
/// (alice, bob) whose outboxes carry distinguishable content. The test asserts: the search matches
/// content case-insensitively; an empty query returns all items (the feed, unfiltered); a query with no
/// match returns an empty collection; paging via <c>?limit</c>/<c>?offset</c> works (page 1
/// <c>OrderedCollection</c>, page 2 <c>OrderedCollectionPage</c> with <c>prev</c>/<c>next</c>, the last
/// page has no <c>next</c>); an unknown community 404s; and the community document advertises
/// <c>iris:capabilities</c> = <c>[feed, members, search]</c> under the default namespace.
/// </remarks>
public sealed class CommunitySearchIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string DefaultNamespace = "https://iris.example/ns#";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly string _base = $"https://{AHost}";

    public CommunitySearchIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        Seed(persistence);
        _server = StartServer(persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Search matches content case-insensitively ---------------------------------

    [Fact]
    public async Task Search_MatchesContent_CaseInsensitive()
    {
        // "fED" matches alice's "FEDERAL" post and bob's "federation" post (case-insensitive), but not
        // alice's "GARDEN" post.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=fED&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search", doc.RootElement.GetProperty("id").GetString());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        // Feed order is newest-first (outbox position, then member IRI): alice's FEDERAL post is at
        // outbox position 0, bob's federation post at position 1, so FEDERAL precedes federation.
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-2", items[0]); // FEDERAL
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[1]); // federation

        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The page records the search query under the iris:searchQuery extension (default namespace).
        var queryTerm = $"{DefaultNamespace}searchQuery";
        var queryValue = doc.RootElement.GetProperty(queryTerm).GetString();
        Assert.Equal("fED", queryValue);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsAllItems()
    {
        // An absent/empty ?q matches all items (the feed, unfiltered): alice's 2 + bob's 2 = 4 items.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());

        // No query was supplied, so no iris:searchQuery extension is recorded.
        Assert.False(doc.RootElement.TryGetProperty($"{DefaultNamespace}searchQuery", out _));
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmptyCollection()
    {
        // "zzz" matches nothing: an empty OrderedCollection with totalItems 0.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=zzz&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
    }

    // --- Paging via ?limit / ?offset -----------------------------------------------

    [Fact]
    public async Task Search_Page2_IsOrderedCollectionPage_WithPrevAndNext()
    {
        // Empty query → 4 items. The feed is merged newest-first (outbox position, then member IRI);
        // each member's create-2 is at outbox position 0 and create-1 at position 1, so the order is
        // alice create-2, bob create-2, alice create-1, bob create-1. limit=2, offset=2 → page 2 holds
        // items 3 and 4 (alice create-1, bob create-1).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?limit=2&offset=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/search/?offset=2&limit=2",
            doc.RootElement.GetProperty("id").GetString());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[1]);

        Assert.Equal($"{_base}/ap/v1/c/{Community}/search", doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search/?offset=0&limit=2", doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _)); // page 2 of 2 is the last page
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Search_Page1_HasNextLink()
    {
        // 4 items, limit=2, offset=0 → page 1 holds items 1 and 2 (the two newest: alice create-2,
        // bob create-2 — both at outbox position 0), with a `next` to offset=2.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?limit=2&offset=0");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-2", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-2", items[1]);
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search/?offset=2&limit=2", doc.RootElement.GetProperty("next").GetString());
    }

    // --- Edge cases -----------------------------------------------------------------

    [Fact]
    public async Task Search_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody/search?q=anything");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_OffsetBeyondEnd_ReturnsEmptyPage()
    {
        // An offset past the last item yields an empty slice (totalItems still reflects the full count).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=&limit=2&offset=99");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement);
        Assert.Empty(items);
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- iris:capabilities on the community document -------------------------------

    [Fact]
    public async Task CommunityDocument_AdvertisesCapabilities()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var capabilitiesTerm = $"{DefaultNamespace}{ActivityPubServerConstants.CapabilitiesTerm}";
        Assert.True(doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities));
        Assert.Equal(JsonValueKind.Array, capabilities.ValueKind);

        var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(
            [
                ActivityPubServerConstants.CapabilityFeed,
                ActivityPubServerConstants.CapabilityMembers,
                ActivityPubServerConstants.CapabilitySearch,
            ],
            values);
    }

    [Fact]
    public async Task CommunityDocument_AdvertisesSearchCollectionLink()
    {
        // The community document carries a `search` extension link (alongside members/feed), pointing at
        // the /c/{name}/search specialized collection.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var searchLink = doc.RootElement.GetProperty("search").GetString();
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search", searchLink);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds: community <c>iris</c> with members alice (2 posts: GARDEN, FEDERAL) and bob (2 posts:
    /// federation, weather), plus a second member-less community <c>empty</c>, via the shared
    /// <see cref="TestSeeder"/>.
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var communityIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        TestSeeder.AddMember(persistence, communityIri, aliceIri);
        TestSeeder.AddMember(persistence, communityIri, bobIri);

        // alice: 2 posts, added oldest→newest (GARDEN create-1, FEDERAL create-2) so the outbox is
        // newest first (FEDERAL, GARDEN).
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-1", "a GARDEN post");
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-2", "a FEDERAL post");

        // bob: 2 posts, added oldest→newest (weather create-1, federation create-2) so the outbox is
        // newest first (federation, weather).
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-1", "about federation");
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-2", "the weather today");

        // A member-less community for completeness.
        TestSeeder.SeedCommunity(persistence, AHost, "empty");
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
        });
}
