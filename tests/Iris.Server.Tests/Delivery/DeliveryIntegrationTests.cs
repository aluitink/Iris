using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
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

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Phase 4 integration tests for the <strong>outbound delivery</strong> slice: the
/// <see cref="DeliveryWorker"/> pumps a <see cref="DeliveryJob"/> off the
/// <see cref="InMemoryDeliveryQueue"/> and POSTs it to a remote inbox <em>over the wire</em>, signed
/// with the instance actor's key. The receiving instance (B) validates the HTTP signature (resolving
/// A's actor key by fetching A's actor document) and stores the delivered activity — proving the full
/// outbound→inbound federation loop end-to-end.
/// </summary>
/// <remarks>
/// Topology mirrors the inbound federation test: instance A (a.domain.local, alice) and instance B
/// (b.domain.local, bob). A's delivery worker is wired (via the <c>Func&lt;HttpMessageHandler&gt;</c>
/// transport seam) to route to B's <c>TestServer</c>; it signs as A's instance actor (alice). B's
/// inbox validates the signature and stores the activity.
/// </remarks>
public sealed class DeliveryIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri AliceActorIri;
    private readonly Iri BobActorIri;
    private readonly Iri BobInboxIri;

    public DeliveryIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        BobActorIri = bSeeded.ActorIri;
        BobInboxIri = BobActorIri.InboxOf();

        // A hosts alice (public actor document, so B can resolve alice's key).
        _a = StartServer(AHost, Alice, aPersistence);

        // B hosts bob; its fetcher is wired to A so B can validate signatures by fetching A's actor doc.
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, targetServer: _a));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- The worker delivers an enqueued job to B's inbox over the wire --------------

    [Fact]
    public async Task Worker_DeliversEnqueuedJob_ToRemoteInboxOverWire()
    {
        using var worker = BuildDeliveryWorker(AliceActorIri, _aliceKey, _b);
        var queue = worker.Queue;

        // Schedule a delivery (as if an activity handler had): an Accept to B's inbox.
        var accept = BuildAccept(AliceActorIri, BobInboxIri);
        await worker.Service.DeliverAsync(BobInboxIri, accept);

        // The job is enqueued (not yet delivered).
        Assert.Equal(1, queue.Count);

        // Start the worker: it dequeues and POSTs the job to B's inbox over the wire.
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(accept.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // B validated the signature (resolving alice's key from A's actor doc) and stored the activity.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(accept.Id!), out var stored),
            "B should have stored the activity delivered by A's worker");
        Assert.NotNull(stored);
        Assert.Equal(accept.Id, stored!.Id);
        Assert.IsType<Accept>(stored);
    }

    // --- The worker resolves the recipient's inbox from the actor IRI ----------------

    [Fact]
    public async Task Worker_DeliverToActor_ResolvesInboxAndDelivers()
    {
        using var worker = BuildDeliveryWorker(AliceActorIri, _aliceKey, _b);

        // DeliverToActorAsync derives bob's inbox from bob's actor IRI and delivers there.
        var create = BuildCreate(AliceActorIri);
        await worker.Service.DeliverToActorAsync(BobActorIri, create);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
    }

    // --- F-01: the recipient's advertised endpoints.sharedInbox is honored end-to-end ---------
    //
    // Topology: instance B (b.domain.local) hosts bob, who advertises endpoints.sharedInbox =
    // https://b.domain.local/ap/v1/shared-inbox, and serves that shared inbox (a local actor). A's
    // delivery service resolves bob's shared inbox from B's live actor document (via the fetcher wired
    // to B) and delivers to it — not to bob's per-actor inbox. This is the full serve→resolve→deliver
    // loop, so it doubles as the proof that the shared inbox is advertised on the actor document.

    [Fact]
    public async Task DeliverToActor_RemoteAdvertisesSharedInbox_ResolvedFromRemoteDocument()
    {
        // B hosts bob, who advertises endpoints.sharedInbox = https://b.domain.local/ap/v1/shared-inbox.
        var bPersistence = new InMemoryPersistenceProvider();
        var bSharedInbox = new Iri($"https://{BHost}/ap/v1/shared-inbox");
        var bobIri = TestSeeder.SeedPersonWithSharedInbox(bPersistence, BHost, Bob, bSharedInbox);
        using var bServer = StartServer(BHost, Bob, bPersistence);

        // A's delivery service resolves bob's delivery target by fetching bob's document from B's live
        // actor-document endpoint (the fetcher is wired to B). It must land on bob's advertised shared
        // inbox, not bob's per-actor inbox.
        var fetcher = BuildFetcherFor(AHost, Alice, _aliceKey, targetServer: bServer);
        var queue = new InMemoryDeliveryQueue();
        var service = new DeliveryService(queue, fetcher, NullLogger<DeliveryService>.Instance);

        var create = BuildCreate(AliceActorIri);
        await service.DeliverToActorAsync(bobIri, create);
        Assert.Equal(1, queue.Count);
        Assert.Equal(bSharedInbox, (await queue.TryDequeueAsync())!.InboxIri);
    }

    // --- F-01: an instance with a configured shared inbox advertises endpoints.sharedInbox ------

    [Fact]
    public async Task ActorDocument_InstanceHasSharedInbox_AdvertisesEndpointsSharedInbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(persistence, BHost, Bob);
        var sharedInbox = new Iri($"https://{BHost}/ap/v1/shared-inbox");
        using var server = StartServer(BHost, Bob, persistence, sharedInboxIri: sharedInbox);

        var client = server.CreateClient();
        using var response = await client.GetAsync($"/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(body)!;

        Assert.NotNull(doc.Endpoints);
        var shared = (doc.Endpoints as Endpoints)?.SharedInbox;
        Assert.NotNull(shared);
        Assert.Equal(sharedInbox.Value, shared.AbsoluteUri);
    }

    // --- A delivery to an unknown inbox is dropped (logged), not crashing the worker --

    [Fact]
    public async Task Worker_DeliveryToUnknownInbox_IsDroppedNotThrown()
    {
        using var worker = BuildDeliveryWorker(AliceActorIri, _aliceKey, _b);

        // B has no actor "nobody", so the inbox endpoint returns 404. The worker logs the non-2xx and
        // continues (it does not throw, does not re-queue).
        var nobodyInbox = new Iri($"https://{BHost}/ap/v1/u/nobody/inbox");
        var note = BuildAccept(AliceActorIri, nobodyInbox);
        await worker.Service.DeliverAsync(nobodyInbox, note);

        await worker.StartAsync(CancellationToken.None);
        // Give the worker a moment to process the (failing) delivery and drain the queue.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, worker.Queue.Count);
        await worker.StopAsync(CancellationToken.None);

        // Nothing was stored (the inbox was unknown → 404 → dropped).
        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(note.Id!), out _));
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// A hosted <see cref="DeliveryWorker"/> (signed as the instance actor, routing deliveries to the
    /// target server). Exposes the worker's <see cref="IDeliveryService"/> and
    /// <see cref="IDeliveryQueue"/> and starts/stops the worker via a minimal host.
    /// </summary>
    private sealed class TestWorker : IDisposable
    {
        private readonly IHost _host;
        private readonly DeliveryWorker _worker;

        public TestWorker(
            IHost host, DeliveryWorker worker, IDeliveryService service, IDeliveryQueue queue)
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
    private static TestWorker BuildDeliveryWorker(
        Iri actorIri, KeyPair key, TestServer targetServer)
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

        // A minimal host that runs just the delivery worker as a hosted service.
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
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, optionally
    /// overriding the <see cref="IActorDocumentFetcher"/> (for the federation wiring) and the instance's
    /// shared inbox (F-01, so local documents advertise <c>endpoints.sharedInbox</c>).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null, Iri? sharedInboxIri = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            SharedInboxIri = sharedInboxIri,
            RegisterLocalKey = false,
        });

    private static Accept BuildAccept(Iri actorIri, Iri objectIri) => new()
    {
        Id = $"https://{AHost}/activities/accept-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    private static Activity BuildCreate(Iri actorIri) => new Create
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["hello"] }],
    };

    /// <summary>
    /// Awaits until <paramref name="probe"/> returns true or the timeout elapses.
    /// </summary>
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
