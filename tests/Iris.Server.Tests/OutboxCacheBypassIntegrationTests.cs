using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.6.6 — <strong>Cache behavior at the boundary</strong>: cached reads (the local collection
/// endpoints, served through the <see cref="LocalCollectionPageCache"/>) expose a <c>?refresh=true</c>
/// bypass, and a <em>new</em> activity becomes visible after a bypass (the UI's refresh path actually
/// re-fetches) — no stale-forever behavior. This pins the exact 19.6.6 scenario the adjacent tests cover
/// only in pieces: a plain read of a just-cached page serves the stale document (a brand-new activity
/// added to the outbox is not yet visible), a <c>?refresh=true</c> read makes the new activity visible
/// (and emits <c>no-cache</c>), and a subsequent plain read now sees it (the bypass wrote the fresh entry
/// back, so nothing is stuck stale forever).
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local, alice) hosting the real collection endpoints, read over
/// the in-process HTTP stack (no signing — public read endpoints). The outbox is seeded with one item;
/// the page-1 read (cache key <c>{actor}/outbox</c>) is primed; a second item is then added to the
/// outbox. A plain read within the fresh TTL serves the stale page (the new item absent); a
/// <c>?refresh=true</c> read re-renders from the live store (the new item present, <c>no-cache</c>
/// emitted) and writes the fresh entry back, so the next plain read is fresh.
/// </remarks>
public sealed class OutboxCacheBypassIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";
    private string _outboxUrl => $"{_base}/ap/v1/u/{Alice}/outbox";

    public OutboxCacheBypassIntegrationTests()
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

    // --- A new outbox activity is stale on a plain read, visible after a ?refresh=true bypass ----

    [Fact]
    public async Task NewOutboxActivity_IsStaleOnPlainRead_VisibleAfterRefreshBypass()
    {
        var firstCreateIri = $"{ActorIriString()}/activities/create-1";
        var newCreateIri = $"{ActorIriString()}/activities/create-2";

        // 1) Prime the page-1 cache with a plain read (a miss → renders and caches a page holding only
        //    the first item). This is the "cached read" the boundary is about.
        using (var prime = await _http.GetAsync($"{_outboxUrl}?limit=10"))
        {
            prime.EnsureSuccessStatusCode();
            var primeIds = JsonDoc.ItemIdsOf(await prime.Content.ReadAsStringAsync());
            Assert.Contains(firstCreateIri, primeIds);
            Assert.DoesNotContain(newCreateIri, primeIds);
        }

        // 2) Add a NEW activity to the outbox (it lands in the store, but the cached page is stale).
        await AddCreateAsync(newCreateIri);

        // 3) A PLAIN read within the fresh TTL still serves the STALE cached page: the new activity is
        //    NOT yet visible, and the response is the normal cacheable collection (not no-cache).
        using (var stale = await _http.GetAsync($"{_outboxUrl}?limit=10"))
        {
            stale.EnsureSuccessStatusCode();
            Assert.Equal(
                ActivityPubServerConstants.CollectionCacheControl,
                stale.Headers.CacheControl?.ToString());
            var staleIds = JsonDoc.ItemIdsOf(await stale.Content.ReadAsStringAsync());
            Assert.Contains(firstCreateIri, staleIds);
            Assert.False(
                staleIds.Contains(newCreateIri),
                "a plain read within the fresh TTL must serve the stale cached page (the new activity is not yet visible)");
        }

        // 4) A ?refresh=true read bypasses the cache: it re-renders from the live store, so the new
        //    activity IS visible, and the response emits no-cache (the value was just re-rendered).
        using (var fresh = await _http.GetAsync($"{_outboxUrl}?limit=10&refresh=true"))
        {
            fresh.EnsureSuccessStatusCode();
            Assert.Equal(
                ActivityPubServerConstants.NoCacheCacheControl,
                fresh.Headers.CacheControl?.ToString());
            var freshIds = JsonDoc.ItemIdsOf(await fresh.Content.ReadAsStringAsync());
            Assert.True(
                freshIds.Contains(newCreateIri),
                "a ?refresh=true read must make the new outbox activity visible (the UI refresh path re-fetches)");
            Assert.Contains(firstCreateIri, freshIds);
        }

        // 5) The bypass wrote the fresh entry back, so a subsequent PLAIN read now sees the new activity
        //    — no stale-forever behavior.
        using (var after = await _http.GetAsync($"{_outboxUrl}?limit=10"))
        {
            after.EnsureSuccessStatusCode();
            var afterIds = JsonDoc.ItemIdsOf(await after.Content.ReadAsStringAsync());
            Assert.True(
                afterIds.Contains(newCreateIri),
                "after the ?refresh=true bypass wrote the fresh entry back, a plain read must see the new activity (no stale-forever)");
        }
    }

    // --- The same boundary holds for the actor document (cached public read) --------------------

    [Fact]
    public async Task ActorDoc_PlainReadServesCached_RefreshBypassRefetches()
    {
        // Prime the public actor-document cache with a plain read (a miss → renders and caches).
        using (var prime = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}"))
        {
            prime.EnsureSuccessStatusCode();
            Assert.Equal(
                ActivityPubServerConstants.ActorCacheControl,
                prime.Headers.CacheControl?.ToString());
        }

        // A ?refresh=true read bypasses the cache (re-reads the actor from the store) and emits
        // no-cache — the boundary's bypass mechanism on the actor-document read.
        using (var fresh = await _http.GetAsync($"{_base}/ap/v1/u/{Alice}?refresh=true"))
        {
            fresh.EnsureSuccessStatusCode();
            Assert.Equal(
                ActivityPubServerConstants.NoCacheCacheControl,
                fresh.Headers.CacheControl?.ToString());
            using var doc = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
            Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static string ActorIriString() => $"{_Base()}/ap/v1/u/{Alice}";

    private static string _Base() => $"https://{AHost}";

    /// <summary>
    /// Adds a <see cref="Create"/> (with the given IRI) to alice's outbox in the store — a new activity
    /// that a just-cached outbox page does not yet reflect.
    /// </summary>
    private Task AddCreateAsync(string createIri)
    {
        var outbox = (InMemoryActivityStore)_persistence.Activities;
        var create = new Create
        {
            Id = createIri,
            Actor = [new Link { Href = new Uri(ActorIriString()) }],
            Object = [new Note { Id = $"{createIri}/note", Content = ["a new post"] }],
        };
        return outbox.AddToOutboxAsync(new Iri(ActorIriString()), create);
    }

    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var actorIri = new Iri(ActorIriString());
        var actor = new Person
        {
            Id = ActorIriString(),
            PreferredUsername = Alice,
            Name = [Alice],
        };
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        // One initial outbox item (create-1); the test adds create-2 to exercise the stale→bypass path.
        var outbox = (InMemoryActivityStore)persistence.Activities;
        var create = new Create
        {
            Id = $"{actorIri}/activities/create-1",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note { Id = $"{actorIri}/objects/note-1", Content = ["an initial post"] }],
        };
        outbox.AddToOutboxAsync(actorIri, create).GetAwaiter().GetResult();
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
        });
}
