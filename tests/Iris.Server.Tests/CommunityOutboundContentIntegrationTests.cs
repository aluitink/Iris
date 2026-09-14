using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.6 end-to-end test (outbound federation <em>from</em> an Iris community, Iris → remote peer):
/// a local community member (bob on instance B) posts a note <em>attributed to</em> his community
/// (<c>iris</c>) — the community-<c>attributedTo</c> post. The post federates to the member's remote
/// follower (alice on instance A) and, on A, two things happen:
/// <list type="number">
/// <item>The federated <see cref="Create"/> is stored <em>with the community's <c>attributedTo</c>
/// intact</em> — the posting community survives the wire as a first-class identity, not an opaque IRI
/// (the outbound half of community federation, gap (a)).</item>
/// <item>The receiving instance <em>fetches + persists the posting community's actor document</em> (a
/// <see cref="Group"/>), so A can resolve <c>iris</c>'s name/icon for display (gap (b) — the community's
/// provenance, which the signature path alone never resolves: it only ever saw the signing member).</item>
/// </list>
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, follower <c>alice</c>) and instance B (b.domain.local, member
/// <c>bob</c> + community <c>iris</c>, of which bob is a member). The follow edge alice→bob is recorded in
/// B's persistence (B is the home of bob's follower set). Bob posts a community-attributed
/// <see cref="Create"/> to his own inbox on B; B's <see cref="CreateActivityHandler"/> (person branch)
/// records it in bob's outbox and federates it to bob's remote follower alice (A), signed as bob. A
/// validates the signature (fetching bob's actor doc from B) and stores the <see cref="Create"/> — and,
/// because the embedded note's <c>attributedTo</c> is the remote community <c>iris</c>, A's
/// <see cref="CreateActivityHandler"/> fetches + persists <c>iris</c>'s <see cref="Group"/> document via
/// A's <see cref="IActorDocumentFetcher"/> (wired to B, with a <see cref="RemoteCommunityPersister"/> over
/// A's community store, mirroring the production fetcher). The community's identity (name/icon) is then
/// resolvable on A.
/// </remarks>
[Collection("CommunityOutboundContent")]
public sealed class CommunityOutboundContentIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string BHost = "b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Community = "iris";

    private readonly CommunityOutboundContentSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;
    private readonly Iri _aliceActorIri;
    private readonly Iri _communityIri;

    public CommunityOutboundContentIntegrationTests(CommunityOutboundContentSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _bobInboxIri = _bobActorIri.InboxOf();
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _communityIri = new Iri($"https://{BHost}/ap/v1/c/{Community}");
        _bobKey = null!;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);

        _bPersistence.Keys.TryGetKey(new Iri($"{_bobActorIri.Value}#key-1"), out var bobKey);
        _bobKey = (KeyPair)bobKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Restores alice (on A) + bob (on B) + the community <c>iris</c> (on B, with bob as its member) with
    /// their existing keys, and the alice→bob follow edge in B's persistence (B is the home of bob's
    /// follower set).
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var communityIri = new Iri($"https://{BHost}/ap/v1/c/{Community}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
        TestSeeder.SeedCommunityWithExistingKey(bPersistence, BHost, Community, new Iri($"{communityIri.Value}#key-1"));
        // bob (B) is a member of the community iris (the community-attributed-post author).
        bPersistence.Communities.AddMemberAsync(communityIri, bobIri).GetAwaiter().GetResult();
        // alice (A) follows bob (B): the follow edge is recorded in B's persistence (the home of bob's
        // follower set), so bob's community-attributed post federates to alice.
        bPersistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
    }

    // --- A community-attributed post federates with the community identity intact -------------

    [Fact]
    public async Task MemberCommunityAttributedPost_FederatesToRemoteFollower_WithCommunityAttributedToIntact()
    {
        // bob (B) posts a note attributed to his community iris, to his own inbox on B (the local-post
        // path the client uses). B's CreateActivityHandler (person branch) records it in bob's outbox and
        // federates it to bob's remote follower alice (A), signed as bob.
        var create = BuildCommunityAttributedCreate(_bobActorIri, _communityIri);

        using var worker = BuildDeliveryWorker(_bobActorIri, _bobKey, _fixture.ServerB);
        await worker.Service.DeliverAsync(_bobInboxIri, create);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the federation (A storing the Create), not on B's storage: B's inbox
        // processor stores the activity before dispatching it to the handler, so "stored on B" is not a
        // sufficient signal that the handler ran and scheduled the outbound delivery.
        await WaitForAsync(async () =>
            await _aPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // (a) A validated the federated Create (resolving bob's key from B's actor doc) and stored it —
        // the community-attributed post reached the remote follower's instance.
        Assert.True(
            await _aPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var stored),
            "A should have stored the community-attributed Create federated by B's worker (signed as bob)");
        Assert.NotNull(stored);
        Assert.IsType<Create>(stored);

        // (a, the community-identity half) the embedded note's attributedTo — the posting community
        // iris — survived the wire intact: the remote instance knows the post is a community post, not
        // an opaque IRI.
        var embedded = ((Create)stored!).ExtractEmbeddedObject() as IObject;
        Assert.NotNull(embedded);
        var attributedTo = embedded!.AttributedTo?.FirstOrDefault()?.ResolveObjectIri();
        Assert.Equal(_communityIri.Value, attributedTo?.Value);
    }

    // --- The receiving instance fetches + persists the posting community's document ----------

    [Fact]
    public async Task MemberCommunityAttributedPost_RemoteInstance_PersistsPostingCommunityDocument()
    {
        // bob (B) posts a note attributed to his community iris, federated to alice (A).
        var create = BuildCommunityAttributedCreate(_bobActorIri, _communityIri);

        using var worker = BuildDeliveryWorker(_bobActorIri, _bobKey, _fixture.ServerB);
        await worker.Service.DeliverAsync(_bobInboxIri, create);
        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the federation (A storing the Create), then on the community-identity
        // effect (A persisting the posting community's Group document). The latter is the Phase 136.6
        // community-provenance half: A's CreateActivityHandler fetches + persists the remote community's
        // actor document (the signature path alone never resolves it — it only saw the signing member).
        await WaitForAsync(async () =>
            await _aPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        await WaitForAsync(async () =>
            await _aPersistence.Communities.TryGetCommunityAsync(_communityIri, out _, CancellationToken.None),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // (b) A persisted the posting community iris (a Group) to its durable community store — the
        // community's identity (name/icon) is now resolvable on A for display. The fetch went through
        // A's IActorDocumentFetcher (wired to B, with a RemoteCommunityPersister over A's community
        // store, mirroring the production fetcher).
        Assert.True(
            await _aPersistence.Communities.TryGetCommunityAsync(_communityIri, out var persisted),
            "A should have persisted the posting community's Group document (community provenance)");
        Assert.NotNull(persisted);
        // The persisted community's IRI matches the one the post attributed to (the same iris).
        Assert.Equal(_communityIri.Value, persisted!.Id);
    }

    // --- Helpers ----------------------------------------------------------------------------

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
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
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
    /// routes to the (deferred) <paramref name="targetServer"/> — i.e. the instance's fetcher reaches the
    /// other instance's actor documents. When <paramref name="communityStore"/> is provided, fetched
    /// remote communities (Group) are also persisted to that store (mirroring the production fetcher's
    /// <see cref="RemoteCommunityPersister"/> wiring).
    /// </summary>
    public static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, Func<TestServer> targetServer,
        ICommunityStore? communityStore = null, Iri? instanceBase = null)
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
            new LazyHandler(() => targetServer().CreateHandler()));

        RemoteCommunityPersister? communityPersister = null;
        if (communityStore is not null)
        {
            communityPersister = new RemoteCommunityPersister(communityStore, instanceBase);
        }

        return new IrisActorDocumentFetcher(client, new RemoteActorCache(), null, communityPersister);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> that reaches the instance's own server (a
    /// self-fetcher), so the instance can validate a <see cref="Create"/> signed by one of its local
    /// actors (resolving the actor's key from its own actor doc).
    /// </summary>
    public static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair key, Iri actorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Builds a community-attributed <see cref="Create"/>: the actor (bob) posts a note whose
    /// <c>attributedTo</c> is the community (iris) — the community-<c>attributedTo</c> post.
    /// </summary>
    private static Create BuildCommunityAttributedCreate(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"https://{BHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = $"https://{BHost}/objects/note-{Guid.NewGuid():N}",
                Content = ["community post"],
                AttributedTo = [new Link { Href = new Uri(communityIri.Value) }],
            },
        ],
    };

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

/// <summary>
/// Shared two-host fixture for <see cref="CommunityOutboundContentIntegrationTests"/> (A:
/// a.domain.local alice, B: b.domain.local bob + community iris). Seeds alice + bob + iris with keys
/// ONCE; B's outbound delivery routes to A (so the federated community-attributed Create reaches alice's
/// inbox), B's fetcher is a self-fetcher (validates the inbound Create signed by bob); A's fetcher
/// reaches B (validates the federated Create AND fetches + persists the posting community's document);
/// the alice→bob follow edge is recorded in B's persistence.
/// </summary>
public sealed class CommunityOutboundContentSharedHost : SharedTwoHostFixture
{
    public CommunityOutboundContentSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, CommunityOutboundContentIntegrationTests.AHost, CommunityOutboundContentIntegrationTests.Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, CommunityOutboundContentIntegrationTests.BHost, CommunityOutboundContentIntegrationTests.Bob);
        var communityKey = TestSeeder.SeedCommunityWithKey(
            bPersistence,
            CommunityOutboundContentIntegrationTests.BHost,
            CommunityOutboundContentIntegrationTests.Community,
            bSeeded.ActorIri).Key;

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var aBaseUri = new Iri($"https://{CommunityOutboundContentIntegrationTests.AHost}");

        var optionsA = new ActivityPubHostOptions
        {
            Host = CommunityOutboundContentIntegrationTests.AHost,
            Handle = CommunityOutboundContentIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            // A's fetcher reaches B (validates the federated Create signed by bob) AND persists fetched
            // remote communities to A's community store (the posting community iris — Phase 136.6
            // community provenance), mirroring the production fetcher's RemoteCommunityPersister wiring.
            Fetcher = CommunityOutboundContentIntegrationTests.BuildFetcherFor(
                CommunityOutboundContentIntegrationTests.AHost,
                CommunityOutboundContentIntegrationTests.Alice,
                aSeeded.Key,
                serverBRef,
                communityStore: aPersistence.Communities,
                instanceBase: aBaseUri),
        };

        var bKeyStore = new InMemoryKeyStore();
        bKeyStore.PutKey(bSeeded.Key);
        bKeyStore.PutKey(communityKey);
        var bKeyProvider = new InMemoryKeyProvider(bKeyStore);
        bKeyProvider.RegisterKey(bSeeded.ActorIri, bSeeded.Key.KeyId);
        var communityIri = new Iri($"https://{CommunityOutboundContentIntegrationTests.BHost}/ap/v1/c/{CommunityOutboundContentIntegrationTests.Community}");
        bKeyProvider.RegisterKey(communityIri, communityKey.KeyId);
        var bSigner = new HttpSignatureSigner(bKeyStore);

        var optionsB = new ActivityPubHostOptions
        {
            Host = CommunityOutboundContentIntegrationTests.BHost,
            Handle = CommunityOutboundContentIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = new IdentityKeys(bKeyStore, bKeyProvider, bSigner),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            // B's fetcher is a self-fetcher (reaches B) so B can validate a Create signed by bob (its
            // local actor) — the local-post path.
            Fetcher = CommunityOutboundContentIntegrationTests.BuildSelfFetcher(
                bSeeded.Key, bSeeded.ActorIri, serverBRef),
        };

        return (optionsA, optionsB);
    }
}

/// <summary>
/// xunit collection definition for the community-outbound-content shared two-host fixture.
/// </summary>
[CollectionDefinition("CommunityOutboundContent")]
public sealed class CommunityOutboundContentCollection : ICollectionFixture<CommunityOutboundContentSharedHost>
{
}
