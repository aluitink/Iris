using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
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
/// Phase 11 Slice 11.7 end-to-end test (gap J-18 — outbound <see cref="Create"/> to the author's remote
/// followers): a local author (alice on instance A) posts a note to her own inbox; the server records it
/// in alice's outbox (J-8, the local-surfacing half) <em>and</em> federates the <see cref="Create"/> to
/// alice's remote follower (erin on instance C) — over the wire, signed as alice. This closes the last
/// piece of the write path: a local post now not only surfaces in the author's own feed but also reaches
/// a remote follower's instance (where it is signature-validated and stored).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, author <c>alice</c>) and instance C (c.domain.local, follower
/// <c>erin</c>). The follow edge erin→alice is recorded in A's persistence (A is the authoritative home of
/// alice's follower set — the client cannot enumerate it, which is exactly why federation happens
/// server-side). A's <see cref="DeliveryWorker"/> is wired (via the <c>Func&lt;HttpMessageHandler&gt;</c>
/// transport seam) to route to C's <c>TestServer</c> and signs as alice; C's fetcher is wired to A so C
/// validates the delivered <see cref="Create"/> by resolving alice's key from A's actor document.
/// </remarks>
[Collection("PostFederation")]
public sealed class PostFederationIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string CHost = "c.domain.local";
    internal const string Alice = "alice";
    internal const string Erin = "erin";

    private readonly PostFederationSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _cPersistence;
    private KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _aliceInboxIri;
    private readonly Iri _erinActorIri;

    public PostFederationIntegrationTests(PostFederationSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _cPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _aliceInboxIri = _aliceActorIri.InboxOf();
        _erinActorIri = new Iri($"https://{CHost}/ap/v1/u/{Erin}");
        _aliceKey = null!;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _cPersistence);

        _aPersistence.Keys.TryGetKey(new Iri($"{_aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Restores alice (on A) + erin (on C) with their existing keys and the erin→alice follow edge in
    /// A's persistence.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider cPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var erinIri = new Iri($"https://{CHost}/ap/v1/u/{Erin}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(cPersistence, CHost, Erin, new Iri($"{erinIri.Value}#key-1"));
        aPersistence.Follows.RecordFollowAsync(erinIri, aliceIri).GetAwaiter().GetResult();
    }

    // --- A local post is federated to the author's remote follower --------------------------

    [Fact]
    public async Task LocalPost_IsFederatedToRemoteFollowerInbox_SignedAsAuthor()
    {
        // alice (A) posts a Create to her own inbox (the "local post" path the client uses).
        var create = BuildCreate(_aliceActorIri);

        // A's server processes the inbound Create: records it in alice's outbox (J-8) and schedules the
        // federated delivery to erin's inbox (J-18), signed as alice. Deliver the Create to A's inbox over
        // the wire via A's own delivery worker (signed as alice) so the full inbound pipeline runs.
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _fixture.ServerA);
        await worker.Service.DeliverAsync(_aliceInboxIri, create);
        Assert.Equal(1, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait on the EFFECT of the federation (C storing the Create), not on A's storage: A's inbox
        // processor stores the activity before dispatching it to the handler, so "stored on A" is not a
        // sufficient signal that the handler ran and scheduled the outbound delivery.
        await WaitForAsync(async () =>
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // (J-8) A recorded the post in alice's own outbox — the local-surfacing half.
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(_aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // (J-18) C validated the federated Create (resolving alice's key from A's actor doc) and stored it
        // — the post reached the remote follower's instance.
        Assert.True(
            await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var stored),
            "C should have stored the Create federated by A's worker (signed as alice)");
        Assert.NotNull(stored);
        Assert.Equal(create.Id, stored!.Id);
        Assert.IsType<Create>(stored);
    }

    // --- A post with no remote followers is surfaced locally but not federated --------------

    [Fact]
    public async Task LocalPost_WithNoRemoteFollowers_IsSurfacedLocallyOnly()
    {
        // A fresh author (dave) with no followers posts a Create; it is recorded in dave's outbox but
        // nothing is scheduled for delivery (no followers).
        var aPersistence = new InMemoryPersistenceProvider();
        var daveSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "dave");
        var daveActorIri = daveSeeded.ActorIri;
        var daveInboxIri = daveActorIri.InboxOf();

        TestServer? daveServer = null;
        using var a = StartAuthorServer(
            aPersistence, daveSeeded.Key, daveActorIri,
            targetServer: () => _fixture.ServerB, selfServer: () => daveServer!);
        daveServer = a;
        using var worker = BuildDeliveryWorker(daveActorIri, daveSeeded.Key, a);
        var create = BuildCreate(daveActorIri);

        await worker.Service.DeliverAsync(daveInboxIri, create);
        await worker.StartAsync(CancellationToken.None);
        // Wait for dave's outbox to surface the post (the handler ran).
        await WaitForAsync(async () =>
            (await aPersistence.Activities.GetOutboxAsync(daveActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == create.Id),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // Surfaced in dave's outbox ...
        Assert.Contains(
            await aPersistence.Activities.GetOutboxAsync(daveActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        // ... and nothing was federated (no followers) — C stored nothing.
        Assert.False(await _cPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
    }

    // --- Helpers ---------------------------------------------------------------------------

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
    /// routes to <paramref name="targetServer"/> — i.e. C's fetcher reaches A's actor documents.
    /// </summary>
    internal static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, TestServer targetServer)
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
            targetServer.CreateHandler());

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Starts the author's instance (A), registering the author's key so the host's outbound
    /// <see cref="DeliveryWorker"/> can sign as the author, routing the host's delivery transport to the
    /// (deferred) remote instance C's <c>TestServer</c>, and wiring a self-fetcher so A can validate an
    /// inbound <see cref="Create"/> signed by the author (the author's key is resolved by fetching the
    /// author's own actor document from A's own <c>TestServer</c> — deferred, since A is created first).
    /// </summary>
    internal static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> targetServer, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        // Derive the host/handle from the author's actor IRI (https://{host}/ap/v1/u/{handle}).
        var uri = new Uri(authorActorIri.Value);
        var host = uri.Authority;
        var handle = uri.AbsolutePath.Trim('/').Split('/').Last();

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            // Route A's outbound deliveries to C's in-process TestServer. The target is resolved lazily
            // (inside the handler's SendAsync) because A's host DeliveryWorker resolves its transport once
            // at startup — which is before C is created (C is created after A).
            DeliveryTransport = () => new LazyHandler(() => targetServer().CreateHandler()),
            // A's inbound key resolver fetches the author's actor doc to validate a Create signed by the
            // author. In this single-instance test that doc lives on A itself, so the fetcher reaches A's
            // own in-process TestServer (deferred: A is still null while its initializer runs).
            Fetcher = BuildSelfFetcher(authorKey, authorActorIri, selfServer),
        });
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> that reaches the author's own instance (A) so A can
    /// validate a <see cref="Create"/> signed by the author (resolving the author's key from A's actor doc).
    /// </summary>
    internal static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair authorKey, Iri authorActorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers to a <see cref="TestServer"/> created after this
    /// handler (chicken-and-egg: the server's own fetcher must reach the in-process server, which does not
    /// exist yet while the server is being constructed). Wraps the inner handler in an <see cref="HttpClient"/>
    /// (whose <c>SendAsync</c> is public) and clones the request (the in-process transport does not clone
    /// between sends).
    /// </summary>
    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, registering the
    /// instance actor's key (so the host's <see cref="DeliveryWorker"/> can sign) and overriding the
    /// <see cref="IActorDocumentFetcher"/> (for the federation wiring).
    /// </summary>
    internal static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair instanceKey, IActorDocumentFetcher? fetcher = null)
    {
        var instanceActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(instanceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, instanceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
        });
    }

    private static Create BuildCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["federated post"] },
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
/// Shared two-host fixture for <see cref="PostFederationIntegrationTests"/> (A: a.domain.local alice,
/// B: c.domain.local erin). Seeds alice + erin with keys ONCE; A's outbound delivery routes to B (so the
/// federated Create reaches erin's inbox), A's fetcher is a self-fetcher (validates the inbound Create
/// signed by alice); B's fetcher reaches A (validates the delivered Create); the erin→alice follow edge
/// is recorded in A's persistence.
/// </summary>
public sealed class PostFederationSharedHost : SharedTwoHostFixture
{
    public PostFederationSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var cPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, PostFederationIntegrationTests.AHost, PostFederationIntegrationTests.Alice);
        var cSeeded = TestSeeder.SeedPersonWithKey(cPersistence, PostFederationIntegrationTests.CHost, PostFederationIntegrationTests.Erin);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(cPersistence);

        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aSeeded.Key);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var aSigner = new HttpSignatureSigner(aKeyStore);

        var optionsA = new ActivityPubHostOptions
        {
            Host = PostFederationIntegrationTests.AHost,
            Handle = PostFederationIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(aKeyStore, aKeyProvider, aSigner),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = PostFederationIntegrationTests.BuildSelfFetcher(
                aSeeded.Key, aSeeded.ActorIri, () => serverARef()),
        };

        var cKeyStore = new InMemoryKeyStore();
        cKeyStore.PutKey(cSeeded.Key);
        var cKeyProvider = new InMemoryKeyProvider(cKeyStore);
        cKeyProvider.RegisterKey(cSeeded.ActorIri, cSeeded.Key.KeyId);
        var cSigner = new HttpSignatureSigner(cKeyStore);

        var optionsB = new ActivityPubHostOptions
        {
            Host = PostFederationIntegrationTests.CHost,
            Handle = PostFederationIntegrationTests.Erin,
            Persistence = cPersistence,
            IdentityKeys = new IdentityKeys(cKeyStore, cKeyProvider, cSigner),
            Fetcher = BuildFetcherForLazy(cSeeded.Key, cSeeded.ActorIri, serverARef),
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// Builds a fetcher whose client (signed as erin) routes to the (deferred) A's
    /// <c>TestServer</c> — i.e. C's fetcher reaches A's actor documents (lazily).
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherForLazy(
        KeyPair key, Iri actorIri, Func<TestServer> targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => targetServer().CreateHandler()));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>
/// xunit collection definition for the post-federation shared two-host fixture.
/// </summary>
[CollectionDefinition("PostFederation")]
public sealed class PostFederationCollection : ICollectionFixture<PostFederationSharedHost>
{
}
