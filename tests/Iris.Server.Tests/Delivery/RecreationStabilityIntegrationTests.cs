using System.Net;
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
/// Phase 19.3.7 recreation-stability test: a host that has already delivered an outbound federation
/// <see cref="Create"/> is <em>recreated</em> (its process stops and starts — the <c>docker compose
/// down</c> (no <c>-v</c>) + <c>up</c> cycle) and the re-created instance's delivery queue replays the
/// already-delivered activity from its on-disk journal. The replay must be a <strong>harmless
/// no-op</strong>, not a re-delivery storm: the peer instance must store the activity exactly once,
/// list it in the recipient's outbox exactly once (no duplicate edge), and not re-fan-out the
/// activity (bounded outbound deliveries).
/// </summary>
/// <remarks>
/// The guarantee rests on two independent guards. (1) The file-backed <see cref="FileBackedDeliveryQueue"/>
/// journals every enqueued job to disk and, on construction, replays every journaled job into its channel
/// (at-least-once). A host that stops without truncating the journal (the default — the shutdown service
/// completes the queue but does not truncate it) therefore re-delivers, on the next <c>up</c>, every
/// activity it has already delivered. (2) The peer's <see cref="Iris.Server.Inbox.IInboxProcessor"/>
/// stores an inbound activity add-if-absent by its <c>Id</c> (C-07) and, on a re-delivery, does NOT
/// re-dispatch it to a handler — so the replay is stored as a no-op and never re-fan-out. Together,
/// these make a recreation safe: no re-delivery storm, no duplicated edges, outboxes unchanged.
/// </remarks>
/// <para>
/// Topology: instance A (rec-a.domain.local, author <c>alice</c>) and instance B (rec-b, follower
/// <c>bob</c>). Both are live in-process <c>TestServer</c>s (A serves its own actor document so B can
/// validate inbound signatures). A's outbound delivery is driven by a standalone
/// <see cref="DeliveryWorker"/> over a <see cref="FileBackedDeliveryQueue"/> whose journal lives on a
/// temp directory (simulating the named volume that survives <c>down</c> without <c>-v</c>) — A's hosted
/// worker does not run (the in-process <c>TestServer</c> starts no hosted services), so the standalone
/// worker models A's outbound federation. The test (1) enqueues a single <c>Create</c> (alice → bob's
/// inbox, signed as alice) and runs the worker once (the intended delivery), (2) <em>recreates</em> the
/// worker by constructing a fresh <see cref="FileBackedDeliveryQueue"/> over the same journal (which
/// replays the already-delivered <c>Create</c>) and runs it a second time (the replay), and (3) asserts
/// B stored the <c>Create</c> exactly once, B's bob outbox lists it exactly once, and B made no
/// (unbounded) outbound re-fan-out for the activity.
/// </para>
public sealed class RecreationStabilityIntegrationTests : IDisposable
{
    private const string AHost = "rec-a.domain.local";
    private const string BHost = "rec-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly string _dir = Directory.CreateTempSubdirectory("iris-recreation-").FullName;
    private readonly string _journalPath;

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;
    private readonly DeliveryCounter _bOutbound = new();
    private readonly DeliveryCounter _aOutbound = new();

    public RecreationStabilityIntegrationTests()
    {
        _journalPath = Path.Combine(_dir, "delivery-queue.jsonl");

        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();

        // bob→alice on B (the author's home instance owns the follower set / propagation target set).
        _bPersistence.Follows.RecordFollowAsync(_bobActorIri, _aliceActorIri).GetAwaiter().GetResult();

        // A (alice's home instance): a live TestServer so it serves its own actor document (alice's
        // publicKey) — B needs it to validate an inbound activity signed as alice. A's outbound
        // delivery is driven by a standalone worker (see the test); A's hosted worker does not run.
        _a = StartServer(AHost, Alice, _aPersistence, _aliceKey);

        // B (bob's home instance): a live TestServer running the full inbound pipeline (signature
        // validation → InboxProcessor add-if-absent gate → CreateActivityHandler). Its fetcher reaches
        // A's actor document (so B validates the alice-signed Create). Its outbound delivery transport
        // routes to a counting handler: bob has no remote followers, so the CreateActivityHandler's
        // fan-out targets none — the counter proves B does not re-fan-out the (replayed) activity.
        _b = StartServer(
            BHost, Bob, _bPersistence, _bobKey,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, a: _a),
            outboundTransport: () => new CountingHandler(
                new HttpClientHandler(), _bOutbound));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // --- Recreation: an already-delivered Create replayed from the journal is a no-op --------

    [Fact]
    public async Task Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged()
    {
        // The note IRI is the key B stores the embedded object under (the CreateActivityHandler stores
        // the embedded Note, not the Create activity), so the storage assertions target it.
        var noteIri = new Iri($"{_aliceActorIri}/notes/{Guid.NewGuid():N}");
        var create = BuildCreate(_aliceActorIri, noteIri, content: "recreation post");

        // Phase 1 — the original host run: A enqueues the Create to bob's inbox (signed as alice) on a
        // FileBackedDeliveryQueue (journaled to disk) and the worker delivers it. B's full pipeline
        // stores the embedded note and records the Create in bob's outbox (the single intended delivery).
        // The A-outbound counter (which counts A's worker's outbound deliveries into B) goes to 1 — the
        // intended delivery.
        await using (var firstQueue = new FileBackedDeliveryQueue(_journalPath))
        {
            using var worker1 = BuildWorker(firstQueue, targetServer: _b);
            await firstQueue.EnqueueAsync(new DeliveryJob(_bobInboxIri, create, _aliceActorIri));
            await worker1.StartAsync(CancellationToken.None);
            await WaitForAsync(
                async () => await _bPersistence.Objects.TryGetObjectAsync(noteIri, out _),
                timeout: TimeSpan.FromSeconds(10));
            await worker1.StopAsync(CancellationToken.None);
        }

        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored),
            $"B should have stored the embedded note federated from A (aOutbound={_aOutbound.Total}, " +
            $"bOutbound={_bOutbound.Total}, noteIri={noteIri})");
        Assert.IsType<Note>(stored);

        // The outbox now lists the Create exactly once (F-1911-2 outbox dedup, first delivery).
        var outboxAfterFirst = await _bPersistence.Activities.GetOutboxAsync(_bobActorIri);
        var firstCount = outboxAfterFirst.Count(o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        Assert.Equal(1, firstCount);
        Assert.True(
            _aOutbound.Total == 1,
            $"exactly one intended A→B outbound delivery should have occurred before the recreation; " +
            $"got {_aOutbound.Total}");

        // Phase 2 — the recreation: the host stops (no truncation of the journal, matching the default
        // shutdown service) and restarts. A fresh FileBackedDeliveryQueue over the same journal replays
        // the already-delivered Create into its channel, and the (re-created) worker re-delivers it to
        // bob's inbox over the wire (the at-least-once replay).
        await using (var replayQueue = new FileBackedDeliveryQueue(_journalPath))
        {
            // The journal was not truncated, so the replayed job is present (Count == 1) — this is the
            // re-transmission the slice is about (a real host re-sends it on `up`).
            Assert.True(
                replayQueue.Count >= 1,
                "the un-truncated journal should replay the already-delivered Create on recreation");

            using var worker2 = BuildWorker(replayQueue, targetServer: _b);
            await worker2.StartAsync(CancellationToken.None);

            // Wait until the recreation replay has ACTUALLY RE-DELIVERED the Create to B over the wire
            // (the A-outbound counter goes from 1 — the intended delivery — to 2 — the replay). This
            // proves the replay is a genuine re-transmission, not a no-op that never left A: without it,
            // the no-op assertions below would be vacuous.
            await WaitForAsync(() => Task.FromResult(_aOutbound.Total >= 2), timeout: TimeSpan.FromSeconds(10));
            await worker2.StopAsync(CancellationToken.None);
        }

        // THE RECREATION-STABILITY ASSERTIONS:
        //
        // (0) The recreation replay GENUINELY re-delivered the Create over the wire: A's worker sent it
        // to B exactly twice in total (1 intended delivery + 1 replay). This is the at-least-once
        // re-transmission the slice is about (an un-truncated journal re-sends on `up`). Without this,
        // the no-op assertions below would be vacuous (the replay could have never left A).
        Assert.True(
            _aOutbound.Total == 2,
            $"the recreation replay must actually re-deliver the Create over the wire (exactly one " +
            $"replay after the one intended delivery); without a re-delivery the no-op assertions below " +
            $"are vacuous (got {_aOutbound.Total} A→B outbound deliveries)");

        // (1) B still stores the embedded note exactly once (the replay is a no-op; it did not overwrite
        // or duplicate the stored object).
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stillStored),
            "B should still store the embedded note after the recreation replay");
        Assert.IsType<Note>(stillStored);

        // (2) B's bob outbox is unchanged in length and still lists the Create exactly once — no
        // duplicate edge from the replay (AddToOutboxAsync is idempotent-by-IRI, and the inbox-Id
        // guard never re-dispatched the replay to the handler that writes the outbox).
        var outboxAfterReplay = await _bPersistence.Activities.GetOutboxAsync(_bobActorIri);
        var replayCount = outboxAfterReplay.Count(o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        Assert.True(
            outboxAfterReplay.Count == outboxAfterFirst.Count,
            $"the outbox must be unchanged in length after the recreation replay (no re-delivery storm); " +
            $"got {outboxAfterReplay.Count} (was {outboxAfterFirst.Count})");
        Assert.True(
            replayCount == 1,
            $"the replay must not duplicate the Create in bob's outbox (no duplicate edge); got {replayCount}");

        // (3) No re-fan-out storm: B made no outbound deliveries for this activity (bob has no remote
        // followers, and — critically — the replay was deduped before the handler could fan anything
        // out). The counter is bounded (the 19.3.1/19.3.2 loop-safety property, now across a
        // recreation, not just a same-process re-delivery).
        Assert.True(
            _bOutbound.Total <= 1,
            $"the recreation replay must not re-fan-out the activity (got {_bOutbound.Total} outbound " +
            "deliveries from B); an unbounded re-fan-out is the 19.3.1/19.3.2 echo defect, now across " +
            "a recreation.");
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Starts one instance: a live <c>TestServer</c> running the full pipeline. When
    /// <paramref name="fetcher"/> is supplied, it is used for inbound signature validation (route to
    /// the peer's actor document); when <paramref name="outboundTransport"/> is supplied, it is the
    /// outbound delivery transport (so a test can count/observe the instance's outbound deliveries).
    /// A's outbound delivery is driven by a standalone worker (its hosted worker does not run), so A
    /// passes no fetcher/transport (its default HttpClient transport is unused).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence, KeyPair key,
        IActorDocumentFetcher? fetcher = null, Func<HttpMessageHandler>? outboundTransport = null)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri($"https://{host}/ap/v1/u/{handle}"), key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            Fetcher = fetcher,
            DeliveryTransport = outboundTransport,
        });
    }

    /// <summary>
    /// Builds a hosted <see cref="DeliveryWorker"/> signed as alice over the given
    /// <paramref name="queue"/>, routing deliveries to <paramref name="targetServer"/>. This models
    /// A's outbound delivery: a real worker that signs as alice and delivers to B's inbox. The queue is
    /// a <see cref="FileBackedDeliveryQueue"/> so the recreation test can replay the journaled jobs by
    /// reconstructing the queue over the same path. When <paramref name="onSend"/> is supplied, it is
    /// invoked for every outbound delivery (used to count the re-delivered replay over the wire).
    /// </summary>
    private DeliveryWorker BuildWorker(
        IDeliveryQueue queue, TestServer targetServer, Action<HttpRequestMessage>? onSend = null)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_aliceActorIri, _aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = _aliceActorIri });

        return new DeliveryWorker(
            queue, new StubClientFactory(keyStore, keyProvider, signer, _aliceActorIri),
            () => new CountingHandler(targetServer.CreateHandler(), _aOutbound, onSend),
            options,
            loggerFactory.CreateLogger<DeliveryWorker>());
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> that routes to <paramref name="a"/>'s actor
    /// documents (so B can validate an inbound activity signed as alice by fetching A's actor
    /// document, where alice's publicKey lives).
    /// </summary>
    private IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair key, TestServer a)
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
            a.CreateHandler());

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Builds a <see cref="Create"/> for <paramref name="actorIri"/> (alice) whose embedded <see cref="Note"/>
    /// has IRI <paramref name="noteIri"/> and content <paramref name="content"/>. The note IRI is the key
    /// B stores the embedded object under (the <c>CreateActivityHandler</c> stores the object, not the
    /// activity), so a test asserts on it.
    /// </summary>
    private static Create BuildCreate(Iri actorIri, Iri noteIri, string content) => new()
    {
        Id = $"{actorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = noteIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(actorIri.Value) }],
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

    // --- Private test doubles --------------------------------------------------------------

    /// <summary>
    /// An <see cref="IActivityPubClientFactory"/> that signs only as one fixed actor (the test delivery
    /// worker's author, alice). The worker's single client is created with the author identity, so the
    /// requested <see cref="ActivityPubClientOptions.ActorId"/> always matches. The pipeline is
    /// <c>JsonLdHandler → SigningHandler → transport</c> (no retry: a delivery is non-idempotent; the
    /// worker retries at its own layer) — the same shape the production federation tests use for a
    /// standalone author worker.
    /// </summary>
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
    }

    /// <summary>
    /// Counts outbound deliveries (total) and forwards each request to its inner handler (the target
    /// server's real handler — this is NOT a terminal stub; it routes to the peer). When
    /// <c>onSend</c> is supplied it is invoked for every delivery (used on A's transport to count the
    /// re-delivered replay over the wire, and on B's transport to count B's own re-fan-out in a second
    /// counter). Lets a test assert B makes no unbounded re-fan-out of a replayed activity and that the
    /// replay actually re-delivered over the wire.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly DeliveryCounter _counter;
        private readonly Action<HttpRequestMessage>? _onSend;

        public CountingHandler(
            HttpMessageHandler inner, DeliveryCounter counter, Action<HttpRequestMessage>? onSend = null)
            : base(inner)
        {
            _counter = counter;
            _onSend = onSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _counter.Record();
            _onSend?.Invoke(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Counts outbound deliveries (a simple total), so a test can assert the number of deliveries of a
    /// single activity is bounded (no re-delivery loop).
    /// </summary>
    private sealed class DeliveryCounter
    {
        private int _count;

        public void Record() => System.Threading.Interlocked.Increment(ref _count);

        public int Total => System.Threading.Volatile.Read(ref _count);
    }
}
