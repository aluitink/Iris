using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
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

        var items = GetItems(doc.RootElement).Select(ItemId).ToArray();
        Assert.Equal(2, items.Length);
        // Feed order: alice's posts precede bob's; within a member, newest first.
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

        var items = GetItems(doc.RootElement).Select(ItemId).ToArray();
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
        // Empty query → 4 items (feed order: alice create-2, alice create-1, bob create-2, bob create-1).
        // limit=2, offset=2 → page 2 holds items 3 and 4 (bob create-2, bob create-1).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?limit=2&offset=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/search/?offset=2&limit=2",
            doc.RootElement.GetProperty("id").GetString());

        var items = GetItems(doc.RootElement).Select(ItemId).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-2", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[1]);

        Assert.Equal($"{_base}/ap/v1/c/{Community}/search", doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search/?offset=0&limit=2", doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _)); // page 2 of 2 is the last page
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Search_Page1_HasNextLink()
    {
        // 4 items, limit=2, offset=0 → page 1 holds items 1 and 2, with a `next` to offset=2.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?limit=2&offset=0");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = GetItems(doc.RootElement).Select(ItemId).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-2", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[1]);
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

        var items = GetItems(doc.RootElement);
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

    private static List<JsonElement> GetItems(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items))
        {
            return [];
        }

        return items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()]
            : [items];
    }

    private static string ItemId(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : element.GetProperty("id").GetString()!;

    /// <summary>
    /// Seeds: community <c>iris</c> with members alice (2 posts: GARDEN, FEDERAL) and bob (2 posts:
    /// federation, weather), plus a second member-less community <c>empty</c>.
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var baseUrl = _Base();
        var communityIri = new Iri($"{baseUrl}/ap/v1/c/{Community}");

        persistence.Communities.PutCommunityAsync(new Group
        {
            Id = communityIri.Value,
            PreferredUsername = Community,
            Name = [Community],
        }).GetAwaiter().GetResult();

        var aliceIri = new Iri($"{baseUrl}/ap/v1/u/{Alice}");
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = aliceIri.Value,
            PreferredUsername = Alice,
            Name = [Alice],
        }).GetAwaiter().GetResult();

        var bobIri = new Iri($"{baseUrl}/ap/v1/u/{Bob}");
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = bobIri.Value,
            PreferredUsername = Bob,
            Name = [Bob],
        }).GetAwaiter().GetResult();

        persistence.Communities.AddMemberAsync(communityIri, aliceIri).GetAwaiter().GetResult();
        persistence.Communities.AddMemberAsync(communityIri, bobIri).GetAwaiter().GetResult();

        // alice: 2 posts, added oldest→newest (GARDEN create-1, FEDERAL create-2) so the outbox is
        // newest first (FEDERAL, GARDEN).
        persistence.Activities.AddToOutboxAsync(aliceIri, new Create
        {
            Id = $"{aliceIri.Value}/activities/create-1",
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note { Id = $"{aliceIri.Value}/objects/note-1", Content = ["a GARDEN post"] }],
        }).GetAwaiter().GetResult();
        persistence.Activities.AddToOutboxAsync(aliceIri, new Create
        {
            Id = $"{aliceIri.Value}/activities/create-2",
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note { Id = $"{aliceIri.Value}/objects/note-2", Content = ["a FEDERAL post"] }],
        }).GetAwaiter().GetResult();

        // bob: 2 posts, added oldest→newest (weather create-1, federation create-2) so the outbox is
        // newest first (federation, weather).
        persistence.Activities.AddToOutboxAsync(bobIri, new Create
        {
            Id = $"{bobIri.Value}/activities/create-1",
            Actor = [new Link { Href = new Uri(bobIri.Value) }],
            Object = [new Note { Id = $"{bobIri.Value}/objects/note-1", Content = ["about federation"] }],
        }).GetAwaiter().GetResult();
        persistence.Activities.AddToOutboxAsync(bobIri, new Create
        {
            Id = $"{bobIri.Value}/activities/create-2",
            Actor = [new Link { Href = new Uri(bobIri.Value) }],
            Object = [new Note { Id = $"{bobIri.Value}/objects/note-2", Content = ["the weather today"] }],
        }).GetAwaiter().GetResult();

        // A member-less community for completeness.
        var emptyIri = new Iri($"{baseUrl}/ap/v1/c/empty");
        persistence.Communities.PutCommunityAsync(new Group
        {
            Id = emptyIri.Value,
            PreferredUsername = "empty",
            Name = ["empty"],
        }).GetAwaiter().GetResult();
    }

    private static string _Base() => $"https://{AHost}";

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
