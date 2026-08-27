using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
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
/// Phase 5 integration test for the <strong>community-following</strong> slice: a local community
/// follows a remote actor (over the wire), and the remote actor's content — delivered to the community's
/// inbox — is propagated to the community's local members and appears in the community's unified feed.
/// This is the "followed content" half of the feed, complementing the local member posts.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c>) and instance B (b.domain.local, actor
/// <c>bob</c>) — the same two-instance federation harness as the outbound delivery test. B hosts a
/// community <c>iris</c> (a <see cref="Group"/>) with a single local member <c>bob</c>.
/// <list type="number">
/// <item><em>Community follows a remote actor:</em> a <see cref="Follow"/> (actor = A's alice, object =
/// B's community) is delivered to B's community inbox. B's <see cref="FollowActivityHandler"/> records
/// the follow in the community's follows set (the community follows alice) and queues an
/// <see cref="Accept"/> back to alice's inbox.</item>
/// <item><em>Followed content reaches the feed:</em> a <see cref="Create"/> by alice is delivered to B's
/// community inbox. B's <see cref="CommunityInboxActivityHandler"/> propagates it to the local member
/// bob's outbox; the community feed (which merges member outboxes) then surfaces alice's note.</item>
/// </list>
/// The deliveries are signed over the wire by a <see cref="DeliveryWorker"/> (routing to B's
/// <c>TestServer</c>); B validates each signature by resolving the sender's key from A's actor document
/// (fetched by B's <c>IActorDocumentFetcher</c>, wired to A's <c>TestServer</c>). The community
/// follows-set and the propagated member outbox are asserted directly against B's persistence, and the
/// feed is asserted against B's <c>GET /ap/v1/c/{name}/feed</c> endpoint.
/// </remarks>
public sealed class CommunityFollowingIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";
    private const string RemoteCommunity = "lumen";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _aliceInboxIri;
    private readonly Iri _communityIri;
    private readonly Iri _communityInboxIri;
    private readonly Iri _bobActorIri;
    private readonly KeyPair _remoteCommunityKey;
    private readonly Iri _remoteCommunityIri;
    private readonly Iri _remoteCommunityInboxIri;

    public CommunityFollowingIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (public actor document, so B can resolve alice's key) and a second community
        // <c>lumen</c> (a Group with a real key) that B's community can follow over the wire.
        var aSeeded = Seed(aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        _aliceInboxIri = _aliceActorIri.InboxOf();

        _remoteCommunityIri = new Iri($"https://{AHost}/ap/v1/c/{RemoteCommunity}");
        _remoteCommunityInboxIri = _remoteCommunityIri.InboxOf();
        _remoteCommunityKey = SeedCommunity(aPersistence, AHost, RemoteCommunity);

        // B hosts bob; its fetcher is wired to A so B can validate signatures by fetching A's actor doc
        // (and A's community doc, so B can resolve A's community-as-follower's key).
        var bSeeded = Seed(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        // B also hosts a community <c>iris</c> with bob as its (only) local member, and a real key so it
        // can sign outbound follows (a Group is a follower just like a Person).
        _communityIri = new Iri($"https://{BHost}/ap/v1/c/{Community}");
        _communityInboxIri = new Iri($"{_communityIri.Value}/inbox");
        var communityKey = SeedCommunity(_bPersistence, BHost, Community, _bobActorIri);

        _a = StartServer(AHost, Alice, aPersistence, _aliceKey);
        _b = StartServer(BHost, Bob, _bPersistence, bSeeded.Key, communityKey,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, targetServer: _a));
        _bHttp = new HttpClient(_b.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _bHttp.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- A community follows a remote actor: the follow is recorded + an Accept is queued --

    [Fact]
    public async Task Community_FollowOfRemoteActor_RecordsCommunityFollow()
    {
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _b);

        // alice (A) follows B's community: a Follow delivered to the community inbox.
        var follow = BuildFollow(_aliceActorIri, _communityIri);
        await worker.Service.DeliverAsync(_communityInboxIri, follow);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the delivery (the follow edge recorded in B's community follows set),
        // not on storage: the inbox processor stores the activity before dispatching it to the handler,
        // so "stored" is not a sufficient signal that the handler has run. (The worker also drains the
        // Accept that B queues back to alice, so the queue count is not a reliable assertion target.)
        await WaitForAsync(async () =>
            (await _bPersistence.Communities.GetFollowsAsync(_communityIri)).Contains(_aliceActorIri),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // B validated the signature and stored the follow; the FollowActivityHandler recorded the follow
        // in the community's follows set (the community follows alice).
        var follows = await _bPersistence.Communities.GetFollowsAsync(_communityIri);
        // The community follows alice after the follow is delivered.
        Assert.Contains(_aliceActorIri, follows);
        // A follow of a community is not a membership grant: alice is not a member of the community.
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
        // B stored the follow activity (the federation loop ran end-to-end).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _));
    }

    // --- Followed content is propagated to local members and appears in the feed ----------

    [Fact]
    public async Task RemoteContent_ToCommunityInbox_PropagatesToMemberAndAppearsInFeed()
    {
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _b);

        // alice (A) posts a Create, delivered to B's community inbox (as a follower's content would be).
        var create = BuildCreate(_aliceActorIri);
        await worker.Service.DeliverAsync(_communityInboxIri, create);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the delivery (the content recorded in bob's outbox), not on storage: the
        // inbox processor stores the activity before dispatching it to the handler, so "stored" is not a
        // sufficient signal that the handler has run.
        await WaitForAsync(async () =>
            (await _bPersistence.Activities.GetOutboxAsync(_bobActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == create.Id),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // The Create was stored (B validated the signature).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            "B should have stored the content delivered to the community inbox");

        // The CommunityInboxActivityHandler recorded the content in the local member bob's outbox (the
        // Create activity itself, newest first) — the member's posted activities.
        var bobOutbox = await _bPersistence.Activities.GetOutboxAsync(_bobActorIri);
        Assert.Contains(bobOutbox, o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // The community feed (which merges member outboxes) surfaces the followed content.
        var response = await _bHttp.GetAsync($"https://{BHost}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var itemIds = GetItems(doc.RootElement).Select(ItemId).ToList();
        // The followed community content (alice's Create) appears in the community feed.
        Assert.Contains(create.Id!, itemIds);
    }

    // --- Guards -----------------------------------------------------------------------------

    [Fact]
    public async Task Content_ToUnknownCommunityInbox_IsNotPropagated()
    {
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _b);

        // A content activity to an unknown community's inbox → 404 → the worker drops it; nothing is
        // stored or propagated.
        var unknownInbox = new Iri($"https://{BHost}/ap/v1/c/nobody/inbox");
        var create = BuildCreate(_aliceActorIri);
        await worker.Service.DeliverAsync(unknownInbox, create);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await worker.StopAsync(CancellationToken.None);

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
        Assert.Empty(await _bPersistence.Activities.GetOutboxAsync(_bobActorIri));
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Seeds a community (a <see cref="Group"/>) with a real EC key (carried in the <c>publicKey</c>
    /// extension, so a remote resolver can verify signatures the community signs) and an optional
    /// local member. Returns the community's key (so it can be registered for outbound signing).
    /// </summary>
    private static KeyPair SeedCommunity(
        InMemoryPersistenceProvider persistence, string host, string name,
        Iri? memberIri = null)
    {
        var communityIri = new Iri($"https://{host}/ap/v1/c/{name}");
        var keyId = new Iri($"{communityIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var community = new Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [name],
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = communityIri.Value,
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();

        if (memberIri is not null)
        {
            persistence.Communities.AddMemberAsync(communityIri, memberIri.Value).GetAwaiter().GetResult();
        }

        return key;
    }

    /// <summary>
    /// Seeds a persistence provider with a single actor (Person) + a real EC key, carrying the real
    /// JWK in the <c>publicKey</c> extension (so a remote resolver can verify signatures).
    /// </summary>
    private static (KeyPair Key, Iri ActorIri) Seed(
        InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIriString = $"https://{host}/ap/v1/u/{handle}";
        var actorIri = new Iri(actorIriString);
        var keyId = new Iri($"{actorIriString}#key-1");

        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIriString,
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        return (key, actorIri);
    }

    private static string ExtractJwkComponent(KeyPair key, string name)
    {
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }

    /// <summary>
    /// A hosted <see cref="DeliveryWorker"/> (signed as the instance actor, routing deliveries to the
    /// target server). Exposes the worker's <see cref="IDeliveryService"/> and
    /// <see cref="IDeliveryQueue"/> and starts/stops the worker via a minimal host.
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
    /// routes to <paramref name="targetServer"/> — i.e. B's fetcher reaches A's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair bobKey, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var bobActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(bobActorIri, bobKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = bobActorIri, EnableRetry = false },
            targetServer.CreateHandler());

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, registering
    /// the instance actor's key (and, when provided, a community key for outbound signing) and
    /// overriding the <see cref="IActorDocumentFetcher"/> (for the federation wiring).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey,
        KeyPair? communityKey = null,
        IActorDocumentFetcher? fetcher = null)
    {
        var instanceActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");

        // Register the instance actor's key (and the community's key, when provided) in the key store
        // so the server can sign outbound deliveries as either identity. The community key lets B's
        // DeliveryWorker sign a Follow as the community (a Group is a follower just like a Person).
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
            var communityIri = new Iri($"https://{host}/ap/v1/c/{Community}");
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

                // Register the community key so the server's DeliveryWorker can sign as the community.
                if (communityKey is not null)
                {
                    s.AddSingleton<IKeyStore>(keyStore);
                    s.AddSingleton<IKeyProvider>(keyProvider);
                    s.AddSingleton<ISignatureSigner>(signer);
                }

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

    private static Follow BuildFollow(Iri followerIri, Iri targetIri) => new()
    {
        Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(followerIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    private static Create BuildCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["followed content"] },
        ],
    };

    private static List<JsonElement> GetItems(JsonElement root)
    {
        var items = root.GetProperty("items");
        return items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()]
            : [items];
    }

    private static string ItemId(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : element.GetProperty("id").GetString()!;

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
