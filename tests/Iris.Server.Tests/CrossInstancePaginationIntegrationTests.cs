using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.15: pagination and backfill consistency for federated community content. Verifies that
/// cross-instance timeline paging boundaries (first/next/previous pages) are correct, that historical
/// backfill after peering includes the expected post window with stable ordering, and that cache
/// bypass/reload does not lose older remote objects or create duplicate entries.
/// </summary>
/// <remarks>
/// Topology: A (page-a.domain.local, actor <c>alice</c>) hosts a multi-page outbox; B
/// (page-b.domain.local, community <c>lumen</c>) peers with A (follows alice) and merges alice's
/// remote outbox into its community feed. B's <see cref="FeedOptions"/> is configured with
/// <c>PagesPerActor = 3</c> (so the feed walks up to 3 of alice's 20-item outbox pages = 60 items)
/// and <c>MaxItems = 60</c>. The community feed is served as a paged collection
/// (<c>GET /ap/v1/c/{name}/feed</c>), so the feed's own page boundaries (25 items per page by default)
/// can be verified independently of the remote outbox walk.
/// </remarks>
[Collection("CrossInstancePagination")]
public sealed class CrossInstancePaginationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "page-a.domain.local";
    internal const string BHost = "page-b.domain.local";
    internal const string Alice = "alice";
    internal const string Lumen = "lumen";
    internal const int AlicePostCount = 50;
    internal const int RemotePageSize = 20;
    internal const int FeedPageSize = 25;

    internal static readonly Iri AliceIri = new($"https://{AHost}/ap/v1/u/{Alice}");
    internal static readonly Iri AliceOutboxIri = new($"https://{AHost}/ap/v1/u/{Alice}/outbox");
    internal static readonly Iri LumenIri = new($"https://{BHost}/ap/v1/c/{Lumen}");
    internal static readonly Iri LumenInboxIri = new($"https://{BHost}/ap/v1/c/{Lumen}/inbox");

    private readonly CrossInstancePaginationSharedHost _fixture;
    private readonly ITestOutputHelper _output;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly IActivityPubClient _signedClient;

    public CrossInstancePaginationIntegrationTests(
        CrossInstancePaginationSharedHost fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aHttp = new HttpClient(fixture.ServerA.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
        _signedClient = fixture.SignedClient;
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    // --- Cross-instance timeline paging boundaries ----------------------------------------

    [Fact]
    public async Task RemoteOutbox_PageBoundaries_FirstNextLast()
    {
        // Page 1: OrderedCollection with `first` pointing to the first page.
        var page1 = await _aHttp.GetAsync($"{AliceOutboxIri.Value}?limit={RemotePageSize}");
        page1.EnsureSuccessStatusCode();
        var page1Doc = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var root1 = page1Doc.RootElement;
        Assert.Equal("OrderedCollection", root1.GetProperty("type").GetString());
        Assert.True(root1.TryGetProperty("first", out var firstProp));
        Assert.Equal(AliceOutboxIri.Value, firstProp.GetString());
        Assert.True(root1.TryGetProperty("next", out var nextProp));
        Assert.Equal($"{AliceOutboxIri.Value}/?page=2", nextProp.GetString());
        var items1 = GetItemIds(root1);
        Assert.Equal(RemotePageSize, items1.Count);
        // Page 1 is newest-first: the most recently added activity (post-49) is first.
        Assert.Equal(MakeActivityId(49), items1[0]);
        // The oldest on page 1 is post-30.
        Assert.Equal(MakeActivityId(30), items1[RemotePageSize - 1]);

        // Page 2: OrderedCollectionPage with `prev`, `next`, `partOf`.
        var page2 = await _aHttp.GetAsync($"{AliceOutboxIri.Value}?page=2&limit={RemotePageSize}");
        page2.EnsureSuccessStatusCode();
        var page2Doc = JsonDocument.Parse(await page2.Content.ReadAsStringAsync());
        var root2 = page2Doc.RootElement;
        Assert.Equal("OrderedCollectionPage", root2.GetProperty("type").GetString());
        Assert.Equal(RemotePageSize + 1, root2.GetProperty("startIndex").GetInt32());
        Assert.True(root2.TryGetProperty("prev", out var prevProp));
        Assert.Equal($"{AliceOutboxIri.Value}/?page=1", prevProp.GetString());
        Assert.True(root2.TryGetProperty("next", out var nextProp2));
        Assert.Equal($"{AliceOutboxIri.Value}/?page=3", nextProp2.GetString());
        Assert.True(root2.TryGetProperty("partOf", out var partOfProp));
        Assert.Equal(AliceOutboxIri.Value, partOfProp.GetString());
        var items2 = GetItemIds(root2);
        Assert.Equal(RemotePageSize, items2.Count);
        Assert.Equal(MakeActivityId(29), items2[0]);
        Assert.Equal(MakeActivityId(10), items2[RemotePageSize - 1]);

        // Page 3: the last page — no `next`, 10 items.
        var page3 = await _aHttp.GetAsync($"{AliceOutboxIri.Value}?page=3&limit={RemotePageSize}");
        page3.EnsureSuccessStatusCode();
        var page3Doc = JsonDocument.Parse(await page3.Content.ReadAsStringAsync());
        var root3 = page3Doc.RootElement;
        Assert.Equal("OrderedCollectionPage", root3.GetProperty("type").GetString());
        Assert.False(root3.TryGetProperty("next", out _), "last page must not have a `next` link");
        Assert.True(root3.TryGetProperty("prev", out _));
        var items3 = GetItemIds(root3);
        Assert.Equal(AlicePostCount % RemotePageSize, items3.Count);
        Assert.Equal(MakeActivityId(9), items3[0]);
        Assert.Equal(MakeActivityId(0), items3[^1]);
    }

    // --- Backfill window: 3 pages × 20 items = 60 items, oldest-first window -------------

    [Fact]
    public async Task BackfillWindow_ThreePagesOfTwenty_SixtyItems()
    {
        // B's community feed merges alice's remote outbox (3 pages × 20 = 60 items, since alice has
        // exactly 50 posts, the walk yields all 50). The feed is served as a paged collection with
        // FeedPageSize (25) items per page.
        var feedPage1 = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/c/{Lumen}/feed?limit={FeedPageSize}&refresh=true");
        feedPage1.EnsureSuccessStatusCode();
        var feedPage1Doc = JsonDocument.Parse(await feedPage1.Content.ReadAsStringAsync());
        var feedRoot1 = feedPage1Doc.RootElement;
        Assert.Equal("OrderedCollection", feedRoot1.GetProperty("type").GetString());
        var feedItems1 = GetItemIds(feedRoot1);
        Assert.Equal(FeedPageSize, feedItems1.Count);

        // The feed is newest-first: alice's most recent post (post-49) is first.
        Assert.Equal(MakeActivityId(49), feedItems1[0]);
        Assert.Equal(MakeActivityId(25), feedItems1[^1]);

        var feedPage2 = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/c/{Lumen}/feed?page=2&limit={FeedPageSize}&refresh=true");
        feedPage2.EnsureSuccessStatusCode();
        var feedPage2Doc = JsonDocument.Parse(await feedPage2.Content.ReadAsStringAsync());
        var feedRoot2 = feedPage2Doc.RootElement;
        Assert.Equal("OrderedCollectionPage", feedRoot2.GetProperty("type").GetString());
        var feedItems2 = GetItemIds(feedRoot2);
        Assert.Equal(FeedPageSize, feedItems2.Count);
        Assert.Equal(MakeActivityId(24), feedItems2[0]);
        Assert.Equal(MakeActivityId(0), feedItems2[^1]);

        // All 50 items are present, in stable newest-first order, with no duplicates.
        var allItems = feedItems1.Concat(feedItems2).ToList();
        Assert.Equal(AlicePostCount, allItems.Count);
        Assert.Equal(AlicePostCount, allItems.Distinct().Count());
        // Stable ordering: strictly decreasing by post number.
        for (var i = 1; i < allItems.Count; i++)
        {
            var prevNum = int.Parse(allItems[i - 1].Substring("post-".Length));
            var currNum = int.Parse(allItems[i].Substring("post-".Length));
            Assert.True(prevNum > currNum,
                $"Expected strictly decreasing order: {allItems[i - 1]} before {allItems[i]}");
        }
    }

    // --- Stable ordering across repeated reads --------------------------------------------

    [Fact]
    public async Task FeedOrder_Stable_AcrossRepeatedReads()
    {
        var read1 = await GetFeedItemIdsAsync(refresh: true);
        var read2 = await GetFeedItemIdsAsync(refresh: true);
        var read3 = await GetFeedItemIdsAsync(refresh: false);

        Assert.Equal(read1, read2);
        Assert.Equal(read1, read3);
        Assert.Equal(AlicePostCount, read1.Count);
    }

    // --- Helpers --------------------------------------------------------------------------

    private string MakeActivityId(int index) => $"post-{index}";

    private void SeedForFixture()
    {
        TestSeeder.SeedPersonWithExistingKey(
            _aPersistence, AHost, Alice, new Iri($"{AliceIri.Value}#key-1"));

        TestSeeder.SeedCommunityWithExistingKey(
            _bPersistence, BHost, Lumen, new Iri($"{LumenIri.Value}#key-1"));

        // Add bob as a member of lumen (so the community has at least one member).
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/bob");
        _bPersistence.Communities.AddMemberAsync(LumenIri, bobIri).GetAwaiter().GetResult();

        // Record the peering edge: lumen follows alice (the community feed's peering branch
        // merges alice's outbox into the feed).
        _bPersistence.Communities.AddFollowAsync(LumenIri, AliceIri).GetAwaiter().GetResult();

        // Seed alice's outbox with 50 posts (oldest first: post-0, post-1, ..., post-49).
        // The outbox serves newest-first, so post-49 is first and post-0 is last.
        for (var i = 0; i < AlicePostCount; i++)
        {
            TestSeeder.AddCreateActivity(_aPersistence, AliceIri, MakeActivityId(i), $"content {i}");
        }
    }

    private async Task<List<string>> GetFeedItemIdsAsync(bool refresh)
    {
        var url = $"https://{BHost}/ap/v1/c/{Lumen}/feed?limit={FeedPageSize}";
        var allItems = new List<string>();
        var page = 1;

        while (true)
        {
            var pageUrl = page == 1
                ? $"{url}{(refresh ? "&refresh=true" : string.Empty)}"
                : $"{url}&page={page}{(refresh ? "&refresh=true" : string.Empty)}";

            var response = await _bHttp.GetAsync(pageUrl);
            response.EnsureSuccessStatusCode();
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            allItems.AddRange(GetItemIds(root));

            if (!root.TryGetProperty("next", out _))
            {
                break;
            }

            page++;
        }

        return allItems;
    }

    private static List<string> GetItemIds(JsonElement root)
    {
        var items = new List<string>();
        // Prefer `orderedItems` (the canonical AS2.0 OrderedCollectionPage form, 139.1 F-7); fall
        // back to `items` (the non-ordered CollectionPage property).
        var itemsProp = root.TryGetProperty("orderedItems", out var ordered)
            ? ordered
            : root.TryGetProperty("items", out var plain) ? plain : default;
        if (itemsProp.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in itemsProp.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null)
                {
                    // Extract the short id (e.g. "post-49") from the full IRI
                    // (e.g. "https://page-a.domain.local/ap/v1/u/alice/creates/post-49").
                    var lastSlash = id.LastIndexOf('/');
                    items.Add(lastSlash >= 0 ? id[(lastSlash + 1)..] : id);
                }
            }
        }

        return items;
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="CrossInstancePaginationIntegrationTests"/>.
/// A: page-a.domain.local (alice, 50-post outbox), B: page-b.domain.local (lumen community,
/// FeedOptions.PagesPerActor=3, MaxItems=60). B's fetcher routes to A (for signature validation +
/// remote outbox fetch), B's client routes to A (for the community feed's remote outbox walk).
/// </summary>
public sealed class CrossInstancePaginationSharedHost : SharedTwoHostFixture
{
    /// <summary>A signed <see cref="IActivityPubClient"/> that signs as alice (A) and delivers to B.</summary>
    public IActivityPubClient SignedClient { get; }

    public CrossInstancePaginationSharedHost()
        : base(BuildOptions())
    {
        var keyStore = (InMemoryKeyStore)ServerA.Services.GetRequiredService<IKeyStore>();
        var keyProvider = (InMemoryKeyProvider)ServerA.Services.GetRequiredService<IKeyProvider>();
        var signer = ServerA.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        SignedClient = factory.Create(
            new ActivityPubClientOptions
            {
                ActorId = CrossInstancePaginationIntegrationTests.AliceIri,
                EnableRetry = false,
            },
            new LazyHandler(() => ServerB.CreateHandler()));
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(
            aPersistence,
            CrossInstancePaginationIntegrationTests.AHost,
            CrossInstancePaginationIntegrationTests.Alice);
        var lumen = TestSeeder.SeedCommunityWithKey(
            bPersistence,
            CrossInstancePaginationIntegrationTests.BHost,
            CrossInstancePaginationIntegrationTests.Lumen);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstancePaginationIntegrationTests.AHost,
            Handle = CrossInstancePaginationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, alice.ActorIri, alice.KeyId),
            Fetcher = new PaginationRoutingFetcher(
                CrossInstancePaginationIntegrationTests.AHost, () => serverARef().CreateHandler(),
                CrossInstancePaginationIntegrationTests.BHost, () => serverBRef().CreateHandler(),
                alice.Key, alice.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstancePaginationIntegrationTests.BHost,
            Handle = CrossInstancePaginationIntegrationTests.Lumen,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(lumen.Key, lumen.CommunityIri, lumen.KeyId),
            Fetcher = new PaginationRoutingFetcher(
                CrossInstancePaginationIntegrationTests.AHost, () => serverARef().CreateHandler(),
                CrossInstancePaginationIntegrationTests.BHost, () => serverBRef().CreateHandler(),
                lumen.Key, lumen.CommunityIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            PreServices = s =>
            {
                var feedOpts = new FeedOptions { PagesPerActor = 3, MaxItems = 60 };
                s.AddSingleton(feedOpts);
                s.AddSingleton<IOptions<FeedOptions>>(
                    new Microsoft.Extensions.Options.OptionsWrapper<FeedOptions>(feedOpts));
                var instanceActorIri = new Iri($"https://{CrossInstancePaginationIntegrationTests.BHost}/ap/v1/u/{CrossInstancePaginationIntegrationTests.Lumen}");
                s.AddSingleton<IActivityPubClientFactory>(
                    new RoutingClientFactory(
                        lumen.Key, instanceActorIri, () => serverARef().CreateHandler()));
            },
        };

        return (optionsA, optionsB);
    }

    private static IActivityPubClient BuildClient(KeyPair key, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, keyId);
        var signer = new HttpSignatureSigner(store);
        return new IdentityKeys(store, provider, signer);
    }

    /// <summary>
    /// An <see cref="IActivityPubClientFactory"/> that creates clients routed to the target server
    /// (instead of a real <see cref="HttpClientHandler"/>). Used to override the factory that
    /// <c>AddActivityPubServer</c> registers, so the <c>ICommunityFeedService</c> factory can create
    /// a client that reaches the other instance's test server.
    /// </summary>
    private sealed class RoutingClientFactory : IActivityPubClientFactory
    {
        private readonly ActivityPubClientFactory _inner;

        public RoutingClientFactory(
            KeyPair key, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
        {
            var keyStore = new InMemoryKeyStore();
            keyStore.PutKey(key);
            var keyProvider = new InMemoryKeyProvider(keyStore);
            keyProvider.RegisterKey(actorIri, key.KeyId);
            var signer = new HttpSignatureSigner(keyStore);
            _inner = new ActivityPubClientFactory(keyStore, keyProvider, signer);
            _handlerFactory = handlerFactory;
        }

        private readonly Func<HttpMessageHandler> _handlerFactory;

        public IActivityPubClient Create(
            ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            options.Caches = null;
            return _inner.Create(options, new LazyHandler(_handlerFactory));
        }

        public IActivityPubClient Create(
            ActivityPubClientOptions options,
            HttpMessageHandler httpHandler,
            DelegatingHandler? outermost)
        {
            return _inner.Create(options, new LazyHandler(_handlerFactory), outermost);
        }

        public ILocalModerationClient CreateLocalModerationClient(
            ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            return _inner.CreateLocalModerationClient(options, new LazyHandler(_handlerFactory));
        }

        public IMediaClient CreateMediaClient(
            ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            return _inner.CreateMediaClient(options, new LazyHandler(_handlerFactory));
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
    /// actor IRI's host.
    /// </summary>
    private sealed class PaginationRoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public PaginationRoutingFetcher(
            string aHost, Func<HttpMessageHandler> aHandlerFactory,
            string bHost, Func<HttpMessageHandler> bHandlerFactory,
            KeyPair signingKey, Iri signingActor)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, aHandlerFactory, signingKey, signingActor),
                [bHost] = BuildFetcherFor(bHost, bHandlerFactory, signingKey, signingActor),
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
            string host, Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
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
}

/// <summary>
/// xunit collection definition for the cross-instance-pagination shared two-host fixture.
/// </summary>
[CollectionDefinition("CrossInstancePagination")]
public sealed class CrossInstancePaginationCollection : ICollectionFixture<CrossInstancePaginationSharedHost>
{
}
