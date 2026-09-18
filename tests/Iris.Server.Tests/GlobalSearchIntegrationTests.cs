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
[Collection(CollectionName)]
public sealed class GlobalSearchIntegrationTests : IDisposable
{
    /// <summary>The xunit collection that owns this class's shared host fixture (29.3: build the host once
    /// per collection instead of once per method). The methods here are read-only, so sharing the host's
    /// seeded persistence is safe.</summary>
    public const string CollectionName = "GlobalSearchIntegrationTests";

    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string DefaultNamespace = "https://iris.example/ns#";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly GlobalSearchSharedHost _fixture;
    private readonly string _base = $"https://{AHost}";

    public GlobalSearchIntegrationTests(GlobalSearchSharedHost fixture)
    {
        _fixture = fixture;
        _server = fixture.Server;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();

        // This class is read-only (the collection's seeded persistence is shared), so undo any test
        // mutations here to restore the baseline for the other (parallel) methods in the collection:
        // remove the carol actor and the alice/bob outbox + follow edges the counter tests add.
        var p = (InMemoryPersistenceProvider)_fixture.Persistence;
        var alice = AliceIri;
        var bob = new Iri($"https://{AHost}/ap/v1/u/{Bob}");
        var carol = new Iri($"https://{AHost}/ap/v1/u/carol");
        foreach (var actor in new[] { alice, bob, carol })
        {
            foreach (var item in p.Activities.GetOutboxAsync(actor).GetAwaiter().GetResult())
            {
                if (item is IObject { Id: { } id } && !string.IsNullOrEmpty(id))
                {
                    p.Activities.RemoveFromOutboxAsync(actor, new Iri(id)).GetAwaiter().GetResult();
                }
            }
        }

        var erin = new Iri($"https://{AHost}/ap/v1/u/erin");
        foreach (var pair in new List<(Iri Follower, Iri Target)>
        {
            (bob, alice), (erin, alice), (alice, bob), (bob, carol), (carol, bob),
        })
        {
            p.Follows.RemoveFollowAsync(pair.Follower, pair.Target).GetAwaiter().GetResult();
        }

        p.ActorStore.RemoveActorAsync(carol).GetAwaiter().GetResult();
    }

    private Iri AliceIri => new($"https://{AHost}/ap/v1/u/{Alice}");

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
        // Phase 99: with a query present, the collection base (the page-1 `id` and `first`) carries
        // the escaped query so a client walking `next` keeps the filter.
        Assert.Equal($"{_base}/ap/v1/search?q=ALIC", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal($"{_base}/ap/v1/search?q=ALIC", doc.RootElement.GetProperty("first").GetString());

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

    // --- Type filter: ?type=Actor returns only the directory (31.4) ----------------------

    [Fact]
    public async Task Search_TypeActor_ReturnsOnlyActors_NoContent()
    {
        // ?q=ALIC&type=Actor → only actor alice (the note whose content contains "alice" is excluded).
        // Without the type filter the same query returns alice (actor) + the note (content).
        var response = await _http.GetAsync($"{_base}/ap/v1/search?q=ALIC&type=Actor&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", Assert.Single(items));
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Search_EmptyQueryTypeActor_ReturnsAllActorsThenNoContent()
    {
        // Empty query + type=Actor → the whole directory (both actors, IRI-sorted), no content notes.
        // (The unfiltered empty query returns 4 items — the 2 actors + 2 notes.)
        var response = await _http.GetAsync($"{_base}/ap/v1/search?type=Actor&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/{Alice}",
                $"https://{AHost}/ap/v1/u/{Bob}",
            ],
            items);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
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

    [Fact]
    public async Task ClientSearchAsync_TypeActor_ReturnsOnlyActors()
    {
        // A client search with SearchOptions.Type = "Actor" appends ?type=Actor to the endpoint IRI;
        // the server restricts the result to actors, so the query "ALIC" yields only actor alice (the
        // matching content note is excluded).
        var client = new ActivityPubClient(
            new HttpClient(_server.CreateHandler(), disposeHandler: false),
            new ActorCache(),
            new Iris.Client.Collections.CollectionPageCache());

        var items = new List<string?>();
        await using (var enumerator = client.SearchAsync(new Iri($"{_base}/ap/v1"), "ALIC", new SearchOptions { Type = "Actor", Limit = 10 }).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                items.Add(enumerator.Current is IObject o ? o.Id : null);
            }
        }

        client.Dispose();
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", Assert.Single(items));
    }

    // --- Cached per-actor counters on search results and the actor document (F-13) ----------

    [Fact]
    public async Task Search_TypeActor_IncludesCachedCountersOnActorItems()
    {
        // Seed alice's outbox with two content posts and a follow (a non-content activity that must not
        // count), and two followers + one following. Counts are computed fresh per request (the actor
        // document is cached, but the search path is not), so this is a deterministic assertion.
        var p = (InMemoryPersistenceProvider)_fixture.Persistence;
        var bobIri = new Iri($"https://{AHost}/ap/v1/u/{Bob}");

        TestSeeder.AddCreateActivity(p, AliceIri, $"{AliceIri.Value}/activities/create-1", "a first post");
        TestSeeder.AddCreateActivity(p, AliceIri, $"{AliceIri.Value}/activities/create-2", "a second post");
        await p.Activities.AddToOutboxAsync(AliceIri, new Follow
        {
            Id = $"{AliceIri.Value}/activities/follow-1",
            Actor = [new Link { Href = new Uri(AliceIri.Value) }],
            Object = [new Link { Href = new Uri(bobIri.Value) }],
        });
        await p.Follows.RecordFollowAsync(bobIri, AliceIri);
        await p.Follows.RecordFollowAsync(new Iri($"https://{AHost}/ap/v1/u/erin"), AliceIri);
        await p.Follows.RecordFollowAsync(AliceIri, bobIri);

        var response = await _http.GetAsync($"{_base}/ap/v1/search?type=Actor&q=ALIC&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var actorItems = JsonDoc.GetItems(doc.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.GetString() == "Person")
            .ToList();
        var alice = Assert.Single(actorItems);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}", alice.GetProperty("id").GetString());

        // The Mastodon-style directory stats ride on the search result itself (iris: extension terms,
        // the default namespace) — no client-side counting of the outbox/follows collections.
        Assert.Equal(2, alice.GetProperty($"{DefaultNamespace}postsCount").GetInt32());
        Assert.Equal(2, alice.GetProperty($"{DefaultNamespace}followersCount").GetInt32());
        Assert.Equal(1, alice.GetProperty($"{DefaultNamespace}followingCount").GetInt32());
    }

    [Fact]
    public async Task ActorDocument_IncludesCachedCounters()
    {
        // Seed a fresh local actor (carol) with a single content post plus a follow (which must not
        // count) and one follower/following, then fetch the actor document directly — the counters must
        // be present on the document. carol is used (not alice/bob) so this is the first fetch of that
        // actor and the 60 s actor-document cache cannot return a stale, counter-less document from an
        // earlier method in the class.
        const string Carol = "carol";
        var p = (InMemoryPersistenceProvider)_fixture.Persistence;
        var carolIri = new Iri($"https://{AHost}/ap/v1/u/{Carol}");
        var bobIri = new Iri($"https://{AHost}/ap/v1/u/{Bob}");

        TestSeeder.SeedPerson(p, AHost, Carol);
        TestSeeder.AddCreateActivity(p, carolIri, $"{carolIri.Value}/activities/create-doc", "a doc post");
        await p.Activities.AddToOutboxAsync(carolIri, new Follow
        {
            Id = $"{carolIri.Value}/activities/follow-doc",
            Actor = [new Link { Href = new Uri(carolIri.Value) }],
            Object = [new Link { Href = new Uri(bobIri.Value) }],
        });
        await p.Follows.RecordFollowAsync(bobIri, carolIri);
        await p.Follows.RecordFollowAsync(carolIri, bobIri);

        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Carol}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal($"https://{AHost}/ap/v1/u/{Carol}", doc.RootElement.GetProperty("id").GetString());
        // Exactly one content post (the follow is excluded), one follower, one following.
        Assert.Equal(1, doc.RootElement.GetProperty($"{DefaultNamespace}postsCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty($"{DefaultNamespace}followersCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty($"{DefaultNamespace}followingCount").GetInt32());
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
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence) => Seed(persistence);

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
}

/// <summary>
/// The collection's shared host (29.3): seeds the persistence once and starts a single-instance
/// <c>TestServer</c> hosting the real search endpoint. Registered as the collection's
/// <see cref="SharedHostFixture"/> so it is constructed once per collection (not once per method).
/// </summary>
public sealed class GlobalSearchSharedHost : SharedHostFixture
{
    public GlobalSearchSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = BuildPersistence(),
        })
    {
    }

    private static InMemoryPersistenceProvider BuildPersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        GlobalSearchIntegrationTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// Collection definition for <see cref="GlobalSearchIntegrationTests"/>: a single shared host built once
/// per collection (29.3) so the class's read-only methods do not each rebuild the pipeline.
/// </summary>
[CollectionDefinition(GlobalSearchIntegrationTests.CollectionName)]
public sealed class GlobalSearchCollection : ICollectionFixture<GlobalSearchSharedHost>
{
}
