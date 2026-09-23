using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

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
/// <c>iris:capabilities</c> = <c>[feed, members, search, mute]</c> under the default namespace (19.0b.2b
/// adds the local-moderation <c>mute</c> capability — a community can mute a member via a non-AP
/// <c>/local/v1</c> write).
/// </remarks>
[Collection("CommunitySearch")]
public sealed class CommunitySearchIntegrationTests : IAsyncLifetime
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string DefaultNamespace = "https://iris.example/ns#";

    private readonly CommunitySearchSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunitySearchIntegrationTests(CommunitySearchSharedHost fixture)
    {
        _fixture = fixture;
        _persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_persistence);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
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
        // Phase 99: with a query present, the collection base (the page-1 `id` and `first`) carries
        // the escaped query so a client walking `next` keeps the filter.
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=fED", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=fED", doc.RootElement.GetProperty("first").GetString());

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

    // --- Page links preserve the query (Phase 99: client next-walking keeps the filter) ---

    [Fact]
    public async Task Search_Page1_WithQuery_NextAndFirstCarryTheQuery()
    {
        // "a" matches every item's content (alice "a GARDEN post", alice "a FEDERAL post",
        // bob "about federation", bob "the weather today" — each contains the substring "a"),
        // so all 4 items match. limit=2 → page 1 of 2. The `first` self-link and the `next`
        // link must both carry ?q=a so a client that walks `next` (the ActivityPub client's
        // GetCollectionAsync, driven by the PagedCollection component) keeps the filter on
        // page 2 instead of dropping it and returning unfiltered results.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=a&limit=2&offset=0");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        // The collection base (the `first` target and the page-1 `id`) carries the query.
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a", doc.RootElement.GetProperty("first").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a", doc.RootElement.GetProperty("id").GetString());
        // The `next` link carries the query + the offset/limit, so following it keeps the filter.
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a&offset=2&limit=2", doc.RootElement.GetProperty("next").GetString());
    }

    [Fact]
    public async Task Search_Page2_WithQuery_PrevAndIdCarryTheQuery()
    {
        // "a" matches all 4 items; limit=2, offset=2 → page 2 of 2 (the last page). The `id`,
        // `partOf`, and `prev` links must all carry ?q=a (the last page has no `next`).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=a&limit=2&offset=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a&offset=2&limit=2", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a", doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q=a&offset=0&limit=2", doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _)); // last page
    }

    [Fact]
    public async Task Search_WithQuery_PagedNextWalkStillFilters()
    {
        // End-to-end paging walk: fetch page 1 (?q=a&limit=2), then follow its `next` link
        // verbatim. The page-2 response must be filtered by the same query (the `next` link
        // carried ?q=a), not reset to the unfiltered feed. This is the exact sequence the
        // ActivityPub client's GetCollectionAsync performs when a PagedCollection is pointed
        // at {community}/search?q=a.
        var page1 = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=a&limit=2&offset=0");
        page1.EnsureSuccessStatusCode();
        using (var doc1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync()))
        {
            var nextIri = doc1.RootElement.GetProperty("next").GetString();
            Assert.NotNull(nextIri);

            // Follow the emitted `next` link verbatim.
            var page2 = await _http.GetAsync(nextIri!);
            page2.EnsureSuccessStatusCode();
            using var doc2 = JsonDocument.Parse(await page2.Content.ReadAsStringAsync());

            // Page 2 is the second page of the SAME filtered result set (2 of the 4 "a" matches),
            // not the unfiltered feed. totalItems still reflects the full match count.
            Assert.Equal("OrderedCollectionPage", doc2.RootElement.GetProperty("type").GetString());
            Assert.Equal(2, JsonDoc.GetItems(doc2.RootElement).Count());
            Assert.Equal(4, doc2.RootElement.GetProperty("totalItems").GetInt32());
        }
    }

    [Fact]
    public async Task Search_WithSpacesInQuery_QueryIsPercentEscapedInLinks_AndUnescapedInExtension()
    {
        // A query containing a space ("garden post") must be percent-escaped in the emitted page
        // links so they are valid IRIs, while the iris:searchQuery extension records the original
        // un-escaped query (the server un-escapes ?q when it arrives). "garden post" matches only
        // alice's "a GARDEN post" (1 item), so limit=10 is a single page (no `next`).
        var escaped = Uri.EscapeDataString("garden post"); // "garden%20post"
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q={escaped}&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The collection base (the `first` target and the page-1 `id`) carries the ESCAPED query,
        // so the emitted IRI is well-formed (a literal space would make it invalid).
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q={escaped}", doc.RootElement.GetProperty("first").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search?q={escaped}", doc.RootElement.GetProperty("id").GetString());

        // The page is filtered to the single match.
        Assert.Single(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The iris:searchQuery extension records the UN-ESCAPED original query (the server
        // percent-un-escaped ?q before storing/matching).
        var queryValue = doc.RootElement.GetProperty($"{DefaultNamespace}searchQuery").GetString();
        Assert.Equal("garden post", queryValue);
    }

    // --- S53: search matches content nested in the Lemmy relay envelope --------------

    [Fact]
    public async Task Search_MatchesContent_NestedInAnnouncedCreate()
    {
        // S53 repro: a member's outbox carries an Announce of an embedded Create of a Note (the
        // Lemmy relay envelope, 138.20). The community feed renders the post (the backfill unwraps
        // the envelope), but the community search's in-memory match only looks one level into the
        // activity (the Create), never the Note — so the post is invisible to search (0 results).
        // "quantum" occurs only in the nested note's content.
        var communityIri = TestSeeder.SeedCommunity(_persistence, AHost, Community);
        var carolIri = TestSeeder.SeedPerson(_persistence, AHost, "carol");
        TestSeeder.AddMember(_persistence, communityIri, carolIri);
        TestSeeder.AddAnnouncedCreateActivity(
            _persistence, carolIri, $"{carolIri.Value}/activities/announce-1",
            "notes on quantum coherence", new[] { communityIri });

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/search?q=quantum&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal($"https://{AHost}/ap/v1/u/carol/activities/announce-1", items[0]);
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
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
                // 19.0b.2b: a community can mute a member (a non-AP local write under /local/v1).
                ActivityPubServerConstants.CapabilityMute,
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

        // The search collection link is an Iris extension, namespaced under the iris: namespace.
        var searchLink = doc.RootElement
            .GetProperty(DefaultNamespace + CollectionExtensionNames.Search)
            .GetString();
        Assert.Equal($"{_base}/ap/v1/c/{Community}/search", searchLink);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds: community <c>iris</c> with members alice (2 posts: GARDEN, FEDERAL) and bob (2 posts:
    /// federation, weather), plus a second member-less community <c>empty</c>, via the shared
    /// <see cref="TestSeeder"/>.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var communityIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        TestSeeder.AddMember(persistence, communityIri, aliceIri);
        TestSeeder.AddMember(persistence, communityIri, bobIri);

        // alice: 2 posts, added oldest→newest (GARDEN create-1, FEDERAL create-2) so the outbox is
        // newest first (FEDERAL, GARDEN).
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-1", "a GARDEN post", new[] { communityIri });
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-2", "a FEDERAL post", new[] { communityIri });

        // bob: 2 posts, added oldest→newest (weather create-1, federation create-2) so the outbox is
        // newest first (federation, weather).
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-1", "about federation", new[] { communityIri });
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-2", "the weather today", new[] { communityIri });

        // A member-less community for completeness.
        TestSeeder.SeedCommunity(persistence, AHost, "empty");
    }
}

/// <summary>
/// Shared-host fixture for <see cref="CommunitySearchIntegrationTests"/> (single instance,
/// a.domain.local). Built once per xunit collection; the test class resets + reseeds before
/// each method for isolation.
/// </summary>
public sealed class CommunitySearchSharedHost : SharedHostFixture
{
    public CommunitySearchSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = CreatePersistence(),
        })
    {
    }

    private static InMemoryPersistenceProvider CreatePersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        CommunitySearchIntegrationTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// xunit collection definition for the community-search shared-host fixture.
/// </summary>
[CollectionDefinition("CommunitySearch")]
public sealed class CommunitySearchCollection : ICollectionFixture<CommunitySearchSharedHost>
{
}
