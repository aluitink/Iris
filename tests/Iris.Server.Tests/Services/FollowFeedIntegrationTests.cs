using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Collections;
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
    private readonly IActivityPubClient _signedClient;
    private readonly Iri _alice;
    private readonly Iri _carol;
    private readonly Iri _bob;
    private readonly KeyPair _carolKey;

    public FollowFeedIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var (aKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        var (bKey, bobIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        var (carolKey, carolIri, _) = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Carol);
        _alice = aliceIri;
        _bob = bobIri;
        _carol = carolIri;
        _carolKey = carolKey;

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
        // IActorDocumentFetcher it resolves) to route outbound fetches to the correct in-process
        // TestServer. The routing fetcher reaches A (for alice's key resolution via the signature
        // validator) and B (for bob's outbox fetch via the feed service).
        TestServer? aServerRef = null;
        var aHandlerFactory = () => aServerRef!.CreateHandler();
        var bHandlerFactory = () => _b.CreateHandler();
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aKey);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(_alice, new Iri($"{_alice.Value}#key-1"));
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var clientFactory = new ActivityPubClientFactory(aKeyStore, aKeyProvider, aSigner);

        var a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                // The client signs as alice and reaches B over the wire.
                var bClient = clientFactory.Create(
                    new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
                    _b.CreateHandler());

                // A routing fetcher: resolves actor docs from the correct instance by host.
                var routingFetcher = new FollowFeedRoutingFetcher(
                    AHost, aHandlerFactory,
                    BHost, bHandlerFactory,
                    aKey, _alice);
                s.AddSingleton<IActorDocumentFetcher>(routingFetcher);

                s.AddSingleton<IFollowFeedService>(sp => new FeedService(
                    sp.GetRequiredService<IPersistenceProvider>(),
                    sp.GetRequiredService<ILocalActorResolver>(),
                    sp.GetRequiredService<IActorDocumentFetcher>(),
                    bClient,
                    sp.GetRequiredService<IOptions<FeedOptions>>()));
            },
        });
        _a = a;
        aServerRef = _a;

        _http = new HttpClient(_a.CreateHandler(), disposeHandler: false);

        // A client (signed as alice) that reaches A's endpoint, for the GetFollowFeedAsync round-trip.
        _client = clientFactory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            _a.CreateHandler());

        // A signed client for the owner-gated follow-feed endpoint (139.2-s5c).
        _signedClient = clientFactory.Create(
            new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
            _a.CreateHandler());
    }

    public void Dispose()
    {
        _client.Dispose();
        _signedClient.Dispose();
        _http.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The merged feed includes local + remote follows ----------------------------

    [Fact]
    public async Task Feed_MergesLocalAndRemoteFollows()
    {
        var collection = (OrderedCollection?)await SignedGetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10");
        Assert.NotNull(collection);

        // The merged feed: carol's 1 (local) + bob's 2 (remote, walked newest-first: b-2, b-1) = 3.
        var items = CollectionPageFactory.ResolveCollectionItems(collection!)
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToArray();
        Assert.Equal(3, items.Length);
        Assert.Contains($"{_carol.Value}/activities/c-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);
    }

    [Fact]
    public async Task Feed_ActorWithNoFollows_ReturnsEmptyCollection()
    {
        // dave is a local actor on A who follows no one → an empty OrderedCollection.
        // (139.2-s5c: the follow feed is owner-gated; dave's feed requires a signed request as dave,
        // so this test now verifies the 403 for a non-owner.)
        var dave = TestSeeder.SeedPerson(_aPersistence, AHost, "dave");
        _ = dave;

        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/dave/feed?limit=10");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
        var collection = (OrderedCollection?)await SignedGetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=BOB&limit=10");
        Assert.NotNull(collection);
        var items = CollectionPageFactory.ResolveCollectionItems(collection!)
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);
        Assert.DoesNotContain($"{_carol.Value}/activities/c-1", items);
    }

    [Fact]
    public async Task Feed_Query_MatchesCarolContent()
    {
        var collection = (OrderedCollection?)await SignedGetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=carol&limit=10");
        Assert.NotNull(collection);
        var items = CollectionPageFactory.ResolveCollectionItems(collection!)
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToArray();
        Assert.Single(items);
        Assert.Equal($"{_carol.Value}/activities/c-1", items[0]);
    }

    [Fact]
    public async Task Feed_Query_NoMatch_ReturnsEmptyCollection()
    {
        var collection = (OrderedCollection?)await SignedGetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?q=zzz&limit=10");
        Assert.NotNull(collection);
        var items = CollectionPageFactory.ResolveCollectionItems(collection!);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Feed_EmptyQuery_ReturnsUnfilteredFeed()
    {
        var collection = (OrderedCollection?)await SignedGetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10");
        Assert.NotNull(collection);
        var items = CollectionPageFactory.ResolveCollectionItems(collection!)
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToArray();
        Assert.Equal(3, items.Length);
    }

    // --- Owner-gating (139.2-s5c) --------------------------------------------------------

    [Fact]
    public async Task Feed_NonOwner_SignedAsOtherActor_Returns403()
    {
        // carol is signed in and tries to fetch alice's feed — must be denied (403).
        var carolKeyStore = new InMemoryKeyStore();
        carolKeyStore.PutKey(_carolKey);
        var carolKeyProvider = new InMemoryKeyProvider(carolKeyStore);
        carolKeyProvider.RegisterKey(_carol, _carolKey.KeyId);
        var carolSigner = new HttpSignatureSigner(carolKeyStore);
        var carolClient = new ActivityPubClientFactory(carolKeyStore, carolKeyProvider, carolSigner).Create(
            new ActivityPubClientOptions { ActorId = _carol, EnableRetry = false },
            _a.CreateHandler());

        using (carolClient)
        {
            var response = await carolClient.GetObjectAsync(new Iri($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10"));
            Assert.Null(response);
        }
    }

    [Fact]
    public async Task Feed_Anonymous_Returns403()
    {
        // An unsigned request to the follow feed is denied (403) — the feed is owner-only.
        var response = await _http.GetAsync($"{_aBase()}/ap/v1/u/{Alice}/feed?limit=10");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Helpers --------------------------------------------------------------------

    private async Task<IObject?> SignedGetAsync(string url)
        => await _signedClient.GetObjectAsync(new Iri(url)).ConfigureAwait(false);

    private string _aBase() => $"https://{AHost}";
}

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
/// actor IRI's host.
/// </summary>
file sealed class FollowFeedRoutingFetcher : IActorDocumentFetcher
{
    private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

    public FollowFeedRoutingFetcher(
        string aHost, Func<HttpMessageHandler> aHandlerFactory,
        string bHost, Func<HttpMessageHandler> bHandlerFactory,
        KeyPair signingKey, Iri signingActor)
    {
        _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
        {
            [aHost] = BuildFetcherFor(aHandlerFactory, signingKey, signingActor),
            [bHost] = BuildFetcherFor(bHandlerFactory, signingKey, signingActor),
        };
    }

    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        var host = new Uri(actorIri.Value).Host;
        if (_fetchers.TryGetValue(host, out var fetcher))
        {
            return fetcher.GetActorAsync(actorIri, ct);
        }

        return Task.FromResult<Actor?>(null);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
