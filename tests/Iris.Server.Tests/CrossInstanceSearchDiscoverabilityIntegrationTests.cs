using System.Net;
using System.Text.Json;
using KristofferStrube.ActivityStreams;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Services;
using Iris.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.16: cross-instance search and discoverability checks. Verifies that federated
/// communities and posts become discoverable after handshake + first delivery:
/// (1) community search on B finds posts authored by a remote actor on A (via the peering-driven
/// remote outbox walk in <see cref="ICommunityFeedService"/>);
/// (2) global search on B finds the remote object that was stored in B's object store during
/// inbox delivery;
/// (3) a deep link to a remote post resolves on the origin instance (A) without B having
/// cached it;
/// (4) the same deep link 404s on the non-origin instance (B) because the object-document
/// endpoint reconstructs the IRI as local-base + path.
/// </summary>
[Collection("CrossInstanceSearchDiscoverability")]
public sealed class CrossInstanceSearchDiscoverabilityIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "search-a.domain.local";
    internal const string BHost = "search-b.domain.local";
    internal const string Alice = "alice";
    internal const string Lumen = "lumen";

    internal static readonly Iri AliceIri = new($"https://{AHost}/ap/v1/u/{Alice}");
    internal static readonly Iri LumenIri = new($"https://{BHost}/ap/v1/c/{Lumen}");
    internal static readonly Iri AliceOutboxIri = new($"https://{AHost}/ap/v1/u/{Alice}/outbox");

    private readonly CrossInstanceSearchSharedHost _fixture;
    private readonly ITestOutputHelper _output;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly IActivityPubClient _signedClient;

    public CrossInstanceSearchDiscoverabilityIntegrationTests(
        CrossInstanceSearchSharedHost fixture,
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

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture();
        await DeliverRemotePostToBAsync();
    }

    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    // --- Community search finds remote content via peering ---------------------------------

    [Fact]
    public async Task CommunitySearch_FindsRemotePost_ViaPeeringEdge()
    {
        // B's community search for a term that appears only in alice's (remote) posts.
        var resp = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/c/{Lumen}/search?q=federated");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = GetItemIds(doc.RootElement);

        Assert.Contains("post-federated-1", items);
        Assert.Contains("post-federated-2", items);
    }

    [Fact]
    public async Task CommunitySearch_DoesNotFindUnrelatedTerm()
    {
        // A term that appears in neither alice's remote posts nor lumen's local content.
        var resp = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/c/{Lumen}/search?q=nonexistentterm");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = GetItemIds(doc.RootElement);

        Assert.Empty(items);
    }

    // --- Global search finds locally-cached remote content ---------------------------------

    [Fact]
    public async Task GlobalSearch_FindsDeliveredRemoteObject_OnReceivingInstance()
    {
        // After delivery, the embedded object is stored in B's object store under its original
        // IRI (A's host). B's global search (the content pass has no localOnly restriction)
        // should find it.
        var resp = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/search?q=deliveredcontent");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = GetItemIds(doc.RootElement);

        Assert.Contains("post-delivered-1#note", items);
    }

    [Fact]
    public async Task GlobalSearch_LocalOnly_ExcludesCachedRemoteActor()
    {
        // With ?local=true, the actor pass is restricted to this instance's own actors.
        // Alice (a remote actor cached in B's actor store) should not appear.
        var resp = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/search?q=alice&local=true");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = GetItemIds(doc.RootElement);

        Assert.DoesNotContain(AliceIri.Value, items);
    }

    // --- Deep links resolve on the origin instance -----------------------------------------

    [Fact]
    public async Task DeepLink_RemotePost_ResolvesOnOriginInstance()
    {
        // A deep link to alice's post on A resolves when requested from A (the origin).
        var postIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/post-deep-1");
        var resp = await _aHttp.GetAsync(postIri.Value);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(postIri.Value, doc.RootElement.GetProperty("id").GetString());
        // The object-document endpoint returns the stored activity (a Create wrapping the Note),
        // so the top-level type is "Create" (the embedded Note is under "object").
        Assert.Equal("Create", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task DeepLink_RemotePost_404sOnNonOriginInstance()
    {
        // The same deep link 404s when requested from B: B's object-document endpoint
        // reconstructs the IRI as B's local base + path, which does not match the stored
        // remote object (stored under A's host IRI).
        var postIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/post-deep-1");
        var resp = await _bHttp.GetAsync(postIri.Value);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static List<string> GetItemIds(JsonElement root)
    {
        var ids = new List<string>();
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                {
                    ids.Add(id.GetString()!);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Delivers a post from alice (A) to lumen's inbox (B) so the embedded object is stored
    /// in B's object store (the same path production federation takes).
    /// </summary>
    private async Task DeliverRemotePostToBAsync()
    {
        var lumenInboxIri = new Iri($"https://{BHost}/ap/v1/c/{Lumen}/inbox");
        await _signedClient.DeliverAsync(
            lumenInboxIri,
            new Create
            {
                Id = "post-delivered-1",
                Actor = [new Link { Href = new Uri(AliceIri.Value) }],
                Object = [new Note
                {
                    Id = "post-delivered-1#note",
                    Content = ["deliveredcontent payload"],
                    AttributedTo = [new Link { Href = new Uri(AliceIri.Value) }],
                }],
            },
            CancellationToken.None);
    }

    private void SeedForFixture()
    {
        // A: alice (person) with several posts in her outbox.
        TestSeeder.SeedPersonWithExistingKey(
            _aPersistence, AHost, Alice, new Iri($"{AliceIri.Value}#key-1"));

        TestSeeder.AddCreateActivity(
            _aPersistence, AliceIri, "post-federated-1", "federated content one");
        TestSeeder.AddCreateActivity(
            _aPersistence, AliceIri, "post-federated-2", "federated content two");
        TestSeeder.AddCreateActivity(
            _aPersistence, AliceIri, "post-delivered-1", "deliveredcontent payload");
        var deepPostIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/post-deep-1");
        TestSeeder.AddCreateActivity(
            _aPersistence, AliceIri, deepPostIri.Value, "deep link target",
            attributedTo: [AliceIri]);
        // Also store the activity in the global Activities store so the object-document
        // endpoint can resolve it by IRI (AddCreateActivity only adds to the outbox).
        var deepNote = new Note
        {
            Id = $"{deepPostIri.Value}#note",
            Content = ["deep link target"],
            AttributedTo = [new Link { Href = new Uri(AliceIri.Value) }],
        };
        _aPersistence.Activities.PutActivityAsync(new Create
        {
            Id = deepPostIri.Value,
            Actor = [new Link { Href = new Uri(AliceIri.Value) }],
            Object = [deepNote],
        }).GetAwaiter().GetResult();

        // B: lumen (community) with a local member bob and a follow edge to alice.
        TestSeeder.SeedCommunityWithExistingKey(
            _bPersistence, BHost, Lumen, new Iri($"{LumenIri.Value}#key-1"));

        var bobIri = new Iri($"https://{BHost}/ap/v1/u/bob");
        _bPersistence.Communities.AddFollowerAsync(LumenIri, bobIri).GetAwaiter().GetResult();
        _bPersistence.Communities.AddFollowAsync(LumenIri, AliceIri).GetAwaiter().GetResult();

        // A local post on B (authored by bob, tagged to lumen) that does NOT contain
        // the "federated" term, so the community-search test can distinguish local vs remote.
        TestSeeder.AddCreateActivity(
            _bPersistence, bobIri, "post-local-1", "locallyauthored content",
            attributedTo: [bobIri, LumenIri]);
    }
}

/// <summary>
/// Shared two-host fixture for the cross-instance search/discoverability collection.
/// </summary>
public sealed class CrossInstanceSearchSharedHost : SharedTwoHostFixture
{
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;

    public IActivityPubClient SignedClient { get; }

    public CrossInstanceSearchSharedHost()
        : base(BuildOptions(out var aPersistence, out var bPersistence))
    {
        _aPersistence = aPersistence;
        _bPersistence = bPersistence;
        SignedClient = BuildSignedClient();
    }

    private IActivityPubClient BuildSignedClient()
    {
        var alice = TestSeeder.SeedPersonWithKey(
            _aPersistence, CrossInstanceSearchDiscoverabilityIntegrationTests.AHost,
            CrossInstanceSearchDiscoverabilityIntegrationTests.Alice);
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(alice.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(CrossInstanceSearchDiscoverabilityIntegrationTests.AliceIri, alice.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions
            {
                ActorId = CrossInstanceSearchDiscoverabilityIntegrationTests.AliceIri,
                EnableRetry = false,
            },
            new LazyHandler(() => ServerB.CreateHandler()));
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions(
        out InMemoryPersistenceProvider aPersistence, out InMemoryPersistenceProvider bPersistence)
    {
        aPersistence = new InMemoryPersistenceProvider();
        bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(
            aPersistence,
            CrossInstanceSearchDiscoverabilityIntegrationTests.AHost,
            CrossInstanceSearchDiscoverabilityIntegrationTests.Alice);
        var lumen = TestSeeder.SeedCommunityWithKey(
            bPersistence,
            CrossInstanceSearchDiscoverabilityIntegrationTests.BHost,
            CrossInstanceSearchDiscoverabilityIntegrationTests.Lumen);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceSearchDiscoverabilityIntegrationTests.AHost,
            Handle = CrossInstanceSearchDiscoverabilityIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, alice.ActorIri, alice.KeyId),
            Fetcher = new SearchRoutingFetcher(
                CrossInstanceSearchDiscoverabilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceSearchDiscoverabilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                alice.Key, alice.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceSearchDiscoverabilityIntegrationTests.BHost,
            Handle = CrossInstanceSearchDiscoverabilityIntegrationTests.Lumen,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(lumen.Key, lumen.CommunityIri, lumen.KeyId),
            Fetcher = new SearchRoutingFetcher(
                CrossInstanceSearchDiscoverabilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceSearchDiscoverabilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                lumen.Key, lumen.CommunityIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            PreServices = s =>
            {
                var feedOpts = new FeedOptions { PagesPerActor = 3, MaxItems = 60 };
                s.AddSingleton(feedOpts);
                s.AddSingleton<IOptions<FeedOptions>>(
                    new OptionsWrapper<FeedOptions>(feedOpts));
                var instanceActorIri = new Iri(
                    $"https://{CrossInstanceSearchDiscoverabilityIntegrationTests.BHost}/ap/v1/u/" +
                    $"{CrossInstanceSearchDiscoverabilityIntegrationTests.Lumen}");
                s.AddSingleton<IActivityPubClientFactory>(
                    new SearchRoutingClientFactory(
                        lumen.Key, instanceActorIri, () => serverARef().CreateHandler()));
            },
        };

        return (optionsA, optionsB);
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
    /// An <see cref="IActivityPubClientFactory"/> that creates clients routed to the target
    /// server (instead of a real <see cref="HttpClientHandler"/>). Overrides the factory that
    /// <c>AddActivityPubServer</c> registers, so the <c>ICommunityFeedService</c> factory can
    /// create a client that reaches the other instance's test server.
    /// </summary>
    private sealed class SearchRoutingClientFactory : IActivityPubClientFactory
    {
        private readonly ActivityPubClientFactory _inner;
        private readonly Func<HttpMessageHandler> _handlerFactory;

        public SearchRoutingClientFactory(
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
    private sealed class SearchRoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public SearchRoutingFetcher(
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
/// xunit collection definition for the cross-instance search/discoverability shared two-host fixture.
/// </summary>
[CollectionDefinition("CrossInstanceSearchDiscoverability")]
public sealed class CrossInstanceSearchDiscoverabilityCollection : ICollectionFixture<CrossInstanceSearchSharedHost>
{
}
