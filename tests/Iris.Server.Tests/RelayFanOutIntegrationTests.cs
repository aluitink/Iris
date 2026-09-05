using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.19 integration test (F-06 relay — <strong>fan-out</strong>, the delivery half):
/// when a local actor posts content, that content is delivered not only to the author's remote
/// followers but also to each <c>relay</c> the author has subscribed to (a <c>star</c>-subscribed
/// fan-out server, AP §5.1.3). This is the end-to-end proof that a local post reaches a relay over the
/// wire — the complement of Slice 12.18 (the relay <em>subscription</em> half: the local
/// <c>relays</c>/<c>star</c> collection, covered by <see cref="RelaysCollectionIntegrationTests"/>).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts the local author <c>alice</c>, who has subscribed to a
/// relay (the <c>relay</c> application on instance R, relay1.example.com). Alice posts a <c>Create</c> to
/// her own inbox; A's <see cref="CreateActivityHandler"/> records the post in alice's outbox (J-8)
/// <em>and</em> fans it out to the relay (F-06). A's host <see cref="DeliveryWorker"/> (a hosted service
/// that starts with the host) POSTs the <c>Create</c> to the relay's inbox, signed as alice; R validates
/// that delivery (resolving alice's key from A's actor document) and stores the <c>Create</c> in R's
/// activity store. This mirrors the topology of <see cref="PostFederationIntegrationTests"/> (the J-18
/// remote-follower federation), except the target is a relay rather than a follower.
/// <para>
/// A's outbound <c>IActorDocumentFetcher</c> is a <see cref="DelegatingFetcher"/>: it reaches the relay's
/// actor document (on R's in-process <c>TestServer</c>) to resolve the relay's delivery target, and every
/// other actor (alice, for inbound signature validation) is resolved from A's own in-process
/// <c>TestServer</c>. A's outbound delivery transport routes to R's <c>TestServer</c>.
/// </para>
/// </remarks>
[Collection("RelayFanOut")]
public sealed class RelayFanOutIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "a.domain.local";
    internal const string RelayHost = "relay1.example.com";
    internal const string Alice = "alice";
    internal const string Relay = "relay";

    private readonly RelayFanOutSharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _relayPersistence;

    public RelayFanOutIntegrationTests(RelayFanOutSharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _relayPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _relayPersistence);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Restores alice (on A) + relay (on B) with their existing keys and the alice→relay subscription
    /// edge in A's relay store.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider relayPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var relayIri = new Iri($"https://{RelayHost}/ap/v1/u/{Relay}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(relayPersistence, RelayHost, Relay, new Iri($"{relayIri.Value}#key-1"));
        aPersistence.Relays.RecordRelayAsync(aliceIri, relayIri).GetAwaiter().GetResult();
    }

    // --- A local post is fanned out to the subscribed relay (signed as the author) -------------

    [Fact]
    public async Task LocalPost_IsFannedOutToRelay_SignedAsAuthor()
    {
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var create = BuildCreate(aliceActorIri);

        // alice (A) posts a Create to her own inbox (the "local post" path the client uses). A's server
        // records it in alice's outbox (J-8) and fans it out to the relay (F-06), signed as alice. A's
        // host DeliveryWorker POSTs the Create to the relay's inbox over the wire.
        var status = await PostToInboxAsync(_fixture.ServerA, aliceActorIri, create);
        Assert.Equal(202, status.StatusCode);

        // Wait on the EFFECT of the fan-out (R storing the Create), not on A's storage: A's inbox
        // processor stores the activity before dispatching it to the handler, so "stored on A" is not a
        // sufficient signal that the handler ran and scheduled the relay fan-out.
        await WaitForAsync(async () =>
            await _relayPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));

        // (J-8) A recorded the post in alice's own outbox — the local-surfacing half.
        Assert.Contains(
            await _aPersistence.Activities.GetOutboxAsync(aliceActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // (F-06) R validated the fanned-out Create (resolving alice's key from A's actor doc) and stored
        // it — the post reached the relay's instance.
        Assert.True(
            await _relayPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out var stored),
            "R should have stored the Create fanned out by A's worker (signed as alice)");
        Assert.NotNull(stored);
        Assert.Equal(create.Id, stored!.Id);
        Assert.IsType<Create>(stored);
    }

    // --- A boost (Announce) is fanned out to the subscribed relay (signed as the announcer) ----

    [Fact]
    public async Task LocalBoost_IsFannedOutToRelay_SignedAsAnnouncer()
    {
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var noteIri = new Iri($"https://{AHost}/objects/note-{Guid.NewGuid():N}");
        var announce = new Announce
        {
            Id = $"https://{AHost}/activities/announce-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(aliceActorIri.Value) }],
            Object = [new Link { Href = noteIri.Uri }],
        };

        // alice (A) boosts a note (posts an Announce to her own inbox). A's server records it in alice's
        // outbox and fans the boost out to the relay (F-06), signed as alice. A's host DeliveryWorker
        // POSTs the Announce to the relay's inbox over the wire.
        var status = await PostToInboxAsync(_fixture.ServerA, aliceActorIri, announce);
        Assert.Equal(202, status.StatusCode);

        // Wait on the EFFECT of the fan-out (R storing the Announce).
        await WaitForAsync(async () =>
            await _relayPersistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out _),
            timeout: TimeSpan.FromSeconds(10));

        // (F-06) R validated the fanned-out Announce (resolving alice's key from A's actor doc) and
        // stored it — the boost reached the relay's instance.
        Assert.True(
            await _relayPersistence.Activities.TryGetActivityAsync(new Iri(announce.Id!), out var stored),
            "R should have stored the Announce fanned out by A's worker (signed as alice)");
        Assert.NotNull(stored);
        Assert.Equal(announce.Id, stored!.Id);
        Assert.IsType<Announce>(stored);
    }

    // --- A post with no subscribed relays is not fanned out ------------------------------------

    [Fact]
    public async Task LocalPost_WithNoSubscribedRelays_IsNotFannedOut()
    {
        // A fresh author (bob) with no subscribed relays posts a Create. The relay store is empty, so the
        // only delivery is the local post to bob's own inbox (J-8) — no relay fan-out.
        var bobPersistence = new InMemoryPersistenceProvider();
        var bobSeeded = TestSeeder.SeedPersonWithKey(bobPersistence, AHost, "bob");
        var bobActorIri = bobSeeded.ActorIri;

        TestServer? bobServer = null;
        using var a = StartAuthorServer(
            bobPersistence, bobSeeded.Key, bobActorIri,
            relayServer: () => _fixture.ServerB, selfServer: () => bobServer!,
            relayActorIri: new Iri($"https://{RelayHost}/ap/v1/u/{Relay}"));
        bobServer = a;

        var create = BuildCreate(bobActorIri);
        var status = await PostToInboxAsync(a, bobActorIri, create);
        Assert.Equal(202, status.StatusCode);

        // Wait for bob's outbox to surface the post (the handler ran).
        await WaitForAsync(async () =>
            (await bobPersistence.Activities.GetOutboxAsync(bobActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == create.Id),
            timeout: TimeSpan.FromSeconds(10));

        // Surfaced in bob's outbox ...
        Assert.Contains(
            await bobPersistence.Activities.GetOutboxAsync(bobActorIri),
            o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        // ... and nothing was fanned out to the relay (bob is not subscribed) — R stored nothing.
        Assert.False(await _relayPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _));
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Posts <paramref name="activity"/> to the actor's inbox via a client that signs as the actor (the
    /// "local post" path the client uses), routed to the in-process <paramref name="server"/>. Returns the
    /// HTTP status (202 Accepted when the full inbound pipeline — signature validation + handler — ran).
    /// </summary>
    private static async Task<DeliveryResult> PostToInboxAsync(TestServer server, Iri actorIri, Activity activity)
    {
        var keyStore = server.Services.GetRequiredService<IKeyStore>();
        var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
        var signer = server.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            server.CreateHandler());
        return await client.DeliverAsync(actorIri.InboxOf(), activity);
    }

    /// <summary>
    /// Starts the author's instance (A), registering the author's key so the host's outbound
    /// <see cref="DeliveryWorker"/> can sign as the author, routing the host's delivery transport to the
    /// (deferred) relay's <c>TestServer</c>, and wiring a <see cref="DelegatingFetcher"/> so A resolves
    /// the relay's delivery target (the relay's actor doc, on R) and validates an inbound
    /// <see cref="Create"/> signed by the author (the author's key, from A's own actor document).
    /// </summary>
    internal static TestServer StartAuthorServer(
        InMemoryPersistenceProvider persistence, KeyPair authorKey, Iri authorActorIri,
        Func<TestServer> relayServer, Func<TestServer> selfServer, Iri relayActorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(authorActorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var uri = new Uri(authorActorIri.Value);
        var host = uri.Authority;
        var handle = uri.AbsolutePath.Trim('/').Split('/').Last();

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var selfClient = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        var relayClient = factory.Create(
            new ActivityPubClientOptions { ActorId = authorActorIri, EnableRetry = false },
            new LazyHandler(() => relayServer().CreateHandler()));

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            // Route A's outbound deliveries to the relay's in-process TestServer. The target is resolved
            // lazily (inside the handler's SendAsync) because A's host DeliveryWorker resolves its
            // transport once at startup — which is before the relay is created (created after A).
            DeliveryTransport = () => new LazyHandler(() => relayServer().CreateHandler()),
            // Resolve the relay's delivery target from the relay's actor doc (on R), and every other
            // actor (alice, for inbound validation) from A's own actor doc (on A).
            Fetcher = new DelegatingFetcher(
                relayActorIri,
                new IrisActorDocumentFetcher(relayClient, new RemoteActorCache()),
                new IrisActorDocumentFetcher(selfClient, new RemoteActorCache())),
        });
    }

    /// <summary>
    /// Starts the relay's instance (R), registering the relay's key. Its fetcher reaches A so R can
    /// validate the fanned-out <see cref="Create"/> (resolving alice's key from A's actor doc).
    /// </summary>
    internal static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, KeyPair key,
        IActorDocumentFetcher fetcher)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
        });
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as <paramref name="handle"/>)
    /// routes to <paramref name="targetServer"/> — i.e. the relay's fetcher reaches A's actor documents.
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

    private static Create BuildCreate(Iri actorIri) => new()
    {
        Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note { Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}", Content = ["relay post"] },
        ],
    };

    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that delegates the relay's actor IRI to one fetcher (the
    /// relay's document, on the relay's instance) and every other actor to another (the local actor's
    /// document, on the author's own instance). This lets the author's <see cref="DeliveryService"/>
    /// resolve the relay's delivery target while the author's inbound key resolver still resolves the
    /// author's own key.
    /// </summary>
    internal sealed class DelegatingFetcher(
        Iri relayActorIri, IActorDocumentFetcher relay, IActorDocumentFetcher self)
        : IActorDocumentFetcher
    {
        private readonly Iri _relayActorIri = relayActorIri;
        private readonly IActorDocumentFetcher _relay = relay;
        private readonly IActorDocumentFetcher _self = self;

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => actorIri == _relayActorIri
                ? _relay.GetActorAsync(actorIri, ct)
                : _self.GetActorAsync(actorIri, ct);
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="RelayFanOutIntegrationTests"/> (A: a.domain.local alice,
/// B: relay1.example.com relay). Seeds alice + relay with keys ONCE; A's outbound delivery routes to B
/// (the relay's inbox), A's fetcher is a <see cref="RelayFanOutIntegrationTests.DelegatingFetcher"/>
/// (relay doc from B, self from A), B's fetcher reaches A (validates the fanned-out Create); the
/// alice→relay subscription edge is recorded in A's relay store.
/// </summary>
public sealed class RelayFanOutSharedHost : SharedTwoHostFixture
{
    public RelayFanOutSharedHost()
        : base(BuildOptions())
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var relayPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, RelayFanOutIntegrationTests.AHost, RelayFanOutIntegrationTests.Alice);
        var relaySeeded = TestSeeder.SeedPersonWithKey(relayPersistence, RelayFanOutIntegrationTests.RelayHost, RelayFanOutIntegrationTests.Relay);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(relayPersistence);

        // A's options: reuse StartAuthorServer's wiring (DelegatingFetcher, DeliveryTransport to B, identity).
        // Build the options directly (StartAuthorServer creates a TestServer; we need options for the fixture).
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aSeeded.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aSeeded.ActorIri, aSeeded.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var selfClient = factory.Create(
            new ActivityPubClientOptions { ActorId = aSeeded.ActorIri, EnableRetry = false },
            new LazyHandler(() => serverARef().CreateHandler()));
        var relayClient = factory.Create(
            new ActivityPubClientOptions { ActorId = aSeeded.ActorIri, EnableRetry = false },
            new LazyHandler(() => serverBRef().CreateHandler()));
        var relayActorIri = relaySeeded.ActorIri;

        var optionsA = new ActivityPubHostOptions
        {
            Host = RelayFanOutIntegrationTests.AHost,
            Handle = RelayFanOutIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
            Fetcher = new RelayFanOutIntegrationTests.DelegatingFetcher(
                relayActorIri,
                new IrisActorDocumentFetcher(relayClient, new RemoteActorCache()),
                new IrisActorDocumentFetcher(selfClient, new RemoteActorCache())),
        };

        var relayKeyStore = new InMemoryKeyStore();
        relayKeyStore.PutKey(relaySeeded.Key);
        var relayKeyProvider = new InMemoryKeyProvider(relayKeyStore);
        relayKeyProvider.RegisterKey(relayActorIri, relaySeeded.Key.KeyId);
        var relaySigner = new HttpSignatureSigner(relayKeyStore);

        var optionsB = new ActivityPubHostOptions
        {
            Host = RelayFanOutIntegrationTests.RelayHost,
            Handle = RelayFanOutIntegrationTests.Relay,
            Persistence = relayPersistence,
            IdentityKeys = new IdentityKeys(relayKeyStore, relayKeyProvider, relaySigner),
            Fetcher = BuildFetcherForLazy(relaySeeded.Key, relayActorIri, serverARef),
        };

        return (optionsA, optionsB);
    }

    /// <summary>
    /// Builds a fetcher whose client (signed as the relay) routes to the (deferred) A's
    /// <c>TestServer</c> — i.e. the relay's fetcher reaches A's actor documents (lazily).
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
/// xunit collection definition for the relay fan-out shared two-host fixture.
/// </summary>
[CollectionDefinition("RelayFanOut")]
public sealed class RelayFanOutCollection : ICollectionFixture<RelayFanOutSharedHost>
{
}
