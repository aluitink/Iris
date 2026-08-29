using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 11 — closes the carried-forward **client/server page-1 interop gap** (Phase 5) with an
/// end-to-end proof: a real <see cref="ActivityPubClient"/> (built by the real
/// <see cref="ActivityPubClientFactory"/>, over a genuine HTTP stack via the test server's handler)
/// enumerates a **multi-page** collection served by a real in-process Iris server.
/// </summary>
/// <remarks>
/// The server serves page 1 as an <c>OrderedCollection</c> (the collection document itself, carrying
/// its first page of items + a self-<c>first</c>) and page N&gt;1 as an <c>OrderedCollectionPage</c>
/// (<c>prev</c>/<c>next</c>). The gap was that the client's <c>OrderedCollection</c>-as-page branch set
/// <c>NextPage = null</c>, so enumeration stopped at page 1 for any multi-page collection — the client
/// silently dropped every item past the first page. This test proves the full walk now works: page 1
/// (an <c>OrderedCollection</c>) → its <c>next</c> → page 2 (an <c>OrderedCollectionPage</c>) → the last
/// page, yielding every item across the boundary.
/// </remarks>
public sealed class ClientServerCollectionInteropTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";

    // The server's default collection page size (ActivityPubServerConstants.DefaultCollectionPageSize).
    private const int ServerDefaultPageSize = 20;

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _actorIri;
    private readonly Iri _outboxIri;
    private readonly IActivityPubClient _client;
    private readonly KeyPair _key;

    public ClientServerCollectionInteropTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);
        _key = seeded.Key;
        _actorIri = seeded.ActorIri;

        // 25 outbox items → 2 pages at the server's default page size of 20 (page 1 = 20 items served
        // as an OrderedCollection, page 2 = 5 items served as an OrderedCollectionPage). The client
        // does not send ?limit, so the page size is the server default; 25 forces the page-1 → page-2
        // boundary that the pre-fix bug broke.
        for (var i = 1; i <= 25; i++)
        {
            TestSeeder.AddCreateActivity(
                _persistence, _actorIri, $"https://{Host}/ap/v1/activities/{i}", $"note {i}");
        }

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = _persistence,
        });

        _outboxIri = _actorIri.OutboxOf();

        // A real signed client, routed to the in-process server over a genuine HTTP stack.
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_actorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        _client = factory.Create(
            new ActivityPubClientOptions { ActorId = _actorIri, EnableRetry = false },
            _server.CreateHandler());
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    // --- The page-1 (OrderedCollection) → page-2 (OrderedCollectionPage) walk ------------------

    [Fact]
    public async Task Client_Enumerates_MultiPageOutbox_YieldsAllItemsAcrossPage1Boundary()
    {
        // Pre-fix, the client's OrderedCollection-as-page branch set NextPage = null, so only the
        // first 20 items (page 1) were ever yielded. The walk must now reach page 2 and yield all 25.
        var items = new List<IObjectOrLink>();
        await foreach (var item in _client.GetCollectionItemsAsync(_outboxIri, null, CancellationToken.None))
        {
            items.Add(item);
        }

        Assert.Equal(25, items.Count);
    }

    [Fact]
    public async Task Client_GetCollectionAsync_FollowsNext_FromOrderedCollectionFirstPage()
    {
        // Enumerate pages (not flattened items). Page 1 is served as an OrderedCollection and must
        // yield a NextPage pointing at page 2 (the OrderedCollectionPage); page 2 is the last page.
        var pages = new List<Iris.Core.Collections.CollectionPage>();
        await foreach (var page in _client.GetCollectionAsync(_outboxIri, null, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);

        var page1 = pages[0];
        Assert.Equal(ServerDefaultPageSize, page1.Items.Count);
        Assert.False(page1.IsLastPage);
        Assert.NotNull(page1.NextPage);

        var page2 = pages[1];
        Assert.Equal(5, page2.Items.Count);
        Assert.True(page2.IsLastPage);
        Assert.Null(page2.NextPage);
        // Page 2's prev points back at the page-1 IRI. The server serves page 1 as the collection
        // document, whose canonical IRI carries a trailing slash, so compare the resolved page-1
        // IRI rather than the bare collection IRI.
        var expectedPrev = _outboxIri.Value + "/?page=1";
        var actualPrev = page2.PrevPage!.ToString();
        Assert.Equal(expectedPrev, actualPrev);
    }

    // --- Direct wire-format proof: page 1 carries a `next` when more pages remain -------------

    [Fact]
    public async Task Server_Page1_OrganizedAsOrderedCollection_CarriesNextWhenMorePagesRemain()
    {
        var http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        const int limit = 2;
        const int itemCount = 25; // the fixture seeds 25 outbox items

        // Derive the true page count from the item count so the "last page" assertion is exact
        // regardless of how many items the fixture holds.
        var pageCount = (itemCount + limit - 1) / limit; // 13

        // Page 1 (the collection IRI) must be an OrderedCollection with a `next` to page 2.
        using var page1 = await http.GetAsync($"{_outboxIri.Value}?limit={limit}");
        Assert.True(page1.IsSuccessStatusCode);
        var json1 = await page1.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"OrderedCollection\"", json1);
        Assert.Contains("\"next\":", json1);
        Assert.Contains("?page=2", json1);

        // Page 2 is an OrderedCollectionPage with a `next` to page 3 (it is not the last page).
        using var page2 = await http.GetAsync($"{_outboxIri.Value}?page=2&limit={limit}");
        Assert.True(page2.IsSuccessStatusCode);
        var json2 = await page2.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"OrderedCollectionPage\"", json2);
        Assert.Contains("?page=3", json2);

        // The true last page (pageCount) is an OrderedCollectionPage with no `next`.
        using var lastPage = await http.GetAsync($"{_outboxIri.Value}?page={pageCount}&limit={limit}");
        Assert.True(lastPage.IsSuccessStatusCode);
        var jsonLast = await lastPage.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"OrderedCollectionPage\"", jsonLast);
        Assert.DoesNotContain("\"next\":", jsonLast);
        // The last page's prev points at the second-to-last page.
        Assert.Contains($"?page={pageCount - 1}", jsonLast);
    }

    // --- The single-page (all items fit) path still works ------------------------------------

    [Fact]
    public async Task Client_Enumerates_SinglePageOutbox_YieldsAllItems()
    {
        // A fresh server with fewer than the default page size of items → a single page (an
        // OrderedCollection with a self first and no next). The client must yield every item and
        // mark the page as last.
        var persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(persistence, Host, "bob");
        for (var i = 1; i <= 3; i++)
        {
            TestSeeder.AddCreateActivity(
                persistence, seeded.ActorIri, $"https://{Host}/ap/v1/activities/b{i}", $"note b{i}");
        }

        using var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = "bob",
            Persistence = persistence,
        });

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(seeded.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(seeded.ActorIri, seeded.Key.KeyId);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, new HttpSignatureSigner(keyStore));
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = seeded.ActorIri, EnableRetry = false },
            server.CreateHandler());

        var items = new List<IObjectOrLink>();
        await foreach (var item in client.GetCollectionItemsAsync(seeded.ActorIri.OutboxOf(), null, CancellationToken.None))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);
    }
}
