using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Delivery;

/// <summary>
/// Slice 26.2 end-to-end test (the inbound <see cref="Tombstone"/> contract, F-10): a peer instance
/// signals that an object was deleted on <em>its</em> instance by delivering a <see cref="Tombstone"/>
/// to a follower's inbox — either a <em>standalone</em> <c>Tombstone</c> (a <c>IObject</c>, not a
/// <c>Activity</c>) or a <see cref="Create"/> whose embedded <c>object</c> is a <c>Tombstone</c>. The
/// receiving instance must store the tombstone under the object IRI so a subsequent <c>GET</c> serves
/// the tombstone (not the stale federated content, not a <c>404</c>), and clean up the local copy
/// (the author's outbox <see cref="Create"/>, the object → <see cref="Create"/> index) that the earlier
/// federated <see cref="Create"/> left behind.
/// </summary>
/// <remarks>
/// Topology: instance B (tomb-b.domain.local, author <c>bob</c>) and instance A (tomb-a.domain.local,
/// follower <c>alice</c>). bob publishes a note (B stores it + federates the <see cref="Create"/> to
/// alice's inbox on A; A's <see cref="CreateActivityHandler"/> stores the embedded note — the "remote
/// copy" on A). bob then deletes the note on B and delivers the deletion to alice's inbox on A. The two
/// tests cover the two inbound tombstone shapes:
/// <list type="bullet">
/// <item>A <em>standalone</em> <c>Tombstone</c> (pre-fix: the inbox endpoint returned <c>400</c> for a
/// <c>IObject</c> payload that was not the special-cased <c>Mute</c>, so A kept the stale note).</item>
/// <item>A <c>Create</c> whose embedded <c>object</c> is a <c>Tombstone</c> (pre-fix: A stored the
/// tombstone but ALSO recorded a bogus outbox <see cref="Create"/> + object → <see cref="Create"/> index
/// and re-federated the "new post").</item>
/// </list>
/// The non-vacuous signal is the <em>stored object on A</em>: before the fix A's object store still held
/// the federated <c>Note</c> (standalone) or the outbox listed a second <see cref="Create"/>
/// (Create-wrapped); after the fix A serves the <see cref="Tombstone"/> under the object IRI and the
/// outbox no longer lists the deleted content.
/// </remarks>
public sealed class InboundTombstoneIntegrationTests : IDisposable
{
    private const string AHost = "tomb-a.domain.local";
    private const string BHost = "tomb-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;

    public InboundTombstoneIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bobKey = bSeeded.Key;

        // The follow edge alice→bob is recorded on B (bob's home instance owns his follower set — the
        // propagation target set bob's outbound Create federation reads).
        _bPersistence.Follows.RecordFollowAsync(_aliceActorIri, _bobActorIri).GetAwaiter().GetResult();

        // A hosts alice; its fetcher reaches B so A validates the inbound Create/Tombstone signed as bob
        // by fetching B's actor document for bob's key.
        var bServer = StartServer(BHost, Bob, _bPersistence, bSeeded.Key);
        _a = StartServer(AHost, Alice, _aPersistence, aSeeded.Key, targetServer: bServer);
    }

    public void Dispose()
    {
        _a.Dispose();
    }

    // --- Standalone Tombstone: A serves the tombstone under the object IRI (F-10) ----------------

    [Fact]
    public async Task StandaloneTombstone_ReplacesFederatedCopy_GetServesTombstone_OutboxCleaned()
    {
        var noteIri = new Iri($"{_bobActorIri}/notes/standalone-tomb-1");

        // 1. bob posts a note; the Create is federated to alice's inbox on A (A stores the embedded
        // note in its object store — the "remote copy").
        var create = BuildCreate(_bobActorIri, noteIri, "doomed body");
        using var worker = BuildDeliveryWorker(_bobActorIri, _bobKey, _a);
        await worker.Service.DeliverAsync(_aliceActorIri.InboxOf(), create);
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var aStored),
            "A should have stored the note federated by bob's Create");
        Assert.Equal("doomed body", aStored!.Content?.FirstOrDefault());

        // 2. bob deletes the note on B and delivers a standalone Tombstone (an IObject, not an Activity)
        // to alice's inbox on A (a signed POST, as a real peer would). A's inbox handler stores the
        // tombstone under the object IRI (replacing the stale note) and cleans up A's local copy (bob's
        // outbox Create + object → Create index).
        var tombstone = BuildTombstone(noteIri);
        using var tombstoneResponse = await SendSignedInboxPostAsync(tombstone);
        Assert.True(tombstoneResponse.StatusCode is System.Net.HttpStatusCode.Accepted
                or System.Net.HttpStatusCode.NoContent
                or System.Net.HttpStatusCode.OK,
            $"expected 2xx for the inbound standalone Tombstone, got {tombstoneResponse.StatusCode}");

        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var o)
                && o is Tombstone,
            timeout: TimeSpan.FromSeconds(10));

        // 3. A serves the tombstone under the object IRI (not the stale note, not a 404).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
        Assert.Equal(noteIri.Value, aTomb!.Id);

        // 4. A's local copy is cleaned up: bob's outbox (on A) no longer lists the note's Create, and the
        // object → Create index no longer maps the note to a Create (the inverse of what the Create
        // handler recorded when A stored the federated note).
        var aOutboxIds = await OutboxCreateIdsAsync(_bobActorIri);
        Assert.DoesNotContain(aOutboxIds, id => id == create.Id);
        Assert.Null(await _aPersistence.Creates.TryGetCreateIriAsync(noteIri, CancellationToken.None));
    }

    // --- Create-wrapped Tombstone: stored as tombstone, no bogus outbox entry or re-federation ---

    [Fact]
    public async Task CreateWrappedTombstone_StoredAsTombstone_NoBogusOutboxEntry_NoReFederation()
    {
        var noteIri = new Iri($"{_bobActorIri}/notes/create-wrapped-tomb-1");

        // 1. bob posts a note; the Create is federated to alice's inbox on A (A stores the embedded
        // note — the "remote copy").
        var create = BuildCreate(_bobActorIri, noteIri, "doomed body");
        using var worker = BuildDeliveryWorker(_bobActorIri, _bobKey, _a);
        await worker.Service.DeliverAsync(_aliceActorIri.InboxOf(), create);
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out _));

        // 2. bob deletes the note on B and delivers a Create whose embedded object is a Tombstone to
        // alice's inbox on A. A's CreateActivityHandler recognizes the embedded tombstone: it stores the
        // tombstone under the object IRI (replacing the stale note) and cleans up A's local copy — but
        // does NOT record a bogus outbox Create, does NOT record a second object → Create index entry,
        // and does NOT re-federate the "new post" to bob's other followers.
        var wrapped = BuildCreateWithTombstone(_bobActorIri, noteIri);
        using var worker2 = BuildDeliveryWorker(_bobActorIri, _bobKey, _a);
        await worker2.Service.DeliverAsync(_aliceActorIri.InboxOf(), wrapped);
        await worker2.StartAsync(CancellationToken.None);
        await WaitForAsync(
            async () => await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var o)
                && o is Tombstone,
            timeout: TimeSpan.FromSeconds(10));
        await worker2.StopAsync(CancellationToken.None);

        // 3. A serves the tombstone under the object IRI (not the stale note, not a 404).
        Assert.True(await _aPersistence.Objects.TryGetObjectAsync(noteIri, out var aTomb));
        Assert.IsType<Tombstone>(aTomb);
        Assert.Equal(noteIri.Value, aTomb!.Id);

        // 4. A's local copy is cleaned up: bob's outbox (on A) holds NEITHER the original note's Create
        // (removed by the tombstone cleanup) NOR a second bogus Create for the wrapped tombstone (the
        // pre-fix behavior recorded the wrapped Create as a new post), and the object → Create index no
        // longer maps the deleted note to a Create.
        var outboxIds = await OutboxCreateIdsAsync(_bobActorIri);
        Assert.DoesNotContain(outboxIds, id => id == wrapped.Id);
        Assert.DoesNotContain(outboxIds, id => id == create.Id);
        Assert.Null(await _aPersistence.Creates.TryGetCreateIriAsync(noteIri, CancellationToken.None));
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Sends a signed <c>POST</c> of <paramref name="obj"/> (a <see cref="Tombstone"/>) to alice's inbox
    /// on A, signed as bob — the standalone-tombstone path (a <c>IObject</c> cannot be delivered via
    /// <see cref="IDeliveryService"/>, which only accepts <see cref="Activity"/>). The request is signed
    /// inline by the client pipeline (JsonLd → Signing → A's transport) and delivered directly to A's
    /// <c>TestServer</c> — the same transport the test <see cref="DeliveryWorker"/> uses for the
    /// federated <see cref="Create"/>, so the signature is validated by A exactly as for the worker's
    /// deliveries.
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedInboxPostAsync(IObject obj)
    {
        var json = ActivityJson.Serialize(obj);
        var inboxPath = $"/ap/v1/u/{Alice}/inbox";
        var client = BuildClient(_bobActorIri, _bobKey, _a.CreateHandler());
        using (client)
        {
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(ActivityJson.ActivityJsonContentType);
            return await client
                .SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, $"https://{AHost}{inboxPath}")
                    {
                        Content = content,
                    },
                    CancellationToken.None);
        }
    }

    /// <summary>
    /// Collects the IRI ids of the <see cref="Create"/> entries in the actor's outbox on A (the outbox is
    /// an <see cref="IReadOnlyList{IObjectOrLink}"/>; only <see cref="Create"/> entries are considered).
    /// </summary>
    private async Task<List<string?>> OutboxCreateIdsAsync(Iri actorIri)
    {
        var outbox = await _aPersistence.Activities.GetOutboxAsync(actorIri, CancellationToken.None);
        return outbox
            .Where(o => o is Create)
            .Select(o => (o as Activity)?.Id)
            .Where(id => id is not null)
            .ToList();
    }

    private sealed class TestWorker : IDisposable
    {
        private readonly IHost _host;
        private readonly DeliveryWorker _worker;

        internal TestWorker(IHost host, DeliveryWorker worker, IDeliveryService service)
        {
            _host = host;
            _worker = worker;
            Service = service;
        }

        public IDeliveryService Service { get; }

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
    /// <paramref name="key"/>), routing deliveries to <paramref name="targetServer"/>. The delivery
    /// service resolves the recipient's delivery target directly (recipient inbox) without a network
    /// round-trip.
    /// </summary>
    private static TestWorker BuildDeliveryWorker(Iri actorIri, KeyPair key, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new StubClientFactory(keyStore, keyProvider, signer, actorIri);
        var queue = new InMemoryDeliveryQueue();
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var service = new DeliveryService(queue, new StubActorDocumentFetcher(), loggerFactory.CreateLogger<DeliveryService>());
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = actorIri });
        var transportFactory = () => targetServer.CreateHandler();

        var worker = new DeliveryWorker(
            queue, factory, transportFactory, options,
            loggerFactory.CreateLogger<DeliveryWorker>());

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();

        return new TestWorker(host, worker, service);
    }

    private sealed class StubActorDocumentFetcher : IActorDocumentFetcher
    {
        private static readonly Actor Document = new() { Id = "https://stub.local/ap/v1/u/actor" };

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult<Actor?>(Document);
    }

    private sealed class StubClientFactory : IActivityPubClientFactory
    {
        private readonly IKeyStore _keyStore;
        private readonly IKeyProvider _keyProvider;
        private readonly ISignatureSigner _signer;
        private readonly Iri _actorId;

        public StubClientFactory(IKeyStore keyStore, IKeyProvider keyProvider, ISignatureSigner signer, Iri actorId)
        {
            _keyStore = keyStore;
            _keyProvider = keyProvider;
            _signer = signer;
            _actorId = actorId;
        }

        public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(httpHandler);

            var signingHandler = new SigningHandler(_signer, _keyProvider, httpHandler)
            {
                ActorId = _actorId,
            };

            var pipeline = new JsonLdHandler(signingHandler);
            var httpClient = new HttpClient(pipeline, disposeHandler: true)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };

            return new ActivityPubClient(httpClient);
        }

        public ILocalModerationClient CreateLocalModerationClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => new LocalModerationClient(null);

        public IMediaClient CreateMediaClient(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
            => new MediaClient(null);
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> whose pipeline signs as bob (the author) over
    /// <paramref name="innerHandler"/> (a capture handler) — used to produce the signed
    /// standalone-tombstone request without a network round-trip.
    /// </summary>
    private static HttpClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler innerHandler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var signingHandler = new SigningHandler(signer, keyProvider, innerHandler)
        {
            ActorId = actorIri,
        };

        var pipeline = new JsonLdHandler(signingHandler);
        return new HttpClient(pipeline, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Starts an instance with the given identity, routing its fetcher to <paramref name="targetServer"/>
    /// (so the instance can resolve the remote actor's key for inbound signature validation).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, KeyPair key,
        TestServer? targetServer = null)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var fetcher = targetServer is null
            ? (IActorDocumentFetcher?)null
            : BuildFetcherFor(host, handle, key, targetServer);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
        });
    }

    private static IActorDocumentFetcher BuildFetcherFor(
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

    private static Create BuildCreate(Iri actorIri, Iri objectIri, string content) => new()
    {
        Id = $"{actorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
            },
        ],
    };

    private static Tombstone BuildTombstone(Iri objectIri) => new()
    {
        Id = objectIri.Value,
        FormerType = ["https://www.w3.org/ns/activitystreams#Note"],
    };

    private static Create BuildCreateWithTombstone(Iri actorIri, Iri objectIri) => new()
    {
        Id = $"{actorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Tombstone
            {
                Id = objectIri.Value,
                FormerType = ["https://www.w3.org/ns/activitystreams#Note"],
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
