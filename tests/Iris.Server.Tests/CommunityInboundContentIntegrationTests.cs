using System.Text.Json;
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

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.5 integration test for the <strong>inbound federation to a local community</strong> slice:
/// a remote actor's content — a <see cref="Create"/> delivered to the community's inbox over the wire —
/// is propagated to the community's local members and surfaces in the community's unified feed. This is
/// the Lemmy→Iris half of 136.5 (the remote community's member posts landing in an Iris community feed).
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c>) and instance B (b.domain.local, actor
/// <c>bob</c>) — the same two-instance federation harness as the community-following test. B hosts a
/// community <c>iris</c> (a <see cref="Group"/>) with a single local member <c>bob</c>.
/// <list type="number">
/// <item><em>Timestamps surface:</em> a <see cref="Create"/> whose activity and embedded Note carry a
/// <c>published</c> value is delivered to B's community inbox. The federated content must surface in the
/// community feed <strong>with the originator's <c>published</c> preserved</strong> — not replaced by the
/// delivery time. This pins the 136.5 "timestamps" item (a federated note keeps the author's timestamp).</item>
/// <item><em>Idempotency:</em> the <strong>same</strong> <see cref="Create"/> (identical activity IRI) is
/// delivered to the community inbox twice. The C-07 guard in <see cref="InboxProcessor"/> (shared by every
/// inbound path, including the community-recipient branch) must store the activity exactly once and the
/// content must reach the member's outbox and the community feed exactly once — a redelivery is a no-op,
/// not a duplicate. This pins the 136.5 "idempotency" item for the community-inbound path.</item>
/// </list>
/// The deliveries are signed over the wire by a <see cref="DeliveryWorker"/> (routing to B's
/// <c>TestServer</c>); B validates each signature by resolving the sender's key from A's actor document
/// (fetched by B's <c>IActorDocumentFetcher</c>, wired to A's <c>TestServer</c>). The propagated member
/// outbox is asserted against B's persistence, and the feed is asserted against B's
/// <c>GET /ap/v1/c/{name}/feed</c> endpoint.
/// </remarks>
public sealed class CommunityInboundContentIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly HttpClient _bHttp;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _communityIri;
    private readonly Iri _communityInboxIri;
    private readonly Iri _bobActorIri;

    public CommunityInboundContentIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (public actor document, so B can resolve alice's key to validate signatures).
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        // B hosts bob; its fetcher is wired to A so B can validate alice's signature by fetching A's
        // actor document.
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;

        // B also hosts a community <c>iris</c> with bob as its (only) local member and a real key (so it
        // can sign outbound deliveries, e.g. an Accept, if one were queued).
        _communityIri = new Iri($"https://{BHost}/ap/v1/c/{Community}");
        _communityInboxIri = new Iri($"{_communityIri.Value}/inbox");
        var communityKey = TestSeeder.SeedCommunityWithKey(_bPersistence, BHost, Community, _bobActorIri).Key;

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

    // --- Timestamps surface: the federated note keeps the originator's `published` ------------

    [Fact]
    public async Task InboundCreate_WithPublished_SurfacesInCommunityFeed_WithOriginatorTimestamp()
    {
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _b);

        // A distinct, fixed `published` value (well before "now") so a test that mistakenly used the
        // delivery time would be detectable. The originator sets it on both the Create activity and the
        // embedded Note — the way a remote (e.g. Lemmy) server stamps a post.
        var published = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var create = BuildCreate(_aliceActorIri, published);

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

        // The federated content reached the local member bob's outbox (the Create activity itself).
        var bobOutbox = await _bPersistence.Activities.GetOutboxAsync(_bobActorIri);
        Assert.Contains(bobOutbox, o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);

        // The community feed surfaces the federated content.
        var response = await _bHttp.GetAsync($"https://{BHost}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Find the feed item whose id is the Create (the full Create object, serialized inline).
        var item = JsonDoc.GetItems(doc.RootElement)
            .FirstOrDefault(e => e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("id", out var idProp)
                && idProp.GetString() == create.Id);
        Assert.True(item.ValueKind == JsonValueKind.Object,
            $"The federated Create ({create.Id}) should appear in the community feed");

        // The originator's `published` survives on the Create activity — not replaced by the delivery
        // time (which would be ~now, years after the fixed 2026-01-15 stamp).
        Assert.True(item.TryGetProperty("published", out var createPublished),
            "The Create in the community feed should carry its `published` timestamp");
        var createPublishedValue = createPublished.GetDateTime();
        Assert.True(Math.Abs((createPublishedValue - published).TotalSeconds) < 1,
            $"The Create's `published` should preserve the originator's timestamp ({published:O}), got {createPublishedValue:O}");

        // And on the embedded Note (the object the client renders). `object` is one-or-many, so a single
        // Note serializes as a bare object (not an array); normalize both shapes.
        var objectProp = item.GetProperty("object");
        var note = objectProp.ValueKind == JsonValueKind.Array
            ? objectProp.EnumerateArray().FirstOrDefault()
            : objectProp;
        var notePublished = default(JsonElement);
        Assert.True(note.ValueKind == JsonValueKind.Object && note.TryGetProperty("published", out notePublished),
            "The embedded Note in the community feed should carry its `published` timestamp");
        var notePublishedValue = notePublished.GetDateTime();
        Assert.True(Math.Abs((notePublishedValue - published).TotalSeconds) < 1,
            $"The Note's `published` should preserve the originator's timestamp ({published:O}), got {notePublishedValue:O}");
    }

    // --- Idempotency: a redelivered Create (same IRI) is recorded exactly once ----------------

    [Fact]
    public async Task RedeliveredCreate_SameIri_RecordedExactlyOnce_InMemberOutboxAndFeed()
    {
        using var worker = BuildDeliveryWorker(_aliceActorIri, _aliceKey, _b);

        // A single Create, delivered to the community inbox TWICE (identical activity IRI) — as a
        // well-meaning (or retrying) remote server might redeliver the same post.
        var create = BuildCreate(_aliceActorIri);
        await worker.Service.DeliverAsync(_communityInboxIri, create);
        await worker.Service.DeliverAsync(_communityInboxIri, create);
        Assert.Equal(2, worker.Queue.Count);

        await worker.StartAsync(CancellationToken.None);
        // Wait until the (first) delivery's effect is visible (the content in bob's outbox). The second
        // delivery is a C-07 no-op, so the effect appears once the first dispatch has run.
        await WaitForAsync(async () =>
            (await _bPersistence.Activities.GetOutboxAsync(_bobActorIri)).Any(o =>
                o is IObject { Id: { Length: > 0 } id } && id == create.Id),
            timeout: TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        // The activity is stored (once — the store is IRI-keyed, so the count is structural).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(create.Id!), out _),
            "B should have stored the Create delivered to the community inbox");

        // The C-07 guard is in the inbox processor, before dispatch: the second delivery was skipped, so
        // the handler ran once. The member's outbox therefore holds the Create EXACTLY ONCE — a duplicate
        // would mean the guard let the second delivery through.
        var bobOutbox = await _bPersistence.Activities.GetOutboxAsync(_bobActorIri);
        var outboxCount = bobOutbox.Count(o => o is IObject { Id: { Length: > 0 } id } && id == create.Id);
        // A redelivered Create must reach the member's outbox exactly once (C-07 idempotency).
        Assert.Equal(1, outboxCount);

        // And the community feed surfaces it exactly once (the feed de-duplicates by IRI as well, but the
        // exactly-once outbox entry above is the authoritative guard assertion).
        var response = await _bHttp.GetAsync($"https://{BHost}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feedCount = JsonDoc.GetItems(doc.RootElement)
            .Count(e => e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("id", out var idProp)
                && idProp.GetString() == create.Id);
        // A redelivered Create must surface in the community feed exactly once.
        Assert.Equal(1, feedCount);
    }

    // --- Helpers ----------------------------------------------------------------------------

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

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
        });
    }

    private static Create BuildCreate(Iri actorIri, DateTime? published = null)
    {
        var note = new Note
        {
            Id = $"https://{AHost}/objects/note-{Guid.NewGuid():N}",
            Content = ["inbound community content"],
        };
        if (published is not null)
        {
            note.Published = published.Value;
        }
        var create = new Create
        {
            Id = $"https://{AHost}/activities/create-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [note],
        };
        if (published is not null)
        {
            create.Published = published.Value;
        }
        return create;
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
