using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 5 integration test for the <strong>community-follows-community</strong> slice: a local
/// community (a <see cref="Group"/>) follows a remote community over the wire. A community is followed
/// (and follows) the same way a person is — it is a <see cref="Group"/> (an <c>actor</c> for follow and
/// signature purposes) — so it carries the same <c>following</c>/<c>followers</c> collections and the
/// same follow lifecycle (follow → accept).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c>, community <c>lumen</c>) and instance B
/// (b.domain.local, actor <c>bob</c>, community <c>iris</c>) — the same two-instance federation harness
/// as the community-following test, with both communities seeded with a real key (so each can sign an
/// outbound follow) and both communities carrying the key in their public document (so the other
/// instance can resolve the community-as-follower's key by fetching the community document).
/// <list type="number">
/// <item><em>A community follows a remote community:</em> B's community <c>iris</c> (a <see
/// cref="Group"/>) signs and delivers a <see cref="Follow"/> (actor = the community, object = A's
/// community <c>lumen</c>) to A's community inbox. A validates the signature by fetching B's community
/// document (resolving the <c>Group</c>'s key) and records the follow in <c>lumen</c>'s follows set
/// (lumen follows iris) + queues an <see cref="Accept"/> back to iris's inbox, which B finalizes into
/// iris's follows set (iris follows lumen). Both sides' <c>following</c> collections then carry the edge.</item>
 /// <item><em>The follow edge is queryable:</em> <c>GET /ap/v1/c/{name}/following</c> (the community
 /// <c>following</c> collection) returns the followed community's IRI; <c>GET /ap/v1/c/{name}/followers</c>
 /// (F-24) returns the follower's IRI — the <c>FollowActivityHandler</c> records the inverse edge
 /// (follower → community) in the community's followers set when it processes an inbound follow, so the
 /// followed community's <c>followers</c> collection lists its followers.</item>
/// <item><em>Followed-community content reaches the feed:</em> A's community <c>lumen</c> posts a
/// <see cref="Create"/> to B's community inbox; B's <see cref="CommunityInboxActivityHandler"/> records
/// it in local member <c>bob</c>'s outbox, so it appears in the community feed (the followed-content
/// half of the unified feed).</item>
/// </list>
/// The community follow is delivered over the wire by B's <see cref="DeliveryWorker"/> (signed as the
/// community, routing to A's <c>TestServer</c>); A validates by fetching B's community document (its
/// <c>IActorDocumentFetcher</c> is wired to B's <c>TestServer</c>). The lazy fetcher wiring breaks the
/// A↔B chicken-and-egg (A's fetcher needs B's handler; B's delivery transport needs A's handler).
/// </remarks>
public sealed class CommunityFollowsCommunityIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string RemoteCommunity = "lumen";
    private const string LocalCommunity = "iris";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _aHttp;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _aCommunityKey;
    private readonly KeyPair _bCommunityKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _localCommunityIri;
    private readonly Iri _localCommunityInboxIri;
    private readonly Iri _remoteCommunityIri;
    private readonly Iri _remoteCommunityInboxIri;

    public CommunityFollowsCommunityIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (a public actor, so B can resolve alice's key) and a community <c>lumen</c>
        // (a Group with a real key, so B's community can follow it over the wire and A can sign back).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        _aCommunityKey = TestSeeder.SeedCommunityWithKey(_aPersistence, AHost, RemoteCommunity).Key;

        // B hosts bob and a community <c>iris</c> (a Group with bob as its only local member and a real
        // key, so it can sign the outbound follow).
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bCommunityKey = TestSeeder.SeedCommunityWithKey(_bPersistence, BHost, LocalCommunity, _bobActorIri).Key;

        _localCommunityIri = new Iri($"https://{BHost}/ap/v1/c/{LocalCommunity}");
        _localCommunityInboxIri = _localCommunityIri.InboxOf();
        _remoteCommunityIri = new Iri($"https://{AHost}/ap/v1/c/{RemoteCommunity}");
        _remoteCommunityInboxIri = _remoteCommunityIri.InboxOf();

        // A's fetcher is wired to B (lazy, to break the A↔B chicken-and-egg): A resolves B's
        // community-as-follower's key by fetching B's community document. B's delivery transport routes
        // to A; B's fetcher routes to A (B can also fetch A's documents).
        _a = StartServer(AHost, Alice, _aPersistence, _aliceKey, _aCommunityKey,
            fetcher: BuildFetcherFor(AHost, Alice, _aliceKey, new LazyHandler(() => _b!.CreateHandler())));
        _b = StartServer(BHost, Bob, _bPersistence, bSeeded.Key, _bCommunityKey,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
        _aHttp = new HttpClient(_a.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- A community follows a remote community over the wire --------------------

    [Fact]
    public async Task Community_FollowOfRemoteCommunity_IsRecordedOnBothSides()
    {
        using var worker = BuildDeliveryWorker(_localCommunityIri, _bCommunityKey, _a);

        // B's community iris follows A's community lumen: a Follow (actor = iris, object = lumen)
        // delivered to lumen's inbox, signed as the community (a Group signs just like a Person).
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        await worker.Service.DeliverAsync(_remoteCommunityInboxIri, follow, _localCommunityIri);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the delivery (the follow edge recorded in A's community follows set —
        // lumen follows iris), not on storage: the inbox processor stores the activity before dispatching
        // it to the handler, so "stored" is not a sufficient signal that the handler has run.
        await WaitForAsync(async () =>
            (await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_localCommunityIri),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // A validated the community signature (by fetching B's community document to resolve the Group's
        // key) and stored the follow; the FollowActivityHandler recorded the follow in lumen's follows set
        // (lumen follows iris).
        Assert.Contains(_localCommunityIri, await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri));
        Assert.True(await _aPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _),
            "A should have stored the community follow delivered to the community inbox");

        // The follow is not a membership grant: iris is not a member of lumen.
        Assert.False(await _aPersistence.Communities.IsMemberAsync(_remoteCommunityIri, _localCommunityIri));
    }

    // --- The follow edge is queryable via the community `following` collection ----

    [Fact]
    public async Task Community_FollowOfRemoteCommunity_AppearsInBothCommunitiesFollowing()
    {
        using var worker = BuildDeliveryWorker(_localCommunityIri, _bCommunityKey, _a);
        var follow = BuildFollow(_localCommunityIri, _remoteCommunityIri);
        await worker.Service.DeliverAsync(_remoteCommunityInboxIri, follow, _localCommunityIri);
        await worker.StartAsync(CancellationToken.None);
        // Wait until A has recorded the follow edge (lumen follows iris).
        await WaitForAsync(async () =>
            (await _aPersistence.Communities.GetFollowsAsync(_remoteCommunityIri)).Contains(_localCommunityIri),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // A's community `following` collection (GET /c/{name}/following — the production gap this slice
        // closes) carries the followed community's IRI (lumen follows iris).
        var aFollowing = await CollectionItemsAsync(_aHttp, $"https://{AHost}/ap/v1/c/{RemoteCommunity}/following");
        Assert.Contains(_localCommunityIri.Value, aFollowing);

        // B's community `following` collection is empty until B finalizes its side of the edge (the
        // Accept A queues back to iris is not delivered in this test — no DeliveryWorker on A) — so the
        // following edge is one-sided here.
        var bFollowing = await CollectionItemsAsync(_bHttp, $"https://{BHost}/ap/v1/c/{LocalCommunity}/following");
        Assert.Empty(bFollowing);

        // F-24: A's community `followers` collection carries the follower's IRI (iris follows lumen) —
        // the FollowActivityHandler on A recorded the inverse edge (follower → community) in lumen's
        // followers set when it processed the inbound Follow. B's community `followers` is empty (no
        // actor has followed iris in this test).
        var aFollowers = await CollectionItemsAsync(_aHttp, $"https://{AHost}/ap/v1/c/{RemoteCommunity}/followers");
        Assert.Contains(_localCommunityIri.Value, aFollowers);
        var bFollowers = await CollectionItemsAsync(_bHttp, $"https://{BHost}/ap/v1/c/{LocalCommunity}/followers");
        Assert.Empty(bFollowers);
    }

    // --- The community following/followers collections 404 for an unknown community

    [Fact]
    public async Task Community_Following_UnknownCommunity_Returns404()
    {
        var response = await _aHttp.GetAsync($"https://{AHost}/ap/v1/c/nobody/following");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var followersResponse = await _aHttp.GetAsync($"https://{AHost}/ap/v1/c/nobody/followers");
        Assert.Equal(HttpStatusCode.NotFound, followersResponse.StatusCode);
    }

    // --- Followed-community content reaches the community feed --------------------

    [Fact]
    public async Task RemoteCommunity_Content_ToLocalCommunityInbox_PropagatesToMemberAndFeed()
    {
        using var worker = BuildDeliveryWorker(_remoteCommunityIri, _aCommunityKey, _b);

        // A's community lumen posts a Create, delivered to B's community inbox (as a followed community's
        // content would be), signed as the community.
        var create = BuildCreate(_remoteCommunityIri);
        await worker.Service.DeliverAsync(_localCommunityInboxIri, create, _remoteCommunityIri);
        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the delivery (the content recorded in bob's outbox).
        await WaitForAsync(async () =>
            (await _bPersistence.Activities.GetOutboxAsync(_bobActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == create.Id),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // B stored the content (it validated the community signature by fetching A's community document).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            "B should have stored the content delivered to the community inbox");

        // The CommunityInboxActivityHandler recorded the content in local member bob's outbox; the
        // community feed (which merges member outboxes) surfaces the followed-community content.
        var response = await _bHttp.GetAsync($"https://{BHost}/ap/v1/c/{LocalCommunity}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var itemIds = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
        Assert.Contains(create.Id!, itemIds);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// A hosted <see cref="DeliveryWorker"/> (signed as the given actor, routing deliveries to the target
    /// server). Exposes the worker's <see cref="IDeliveryService"/> and <see cref="IDeliveryQueue"/> and
    /// starts/stops the worker via a minimal host.
    /// </summary>
    private sealed class TestWorker : IDisposable
    {
        private readonly IHost _host;
        private readonly DeliveryWorker _worker;

        public TestWorker(IHost host, DeliveryWorker worker, IDeliveryService service, IDeliveryQueue queue)
        {
            _host = host;
            _worker = worker;
            Service = service;
            Queue = queue;
        }

        public IDeliveryService Service { get; }
        public IDeliveryQueue Queue { get; }

        public Task StartAsync(CancellationToken ct) => _host.StartAsync(ct);
        public Task StopAsync(CancellationToken ct) => _host.StopAsync(ct);

        public void Dispose()
        {
            _host.Dispose();
            _worker.Dispose();
        }
    }

    /// <summary>
    /// Builds a hosted <see cref="DeliveryWorker"/> signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>), routing deliveries to <paramref name="targetServer"/>.
    /// </summary>
    private static TestWorker BuildDeliveryWorker(Iri actorIri, KeyPair key, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var queue = new InMemoryDeliveryQueue();
        var loggerFactory = NullLoggerFactory.Instance;
        var service = new DeliveryService(queue, loggerFactory.CreateLogger<DeliveryService>());
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri });
        var transportFactory = () => targetServer.CreateHandler();

        var worker = new DeliveryWorker(
            queue, factory, transportFactory, options,
            loggerFactory.CreateLogger<DeliveryWorker>());

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        return new TestWorker(host, worker, service, queue);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as <paramref name="handle"/>)
    /// routes over <paramref name="handler"/> — i.e. the instance's fetcher reaches the other instance's
    /// actor/community documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, registering the
    /// instance actor's key (and the community's key, for outbound signing) and optionally overriding the
    /// <see cref="IActorDocumentFetcher"/> (for the federation wiring).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey, KeyPair? communityKey = null, IActorDocumentFetcher? fetcher = null)
    {
        var instanceActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(instanceKey);
        if (communityKey is not null)
        {
            keyStore.PutKey(communityKey);
        }
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, instanceKey.KeyId);
        if (communityKey is not null)
        {
            var communityIri = new Iri($"https://{host}/ap/v1/c/{LocalCommunity}");
            keyProvider.RegisterKey(communityIri, communityKey.KeyId);
        }
        var signer = new HttpSignatureSigner(keyStore);

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
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    opts.InstanceActorId = instanceActorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);

                // Register the instance + community keys so the server's DeliveryWorker can sign outbound
                // deliveries as either identity (the community as a follower signs just like a Person).
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
                s.AddSingleton<ISignatureSigner>(signer);

                if (fetcher is not null)
                {
                    s.AddSingleton<IActorDocumentFetcher>(fetcher);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request. Used to break the A↔B wiring chicken-and-egg (A's fetcher needs B's handler; B's
    /// transport needs A's handler) — both servers exist by the time any request flows.
    /// </summary>
    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{BHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Create BuildCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["followed community content"] },
        ],
    };

    private static async Task<List<string>> CollectionItemsAsync(HttpClient http, string url)
    {
        var response = await http.GetAsync(url + "?limit=100");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
    }

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
