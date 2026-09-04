using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Services;

/// <summary>
/// F-14 integration test (the followed feed / home timeline): a local actor (alice on instance A) who
/// follows both a local actor (carol on A) and a remote actor (bob on instance B). The endpoint
/// <c>GET /ap/v1/u/alice/feed</c> on A merges carol's outbox (read from A's activity store, no network)
/// with bob's outbox (fetched over the wire from B) into one de-duplicated, capped feed. The client's
/// <see cref="IActivityPubClient.GetFollowFeedAsync"/> round-trips the same feed.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c> + local follower <c>carol</c>) and instance B
/// (b.domain.local, remote followed actor <c>bob</c>). A's <see cref="IFollowFeedService"/> is overridden
/// (via <c>ExtraServices</c>) so its outbound outbox-fetch client routes to B's in-process
/// <c>TestServer</c> (the production registration hardcodes a real <c>HttpClientHandler</c>, which cannot
/// reach an in-process <c>TestServer</c>). bob's outbox on B is seeded with two posts; carol's outbox on A
/// with one. The merged feed must contain all three (two from remote B, one from local A).
/// </remarks>
public sealed class FollowFeedIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Carol = "carol";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _http;
    private readonly IActivityPubClient _client;
    private readonly Iri _alice;
    private readonly Iri _carol;
    private readonly Iri _bob;

    public FollowFeedIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var (aKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        var (bKey, bobIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _alice = aliceIri;
        _bob = bobIri;
        _carol = TestSeeder.SeedPerson(_aPersistence, AHost, Carol);

        // alice follows carol (local) and bob (remote). The follow edges live on A (alice's home).
        _aPersistence.Follows.RecordFollowAsync(_alice, _carol).GetAwaiter().GetResult();
        _aPersistence.Follows.RecordFollowAsync(_alice, _bob).GetAwaiter().GetResult();

        // carol's outbox (on A): one post. bob's outbox (on B): two posts.
        TestSeeder.AddCreateActivity(_aPersistence, _carol, $"{_carol.Value}/activities/c-1", "carol 1");
        TestSeeder.AddCreateActivity(_bPersistence, _bob, $"{_bob.Value}/activities/b-1", "bob 1");
        TestSeeder.AddCreateActivity(_bPersistence, _bob, $"{_bob.Value}/activities/b-2", "bob 2");

        // B hosts bob; its outbox is a local collection endpoint (no outbound fetches needed).
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
        });

        // A hosts alice + carol. The production IFollowFeedService registration hardcodes a real
        // HttpClientHandler (which cannot reach an in-process TestServer), so we override it (and the
        // IActorDocumentFetcher it resolves) to route outbound fetches to B's in-process TestServer.
        // alice's key is registered with a B-wired client factory so the outbox fetch is signed as
        // alice (B's signature-validation middleware accepts it).
        var bHandler = _b.CreateHandler();
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aKey);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(_alice, new Iri($"{_alice.Value}#key-1"));
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var bWiredClientFactory = new ActivityPubClientFactory(aKeyStore, aKeyProvider, aSigner);

        var a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                // The client signs as alice and reaches B over the wire.
                var bClient = bWiredClientFactory.Create(
                    new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
                    bHandler);

                // The document fetcher resolves bob's public key (and bob's outbox link) from B.
                // It must be registered (not just captured) so the FeedService factory resolves it
                // from DI (the production registration would otherwise be used).
                s.AddSingleton<IActorDocumentFetcher>(sp => new IrisActorDocumentFetcher(
                    bClient,
                    sp.GetRequiredService<RemoteActorCache>()));

                s.AddSingleton<IFollowFeedService>(sp => new FeedService(
                    sp.GetRequiredService<IPersistenceProvider>(),
                    sp.GetRequiredService<ILocalActorResolver>(),
                    sp.GetRequiredService<IActorDocumentFetcher>(),
                    bClient,
                    sp.GetRequiredService<IOptions<FeedOptions>>()));
            },
        });
        _a = a;

        _http = new HttpClient(_a.CreateHandler(), disposeHandler: false);

        // A client (signed as alice) that reaches A's endpoint, for the GetFollowFeedAsync round-trip.
        _client = bWiredClientFactory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            _a.CreateHandler());
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The merged feed includes local + remote follows ----------------------------

    [Fact]
    public async Task Feed_MergesLocalAndRemoteFollows()
    {
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());

        // The merged feed: carol's 1 (local) + bob's 2 (remote, walked newest-first: b-2, b-1) = 3.
        // IRI order across follows: carol (.../u/carol) sorts after bob (b.host), so bob's items come
        // first, then carol's. The assertion is on membership (the merge), not strict cross-follow order.
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(3, items.Length);
        Assert.Contains($"{_carol.Value}/activities/c-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);

        Assert.Equal(3, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_ActorWithNoFollows_ReturnsEmptyCollection()
    {
        // dave is a local actor on A who follows no one → an empty OrderedCollection.
        var dave = TestSeeder.SeedPerson(_aPersistence, AHost, "dave");
        _ = dave;

        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/dave/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_UnknownActor_Returns404()
    {
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/nobody/feed");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ActorDocument_AdvertisesFeedExtension()
    {
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(
            doc.RootElement.TryGetProperty(
                IrisDocumentExtensions.DefaultNamespaceIri + CollectionExtensionNames.Feed, out var feed),
            "actor doc should advertise a feed extension");
        Assert.Equal($"{_alice.Value}/feed", feed.GetString());
    }

    // --- Client round-trip ----------------------------------------------------------

    [Fact]
    public async Task Client_GetFollowFeedAsync_RoundTrips()
    {
        var items = new List<string>();
        await foreach (var item in _client.GetFollowFeedAsync(_alice, new CollectionQuery { Limit = 10 }))
        {
            items.Add(item switch
            {
                IObject { Id: { } id } => id,
                ILink { Href: { } href } => href.ToString(),
                _ => throw new InvalidOperationException("unexpected feed item"),
            });
        }

        // The client reads the same merged feed the endpoint serves: 3 items (carol + bob x2).
        Assert.Equal(3, items.Count);
        Assert.Contains($"{_carol.Value}/activities/c-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);
    }

    // --- ?q content filter (21.4.2) --------------------------------------------------

    [Fact]
    public async Task Feed_Query_MatchesContent_CaseInsensitive()
    {
        // ?q=BOB matches bob's 2 posts (content "bob 1", "bob 2" — case-insensitive) but not carol's
        // post ("carol 1"). The content lives on the nested Note (the Create's Object), so the filter
        // must match the referenced object's content, not just the activity's own content.
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=BOB&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);
        Assert.DoesNotContain($"{_carol.Value}/activities/c-1", items);

        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_Query_MatchesCarolContent()
    {
        // ?q=carol matches carol's 1 post (content "carol 1") but not bob's 2 posts.
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=carol&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal($"{_carol.Value}/activities/c-1", items[0]);

        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_Query_NoMatch_ReturnsEmptyCollection()
    {
        // ?q=zzz matches nothing: an empty OrderedCollection with totalItems 0.
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=zzz&limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Feed_EmptyQuery_ReturnsUnfilteredFeed()
    {
        // An absent/empty ?q returns the full unfiltered feed: carol's 1 + bob's 2 = 3 items.
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(3, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- Helpers --------------------------------------------------------------------

    private string _aBase() => $"https://{AHost}";
}
