using System.Linq;
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
/// Phase 4 integration test for the <strong>paged local collection</strong> slice: the
/// <c>GET /ap/v1/u/{handle}/outbox</c>, <c>/followers</c>, and <c>/following</c> endpoints serve an actor's
/// collections as paged <c>OrderedCollection</c>/<c>OrderedCollectionPage</c> documents over the wire,
/// paged via <c>?page</c>/<c>?limit</c> and served through the <see cref="LocalCollectionPageCache"/>.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local, alice) hosts the real collection endpoints. The test reads
/// them over the in-process HTTP stack (a plain <c>HttpClient</c> over <c>TestServer</c>'s handler) — no
/// signing needed, since these are public read endpoints. Outbox items are seeded newest-first; the test
/// asserts ordering, pagination slicing, the <c>next</c>/<c>prev</c> links, the <c>totalItems</c> count,
/// the <c>Cache-Control</c> header, and the <c>?refresh=true</c> bypass.
/// </remarks>
public sealed class CollectionEndpointIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly string _base = $"https://{AHost}";

    public CollectionEndpointIntegrationTests()
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

    // --- Page 1 is an OrderedCollection with its first page of items --------------

    [Fact]
    public async Task Outbox_Page1_IsOrderedCollection_WithFirstPageOfItems()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Page 1 is the collection document itself.
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/u/{Alice}/outbox", doc.RootElement.GetProperty("id").GetString());

        // Items are newest-first; with limit=2 the outbox (seeded newest-first) yields the two most
        // recent. Outbox items are full activity objects, so read their `id`.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.EndsWith("-5", items[0]);
        Assert.EndsWith("-4", items[1]);

        // totalItems reflects the full collection size, not the page size.
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
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2&page=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox/?page=2",
            doc.RootElement.GetProperty("id").GetString());

        // partOf points back at the collection (serialized as its IRI string).
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox",
            doc.RootElement.GetProperty("partOf").GetString());

        // Page 2 holds items 3 and 4 (1-based), newest-first within the page.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.EndsWith("-3", items[0]);
        Assert.EndsWith("-2", items[1]);

        // prev points to page 1, next to page 3 (each serialized as its IRI string).
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox/?page=1",
            doc.RootElement.GetProperty("prev").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox/?page=3",
            doc.RootElement.GetProperty("next").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- The last page carries no next link --------------------------------------

    [Fact]
    public async Task Outbox_LastPage_HasNoNextLink()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2&page=3");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());

        // Page 3 holds the final (5th) item only.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.EndsWith("-1", items[0]);

        // prev points to page 2; there is no next (this is the last page).
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox/?page=2",
            doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _));
    }

    // --- ?limit caps the page size and ?page out-of-range clamps -------------------

    [Fact]
    public async Task Outbox_OutOfRangePage_ClampsToLastPage()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2&page=99");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Clamped to the last page (page 3).
        Assert.Equal(
            $"{_base}/ap/v1/u/{Alice}/outbox/?page=3",
            doc.RootElement.GetProperty("id").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
    }

    // --- followers / following are served as collections --------------------------

    [Fact]
    public async Task Followers_ServesFollowersAsLinks()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/followers");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/bob", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/carol", items[1]);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Following_ServesFollowingAsLinks()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/following");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal($"https://{AHost}/ap/v1/u/dave", items[0]);
    }

    // --- ?refresh=true bypasses the read -----------------------------------------

    [Fact]
    public async Task Outbox_RefreshTrue_BypassesCache()
    {
        // First read (a miss) renders and caches page 1.
        _ = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2");
        // The ?refresh=true read bypasses the cache for the read (the document is re-rendered), though
        // the content is stable. Assert it still succeeds and is a valid collection.
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}/outbox?limit=2&refresh=true");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- Unknown handle → 404 ----------------------------------------------------

    [Fact]
    public async Task Outbox_UnknownHandle_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/nobody/outbox");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds the persistence provider: an actor (alice) with a 5-item outbox (newest-first) and follow
    /// edges (bob, carol → alice; alice → dave).
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var actorIriString = $"{_Base()}/ap/v1/u/{Alice}";
        var actorIri = new Iri(actorIriString);
        var outbox = (InMemoryActivityStore)persistence.Activities;

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = Alice,
            Name = [Alice],
        };
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        // Outbox items, added oldest→newest so that AddToOutboxAsync (which inserts at index 0) leaves
        // the list newest-first: -5, -4, -3, -2, -1.
        for (var i = 1; i <= 5; i++)
        {
            var create = new Create
            {
                Id = $"{actorIriString}/activities/create-{i}",
                Actor = [new Link { Href = new Uri(actorIriString) }],
                Object = [new Note { Id = $"{actorIriString}/objects/note-{i}", Content = [$"note {i}"] }],
            };
            outbox.AddToOutboxAsync(actorIri, create).GetAwaiter().GetResult();
        }

        // Follow edges: bob and carol follow alice; alice follows dave.
        persistence.Follows.RecordFollowAsync(new Iri($"https://{AHost}/ap/v1/u/bob"), actorIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(new Iri($"https://{AHost}/ap/v1/u/carol"), actorIri).GetAwaiter().GetResult();
        persistence.Follows.RecordFollowAsync(actorIri, new Iri($"https://{AHost}/ap/v1/u/dave")).GetAwaiter().GetResult();
    }

    private static string _Base() => $"https://{AHost}";

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> hosting the real collection endpoints.
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
