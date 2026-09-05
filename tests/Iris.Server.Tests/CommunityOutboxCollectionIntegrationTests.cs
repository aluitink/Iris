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
/// Phase 19.5.1 integration test for the <strong>community outbox READ surface</strong>:
/// <c>GET /ap/v1/c/{name}/outbox</c>. The community document advertises an <c>outbox</c> IRI, and
/// <c>POST /ap/v1/c/{name}/outbox</c> (the community outbox publish endpoint) stores each community-authored
/// activity in the community's outbox (the activity store, keyed by the community IRI). This endpoint is
/// the READ counterpart: it serves those authored activities as a paged collection, keeping the advertised
/// <c>outbox</c> link honest (a remote client resolving the community's outbox link finds the community's
/// authored activities rather than a 404).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts a community <c>iris</c>. The test seeds the
/// community's outbox directly (the same <see cref="InMemoryActivityStore.AddToOutboxAsync"/> the publish
/// endpoint uses) and asserts the collection shape: page 1 is an <c>OrderedCollection</c> with the
/// newest-first items + <c>totalItems</c>; page 2 is an <c>OrderedCollectionPage</c> with
/// <c>prev</c>/<c>next</c>; an empty outbox serves an empty collection; <c>?refresh=true</c> emits a
/// <c>no-cache</c> <c>Cache-Control</c>; and an unknown community 404s. The write path (a signed
/// <c>Follow</c> recorded in the outbox) is covered by
/// <see cref="CommunityOutboxPublishIntegrationTests"/>; here the endpoint is exercised as a public read
/// surface (no federation required).
/// </remarks>
[Collection("CommunityOutboxCollection")]
public sealed class CommunityOutboxCollectionIntegrationTests : IAsyncLifetime
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";

    private readonly CommunityOutboxCollectionSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider Persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunityOutboxCollectionIntegrationTests(CommunityOutboxCollectionSharedHost fixture)
    {
        _fixture = fixture;
        Persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    // --- Page 1 is an OrderedCollection with the newest-first items ---------------

    [Fact]
    public async Task Outbox_Page1_IsOrderedCollection_WithNewestFirstItems()
    {
        var communityIri = SeedCommunity();
        SeedOutbox(communityIri, count: 5);

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/outbox?limit=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/outbox", doc.RootElement.GetProperty("id").GetString());

        // Items are newest-first: with limit=2 the page holds the two most recent (-5, -4).
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.EndsWith("-5", items[0]);
        Assert.EndsWith("-4", items[1]);

        // totalItems reflects the full outbox size, not the page size.
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The response carries the collection Cache-Control header.
        Assert.Equal(
            ActivityPubServerConstants.CollectionCacheControl,
            response.Headers.CacheControl?.ToString());
    }

    // --- Page 2 is an OrderedCollectionPage with prev/next ------------------------

    [Fact]
    public async Task Outbox_Page2_IsOrderedCollectionPage_WithPrevAndNext()
    {
        var communityIri = SeedCommunity();
        SeedOutbox(communityIri, count: 5);

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/outbox?limit=2&page=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/c/{Community}/outbox/?page=2", doc.RootElement.GetProperty("id").GetString());

        // partOf points back at the collection; prev points to page 1; next points to page 3.
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/outbox",
            doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/outbox/?page=1",
            doc.RootElement.GetProperty("prev").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/outbox/?page=3",
            doc.RootElement.GetProperty("next").GetString());

        // Page 2 holds items 3 and 4 (1-based), newest-first within the page.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.EndsWith("-3", items[0]);
        Assert.EndsWith("-2", items[1]);
    }

    // --- An empty outbox serves an empty collection -------------------------------

    [Fact]
    public async Task Outbox_Empty_ServesEmptyCollection()
    {
        SeedCommunity();

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/outbox");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- ?refresh=true bypasses the cache -----------------------------------------

    [Fact]
    public async Task Outbox_RefreshTrue_BypassesCache()
    {
        var communityIri = SeedCommunity();
        SeedOutbox(communityIri, count: 5);

        // Prime the cache with a non-refresh read.
        var first = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/outbox?limit=2");
        first.EnsureSuccessStatusCode();
        Assert.Equal(
            ActivityPubServerConstants.CollectionCacheControl,
            first.Headers.CacheControl?.ToString());

        // A ?refresh=true read re-renders and emits no-cache (the value was just re-rendered;
        // intermediates must not serve a stale copy).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/outbox?limit=2&refresh=true");
        response.EnsureSuccessStatusCode();
        Assert.Equal(
            ActivityPubServerConstants.NoCacheCacheControl,
            response.Headers.CacheControl?.ToString());
    }

    // --- Unknown community → 404 --------------------------------------------------

    [Fact]
    public async Task Outbox_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody/outbox");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds the community <c>iris</c> (a <see cref="Group"/>) on this instance and returns its IRI.
    /// </summary>
    private Iri SeedCommunity()
    {
        var communityIri = TestSeeder.SeedCommunity(Persistence, AHost, Community);
        return communityIri;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> activities into <paramref name="communityIri"/>'s outbox, added
    /// oldest→newest so that <see cref="InMemoryActivityStore.AddToOutboxAsync"/> (which inserts at index
    /// 0) leaves the list newest-first: -count, …, -1.
    /// </summary>
    private void SeedOutbox(Iri communityIri, int count)
    {
        var outbox = (InMemoryActivityStore)Persistence.Activities;
        for (var i = 1; i <= count; i++)
        {
            var activity = new Follow
            {
                Id = $"{communityIri.Value}/activities/follow-{i}",
                Actor = [new Link { Href = new Uri(communityIri.Value) }],
                Object = [new Link { Href = new Uri($"https://remote{i}.example/ap/v1/u/actor{i}") }],
            };
            outbox.AddToOutboxAsync(communityIri, activity).GetAwaiter().GetResult();
        }
    }

}

/// <summary>
/// Shared-host fixture for <see cref="CommunityOutboxCollectionIntegrationTests"/> (single instance,
/// a.domain.local, empty persistence — the test methods seed their own community + outbox). Built once
/// per xunit collection; the test class resets before each method for isolation.
/// </summary>
public sealed class CommunityOutboxCollectionSharedHost : SharedHostFixture
{
    public CommunityOutboxCollectionSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = new InMemoryPersistenceProvider(),
        })
    {
    }
}

/// <summary>
/// xunit collection definition for the community-outbox-collection shared-host fixture.
/// </summary>
[CollectionDefinition("CommunityOutboxCollection")]
public sealed class CommunityOutboxCollectionCollection : ICollectionFixture<CommunityOutboxCollectionSharedHost>
{
}
