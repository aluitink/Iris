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
/// Phase 5 integration test for the <strong>community feed</strong> slice: <see cref="ICommunityFeedService"/>
/// (the unified feed = the union of the community's local members' outbox activities, newest first) and
/// the <c>GET /ap/v1/c/{name}/feed</c> endpoint (the feed served as a paged collection).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts a community <c>iris</c> with two local members
/// (alice, bob). Each member has a small outbox (posted activities). The test asserts the feed is the
/// union of the members' outboxes in member order (alice's newest posts, then bob's newest posts), that
/// a member with no outbox contributes nothing, that an unknown community 404s, that an empty
/// community's feed is an empty collection, and that paging works (page 1 <c>OrderedCollection</c> +
/// page 2 <c>OrderedCollectionPage</c> with <c>prev</c>/<c>next</c>). The "followed community content"
/// half (remote content the community follows) is the separate community-following slice.
/// </remarks>
public sealed class CommunityFeedIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly string _base = $"https://{AHost}";

    public CommunityFeedIntegrationTests()
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

    // --- The feed is the union of the members' outboxes, in member order -----------

    [Fact]
    public async Task Feed_Page1_IsUnionOfMemberOutboxes_InMemberOrder()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed", doc.RootElement.GetProperty("id").GetString());

        // Members are grouped in actor-IRI order: alice (.../u/alice) sorts before bob (.../u/bob).
        // alice has 3 posts (newest first: create-3, create-2, create-1); bob has 2 (create-2,
        // create-1). The feed is alice's outbox then bob's outbox: 5 items total.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(5, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-3", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-2", items[1]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[2]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-2", items[3]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[4]);

        // totalItems reflects the full feed size, not the page size.
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_MemberWithNoOutbox_ContributesNothing()
    {
        // carol is a member but has no posts. The feed is bob's 2 + alice's 3 = 5 items; no item is
        // from carol. This proves a member with an empty outbox contributes nothing (and does not
        // break the feed).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(5, items.Length);
        Assert.DoesNotContain(items, id => id.Contains($"/ap/v1/u/carol/"));
    }

    // --- Paging -------------------------------------------------------------------

    [Fact]
    public async Task Feed_Page2_IsOrderedCollectionPage_WithPrevAndNext()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=2&page=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/feed/?page=2",
            doc.RootElement.GetProperty("id").GetString());

        // Page 2 holds items 3 and 4 of the feed (alice's oldest post, then bob's newest).
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-2", items[1]);

        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed", doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed/?page=1", doc.RootElement.GetProperty("prev").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed/?page=3", doc.RootElement.GetProperty("next").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_LastPage_HasNoNextLink()
    {
        // 5 items, limit 2 → 3 pages. Page 3 holds the final (5th) item only.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=2&page=3");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Page 3 holds the final (5th) item only: bob's oldest post (create-1).
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[0]);

        Assert.Equal($"{_base}/ap/v1/c/{Community}/feed/?page=2", doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _));
    }

    // --- Edge cases -----------------------------------------------------------------

    [Fact]
    public async Task Feed_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody/feed");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_CommunityWithNoMembers_ReturnsEmptyCollection()
    {
        // The seeded member-less community <c>empty</c> has an empty feed: an empty OrderedCollection.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/empty/feed");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());

        // An empty feed renders an empty `items` array (and a self-referencing `first`).
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("first", out _));
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds: community <c>iris</c> with members alice (3 posts) and bob (no posts), plus a second
    /// member-less community <c>empty</c> for the empty-feed case, via the shared <see cref="TestSeeder"/>.
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var communityIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        TestSeeder.AddMember(persistence, communityIri, aliceIri);
        TestSeeder.AddMember(persistence, communityIri, bobIri);

        // alice: 3 posts, added oldest→newest (create-1, create-2, create-3) so the outbox is newest
        // first (create-3, create-2, create-1).
        for (var i = 1; i <= 3; i++)
        {
            TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-{i}", $"alice note {i}");
        }

        // bob: 2 posts, added oldest→newest so the outbox is newest first (create-2, create-1).
        for (var i = 1; i <= 2; i++)
        {
            TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-{i}", $"bob note {i}");
        }

        // carol: a member with no posts (empty outbox) — must contribute nothing to the feed.
        var carolIri = TestSeeder.SeedPerson(persistence, AHost, "carol");
        TestSeeder.AddMember(persistence, communityIri, carolIri);

        // A member-less community for the empty-feed case.
        TestSeeder.SeedCommunity(persistence, AHost, "empty");
    }

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
