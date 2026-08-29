using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.11 integration test (F-13 global search / directory): the
/// <c>GET /ap/v1/search</c> endpoint searches an instance's <em>own</em> local actors (the directory)
/// and stored content objects case-insensitively, paged via the shared <c>limit</c>/<c>offset</c> shape.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts two local persons (alice, bob) — the directory —
/// and seeded content objects (notes stored via the object store). The test asserts: a query matches
/// both actors (by <c>name</c>/<c>preferredUsername</c>/IRI) and content objects (by
/// <c>content</c>/<c>name</c>) case-insensitively, actors coming before content; an empty query lists
/// everything (actors + content); a no-match query returns an empty collection; <c>?limit</c>/<c>?offset</c>
/// paging works (page 1 <c>OrderedCollection</c> with <c>next</c>, page 2 <c>OrderedCollectionPage</c> with
/// <c>prev</c> and no <c>next</c> when it is the last page); and the client's
/// <see cref="IActivityPubClient.SearchAsync"/> round-trips against the live endpoint (fetching a single
/// page and yielding its items).
/// </remarks>
public sealed class GlobalSearchIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string DefaultNamespace = "https://iris.example/ns#";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";

    public GlobalSearchIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);
        _server = StartServer(_persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- Search matches actors AND content case-insensitively -------------------------

    [Fact]
    public async Task Search_MatchesActorsAndContent_CaseInsensitive()
    {
        // "ALIC" matches actor alice (preferredUsername/name/IRI) and the note whose content contains
        // "alice". Bob and the "GARDEN" note do not match.
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=ALIC&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/search", doc.RootElement.GetProperty("id").GetString());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        // Actors first (IRI-sorted), then content objects (IRI-sorted): alice (actor), then the note.
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/{Alice}",
                $"https://{AHost}/ap/v1/u/{Alice}/notes/n-alice",
            ],
            items);

        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The page records the search query under the iris:searchQuery extension (default namespace).
        Assert.Equal("ALIC", doc.RootElement.GetProperty($"{DefaultNamespace}searchQuery").GetString());
    }

    [Fact]
    public async Task Search_MatchesActorByPreferredUsername()
    {
        // "bob" matches actor bob's preferredUsername (and name), but no content object.
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=bob&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}", Assert.Single(items));
    }

    [Fact]
    public async Task Search_MatchesContentByName()
    {
        // "GARDEN" (case-insensitive) matches the note's `name` field ("My Garden"), not its content
        // ("a garden post"), and no actor.
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=garden&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-garden", Assert.Single(items));
    }

    // --- Empty query lists everything (the directory + content) ------------------------

    [Fact]
    public async Task Search_EmptyQuery_ReturnsAllActorsThenContent()
    {
        // No ?q → all 2 actors (alice, bob) + 2 content notes, actors first, each IRI-sorted.
        var response = await _http.GetAsync($"{_base}/ap/v1/search?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}", items[1]);
        // Content notes, IRI-sorted (n-alice < n-garden).
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-alice", items[2]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-garden", items[3]);

        // No query was supplied, so no iris:searchQuery extension is recorded.
        Assert.False(doc.RootElement.TryGetProperty($"{DefaultNamespace}searchQuery", out _));
    }

    // --- No match returns an empty collection -----------------------------------------

    [Fact]
    public async Task Search_NoMatch_ReturnsEmptyCollection()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=zzz&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
    }

    // --- Paging via ?limit / ?offset ---------------------------------------------------

    [Fact]
    public async Task Search_Page2_IsOrderedCollectionPage_WithPrevAndNoNext()
    {
        // 4 items (empty query), limit=2, offset=2 → page 2 holds items 3 and 4 (the two notes).
        var response = await _http.GetAsync($"{_base}/ap/v1/search?limit=2&offset=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/search/?offset=2&limit=2",
            doc.RootElement.GetProperty("id").GetString());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-alice", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-garden", items[1]);

        Assert.Equal($"{_base}/ap/v1/search", doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal($"{_base}/ap/v1/search/?offset=0&limit=2", doc.RootElement.GetProperty("prev").GetString());
        Assert.False(doc.RootElement.TryGetProperty("next", out _)); // page 2 of 2 is the last page
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Search_Page1_HasNextLink()
    {
        // 4 items, limit=2, offset=0 → page 1 holds the two actors, with a `next` to offset=2.
        var response = await _http.GetAsync($"{_base}/ap/v1/search?limit=2&offset=0");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}", items[1]);
        Assert.Equal($"{_base}/ap/v1/search/?offset=2&limit=2", doc.RootElement.GetProperty("next").GetString());
    }

    [Fact]
    public async Task Search_OffsetBeyondEnd_ReturnsEmptyPage()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=&limit=2&offset=99");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- Client SearchAsync round-trips against the live endpoint ----------------------

    [Fact]
    public async Task ClientSearchAsync_RoundTrips_AgainstLiveEndpoint()
    {
        // A real client (an HttpClientHandler transport routes the in-process TestServer by host)
        // searches the instance's /ap/v1/search endpoint and yields the matching items (a single page).
        var client = new ActivityPubClient(
            new HttpClient(_server.CreateHandler(), disposeHandler: false),
            new ActorCache(),
            new Iris.Client.Collections.CollectionPageCache());

        var items = new List<string?>();
        await using (var enumerator = client.SearchAsync(new Iri($"{_base}/ap/v1"), "garden", new SearchOptions { Limit = 10 }).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                items.Add(enumerator.Current is IObject o ? o.Id : null);
            }
        }

        client.Dispose();
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/notes/n-garden", Assert.Single(items));
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Seeds: two local persons (alice, bob — the directory) and two content notes stored in the object
    /// store (an "alice" note and a "My Garden" note with content "a garden post"), via the shared
    /// <see cref="TestSeeder"/> for the actors and a direct <see cref="IObjectStore"/> put for the notes
    /// (a stored note is what the content pass searches; a seeded <see cref="Create"/> activity would
    /// only live in the activity store, not the object store, unless delivered through the full inbox
    /// pipeline).
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        TestSeeder.SeedPerson(persistence, AHost, Alice);
        TestSeeder.SeedPerson(persistence, AHost, Bob);

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"https://{AHost}/ap/v1/u/{Alice}/notes/n-alice",
            Name = ["alice's note"],
            Content = ["a note by alice"],
        }).GetAwaiter().GetResult();

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"https://{AHost}/ap/v1/u/{Alice}/notes/n-garden",
            Name = ["My Garden"],
            Content = ["a garden post"],
        }).GetAwaiter().GetResult();
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
        });
}
