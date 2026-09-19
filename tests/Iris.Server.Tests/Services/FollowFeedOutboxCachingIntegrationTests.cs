using System.Net;
using Iris.Client;
using Iris.Client.Caching;
using ClientCollectionPageCache = Iris.Client.Collections.CollectionPageCache;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Services;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Services;

/// <summary>
/// Phase 146.3 (F-136.12.3/5): the follow-feed endpoint walks each remote follow's outbox page-by-page,
/// making O(follows × pages) uncached GETs per feed request. 146.3 wires a client-side
/// <see cref="ClientCollectionPageCache"/> (30 s TTL) into the outbound <see cref="IActivityPubClient"/> so
/// a feed refresh within the TTL does not re-fetch the same pages over the wire.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c>) follows two remote actors: <c>bob</c> and
/// <c>dave</c>, both on instance B (b.domain.local). Each remote actor's outbox has 2 items. A counting
/// handler on the outbound path records every GET. The test calls the feed endpoint twice and asserts
/// that the second call makes zero GETs to B (all actor docs + outbox pages are served from the 30 s TTL
/// cache).
/// </remarks>
public sealed class FollowFeedOutboxCachingIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Dave = "dave";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _http;
    private readonly IActivityPubClient _signedClient;
    private readonly CountingHandler _counter;

    public FollowFeedOutboxCachingIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var (aKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        var (bKey, bobIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        var (_, daveIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Dave);

        // alice follows bob and dave — both remote (on B).
        _aPersistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
        _aPersistence.Follows.RecordFollowAsync(aliceIri, daveIri).GetAwaiter().GetResult();

        // bob's outbox: 2 items. dave's outbox: 2 items.
        TestSeeder.AddCreateActivity(_bPersistence, bobIri, $"{bobIri.Value}/activities/b-1", "bob 1");
        TestSeeder.AddCreateActivity(_bPersistence, bobIri, $"{bobIri.Value}/activities/b-2", "bob 2");
        TestSeeder.AddCreateActivity(_bPersistence, daveIri, $"{daveIri.Value}/activities/d-1", "dave 1");
        TestSeeder.AddCreateActivity(_bPersistence, daveIri, $"{daveIri.Value}/activities/d-2", "dave 2");

        // B hosts bob + dave.
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
        });

        _counter = new CountingHandler(_b.CreateHandler());

        // A hosts alice. Override IFollowFeedService to route outbound fetches through the counting
        // handler. The outbound client carries a CollectionPageCache (146.3) + ActorCache (146.1).
        //
        // The signed test client below signs as alice, so A's inbound signature validator resolves
        // alice's key by fetching A's OWN actor document. A's default IActorDocumentFetcher would use a
        // real HttpClientHandler (which cannot reach the in-process TestServer), so it is overridden
        // with one wired to A's own TestServer (deferred via the LazyHandler — A does not exist yet
        // while the host is being constructed).
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aKey);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aliceIri, new Iri($"{aliceIri.Value}#key-1"));
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var clientFactory = new ActivityPubClientFactory(aKeyStore, aKeyProvider, aSigner);

        var pageCache = new ClientCollectionPageCache();
        var actorCache = new ActorCache();

        TestServer? aServerRef = null;
        var selfFetcher = new IrisActorDocumentFetcher(
            clientFactory.Create(
                new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
                new LazyHandler(() => aServerRef!.CreateHandler())),
            new RemoteActorCache());

        // A signed client (signed as alice) that reaches A's own endpoint. The follow-feed endpoint is
        // owner-gated (139.2-s5c): only a valid signature as the feed's owner is served the feed, so the
        // test's reads go through this signed client instead of an unsigned HttpClient (an unsigned GET
        // is 403'd). The LazyHandler defers A's TestServer handler to the first request (A does not
        // exist yet while the test is being constructed).
        _signedClient = clientFactory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            new LazyHandler(() => aServerRef!.CreateHandler()));

        var outboundClient = clientFactory.Create(
            new ActivityPubClientOptions
            {
                ActorId = aliceIri,
                EnableRetry = false,
                Caches = new ClientCaches(Actors: actorCache, CollectionPages: pageCache),
            },
            _counter);

        var a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                s.AddSingleton<IActorDocumentFetcher>(selfFetcher);

                s.AddSingleton<IFollowFeedService>(sp => new FeedService(
                    sp.GetRequiredService<IPersistenceProvider>(),
                    sp.GetRequiredService<ILocalActorResolver>(),
                    sp.GetRequiredService<IActorDocumentFetcher>(),
                    outboundClient,
                    sp.GetRequiredService<IOptions<FeedOptions>>()));
            },
        });
        _a = a;
        aServerRef = _a;

        _http = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _signedClient.Dispose();
        _http.Dispose();
        _a.Dispose();
        _b.Dispose();
        _counter.Dispose();
    }

    [Fact]
    public async Task FollowFeed_SecondCall_UsesCachedOutboxPages()
    {
        var aBase = $"https://{AHost}";

        // First feed call: fetches bob's and dave's actor docs + outbox pages over the wire. The read is
        // signed as alice (the feed's owner) via the signed client — the endpoint is owner-gated
        // (139.2-s5c) and 403s an unsigned GET. GetObjectAsync returns the feed's page-1 document
        // (an OrderedCollection carrying its items); a null return means the request failed.
        var first = await _signedClient.GetObjectAsync(new Iri($"{aBase}/ap/v1/u/{Alice}/feed?limit=10"));
        Assert.NotNull(first);

        // The first call should have made GETs to B (actor docs + outbox pages).
        var gets1 = _counter.TotalGets;
        Assert.True(gets1 > 0, $"Expected GETs to B on the first feed call, but got 0.");

        _counter.Reset();

        // Second feed call (within the 30 s TTL): all actor docs + outbox pages should be served from cache.
        var second = await _signedClient.GetObjectAsync(new Iri($"{aBase}/ap/v1/u/{Alice}/feed?limit=10"));
        Assert.NotNull(second);

        // Both responses should return the same feed (4 items: bob's 2 + dave's 2). The items are the
        // outbox pages' contents: Create activities, each carrying the created note as `object`. The
        // item's identity is the note's IRI (the Create's `object`), which is what the feed dedupes by.
        var ids1 = FeedItemIds(first!);
        var ids2 = FeedItemIds(second!);
        Assert.Equal(4, ids1.Length);
        Assert.Equal(ids1, ids2);

        // The second call should make zero GETs to B (all cached within the TTL).
        var gets2 = _counter.TotalGets;
        Assert.True(gets2 == 0,
            $"Expected 0 GETs to B on the second feed call (ActorCache + CollectionPageCache should serve from cache), but got {gets2}.");
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Reads the item IRIs from a feed page document (an <see cref="OrderedCollection"/>/
    /// <see cref="OrderedCollectionPage"/>). Each feed item is a <see cref="Create"/> activity whose
    /// <c>object</c> is the created note; the note's IRI (the Create's <c>object</c> href, or the
    /// Create's own IRI when the note is inline) is the item's identity.
    /// </summary>
    private static string[] FeedItemIds(IObject page)
    {
        var items = (page as Collection) is { } collection
            ? CollectionPageFactory.ResolveCollectionItems(collection)
            : [];
        var ids = new List<string>();
        foreach (var item in items)
        {
            var id = FeedItemId(item);
            if (id is not null)
            {
                ids.Add(id);
            }
        }

        return [.. ids];
    }

    private static string? FeedItemId(IObjectOrLink item)
    {
        var obj = item as IObject;
        if (obj is null)
        {
            return null;
        }

        // A Create carries the created note as `object` (a Link to the note's IRI, or the note inline).
        if (obj is Create { Object: { } objects })
        {
            var note = objects.FirstOrDefault() as IObjectOrLink;
            if (note is not null && note.Id is { } noteId)
            {
                return noteId;
            }
        }

        return obj.Id;
    }

    /// <summary>
    /// A counting <see cref="DelegatingHandler"/> that records every GET and forwards via
    /// <see cref="DelegatingHandler.SendAsync(HttpRequestMessage, CancellationToken)"/> to the inner handler.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private int _totalGets;
        private readonly object _lock = new();

        public CountingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        public int TotalGets
        {
            get { lock (_lock) { return _totalGets; } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            lock (_lock)
            {
                if (request.Method == HttpMethod.Get)
                {
                    _totalGets++;
                }
            }

            return await base.SendAsync(request, ct);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _totalGets = 0;
            }
        }
    }
}
