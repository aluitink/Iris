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
/// Phase 19.3.5 integration test: the two-instance follow-edge <strong>convergence</strong> invariant.
/// Alice (instance A) follows Bob (instance B), then un-follows, then re-follows. After the cycle settles,
/// both sides' <see cref="Iris.Server.Stores.IFollowStore"/> edge must agree and be exactly the single
/// <c>alice → bob</c> edge: no <em>orphan</em> edge (an <see cref="Iri"/> in one side's collection with
/// no counterpart on the other), no <em>duplicate</em> edge (the same <see cref="Iri"/> listed more than
/// once), and the public <c>following</c>/<c>followers</c> collections are <em>stable</em> (re-reading
/// them yields the same IRIs and the same count).
/// </summary>
/// <remarks>
/// Topology: instance A (conv-a.domain.local, <c>alice</c>) and instance B (conv-b.domain.local,
/// <c>bob</c>), each hosting its own local person. Each instance's outbound <see cref="DeliveryWorker"/>
/// routes to the peer over the wire; each instance's <see cref="IActorDocumentFetcher"/> routes by
/// actor-IRI host (self → self, peer → peer) so an inbound activity's signature is validated by fetching
/// the author's actor document from the right instance.
/// </remarks>
/// <para>
/// The follow lifecycle is two-sided: an outbound <see cref="Follow"/> (published to the follower's own
/// outbox) is federated to the target's inbox, where the <see cref="Iris.Server.Inbox.FollowActivityHandler"/>
/// records the directed edge and schedules an <c>Accept</c> back to the follower; an outbound
/// <see cref="Undo"/> (un-follow, published to the follower's outbox) is federated to the target's inbox,
/// where the <see cref="Iris.Server.Inbox.UndoActivityHandler"/> removes the edge. Convergence requires
/// that <em>every</em> edge a side records is mirrored by the other and that an un-follow removes the edge
/// from both sides — so the test asserts the store-level edge (the source of truth) <em>and</em> the
/// public collection endpoints (what a client actually reads), in both directions, at the end of the
/// cycle.
/// </para>
public sealed class FollowEdgeConvergenceIntegrationTests : IDisposable
{
    private const string AHost = "conv-a.domain.local";
    private const string BHost = "conv-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;

    public FollowEdgeConvergenceIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        _bobActorIri = bSeeded.ActorIri;

        // Each instance's outbound delivery worker routes to the peer, and its fetcher routes by
        // actor-IRI host (self → self, peer → peer) so it can validate the peer's signature by fetching
        // the peer's actor document (where the public key lives).
        _a = StartServer(
            AHost, Alice, _aPersistence, _aliceKey, _aliceActorIri,
            peer: () => _b!, self: () => _a!);
        _b = StartServer(
            BHost, Bob, _bPersistence, _bobKey, _bobActorIri,
            peer: () => _a!, self: () => _b!);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A follow / un-follow / re-follow cycle converges the edge on both instances ----------

    [Fact]
    public async Task Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections()
    {
        // Phase 1 — alice follows bob. The Follow is published to alice's own outbox; A federates it to
        // bob's inbox over the wire (signed as alice); B's FollowActivityHandler records the alice → bob
        // edge in B's follow store. (A also records the follow it authored, so alice's own following set
        // is populated on her home instance.)
        var follow = BuildFollow(_aliceActorIri, _bobActorIri);
        await PublishToOutboxAsync(_aliceActorIri, _aliceKey, follow, target: () => _a!);

        // Decision 055: A minted the Follow's id (the client sent it id-less). Learn the minted id from
        // A's outbox (the published Follow is recorded there under its minted id). The Undo references
        // this learned id; inbound federation keeps the originator's id, so B records the edge under
        // this same id.
        var followId = await FindFollowIriInOutboxAsync(_aPersistence, _aliceActorIri, _bobActorIri);

        // B's edge is the cross-instance half: bob's follower set now contains alice (on B, where bob
        // lives). A's edge is alice's own following set.
        await WaitForAsync(
            async () => await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(15));
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after alice follows bob, B should record that alice follows bob");
        Assert.True(
            await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after alice follows bob, A should record alice's own follow of bob");
        Assert.True(
            await _aPersistence.Follows.IsFollowingAsync(_bobActorIri, _aliceActorIri) is false,
            "bob must not follow alice (no reciprocal edge was ever created)");

        // Phase 2 — alice un-follows bob. The Undo (object = the original Follow's LEARNED id) is
        // published to alice's own outbox; A federates it to bob's inbox over the wire (signed as
        // alice); B's UndoActivityHandler (recipient = bob, the target) removes the alice → bob edge from
        // B's follow store. A's UndoActivityHandler (recipient = alice, the un-follower) removes alice's
        // own following edge.
        var undo = BuildUndo(_aliceActorIri, followId);
        await PublishToOutboxAsync(_aliceActorIri, _aliceKey, undo, target: () => _a!);

        // Both sides must drop the edge (this is the convergence-critical step: an orphan on either side
        // means the cycle did not fully unwind).
        await WaitForAsync(
            async () =>
                !await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri)
                && !await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(15));
        Assert.False(
            await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after the un-follow, A must no longer record alice → bob (no orphan on the follower's home side)");
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after the un-follow, B must no longer record alice → bob (no orphan on the target's side)");
        var bobFollowersAfterUndo = await _bPersistence.Follows.GetFollowersAsync(_bobActorIri);
        Assert.Empty(bobFollowersAfterUndo);
        Assert.True(bobFollowersAfterUndo.Count == 0,
            "after the un-follow, bob's follower set must be empty (no orphan follower IRI)");
        var aliceFollowingAfterUndo = await _aPersistence.Follows.GetFollowingAsync(_aliceActorIri);
        Assert.Empty(aliceFollowingAfterUndo);
        Assert.True(aliceFollowingAfterUndo.Count == 0,
            "after the un-follow, alice's following set must be empty (no orphan following IRI)");

        // Phase 3 — alice re-follows bob. A new Follow (a fresh IRI, since the deterministic
        // {alice}/follows/{bob} IRI was already stored in phase 1) is published to alice's outbox; A
        // federates it to bob's inbox; B's FollowActivityHandler re-records the alice → bob edge.
        var reFollow = BuildFollow(_aliceActorIri, _bobActorIri);
        await PublishToOutboxAsync(_aliceActorIri, _aliceKey, reFollow, target: () => _a!);

        await WaitForAsync(
            async () =>
                await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri)
                && await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            timeout: TimeSpan.FromSeconds(15));

        // --- Convergence assertions (the 19.3.5 invariant) ---------------------------------------
        // Both sides' store agree on the single edge: alice follows bob, and nothing else.
        Assert.True(
            await _aPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after the re-follow, A must record alice → bob");
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_aliceActorIri, _bobActorIri),
            "after the re-follow, B must record alice → bob");

        // The edge is recorded exactly once on each side (no duplicate IRI) — the following/followers
        // sets are HashSets, so a duplicate would surface as a count > 1 only if the store were a
        // multiset; assert the count is exactly 1 and the single IRI is the expected one.
        var aAliceFollowing = await _aPersistence.Follows.GetFollowingAsync(_aliceActorIri);
        Assert.Equal([_bobActorIri], aAliceFollowing.Distinct().ToList());
        Assert.Single(aAliceFollowing);

        var bBobFollowers = await _bPersistence.Follows.GetFollowersAsync(_bobActorIri);
        Assert.Equal([_aliceActorIri], bBobFollowers.Distinct().ToList());
        Assert.Single(bBobFollowers);

        // No reciprocal / spurious edge on either side.
        var aliceFollowers = await _aPersistence.Follows.GetFollowersAsync(_aliceActorIri);
        Assert.Empty(aliceFollowers);
        Assert.True(aliceFollowers.Count == 0, "alice must have no followers (bob never followed her)");
        var bobFollowing = await _bPersistence.Follows.GetFollowingAsync(_bobActorIri);
        Assert.Empty(bobFollowing);
        Assert.True(bobFollowing.Count == 0, "bob must follow no one (he never followed anyone)");

        // --- Stable public collections ----------------------------------------------------------
        // The public `following` (alice) and `followers` (bob) endpoints are what a client reads; they
        // must expose exactly the converged edge and be stable across re-reads (same IRIs, same count).
        var aliceFollowingIri = new Iri($"{_aliceActorIri.Value.TrimEnd('/')}/following");
        var bobFollowersIri = new Iri($"{_bobActorIri.Value.TrimEnd('/')}/followers");

        var aliceFollowingA = await ReadCollectionIrisAsync(_a, aliceFollowingIri);
        var aliceFollowingB = await ReadCollectionIrisAsync(_a, aliceFollowingIri);
        var bobFollowersB = await ReadCollectionIrisAsync(_b, bobFollowersIri);
        var bobFollowersBAgain = await ReadCollectionIrisAsync(_b, bobFollowersIri);
        Assert.Equal([_bobActorIri], aliceFollowingA);
        Assert.Equal(aliceFollowingA.Count, aliceFollowingB.Count);
        Assert.Contains(_bobActorIri, aliceFollowingB);

        Assert.Equal([_aliceActorIri], bobFollowersB);
        Assert.Equal(bobFollowersB.Count, bobFollowersBAgain.Count);
        Assert.Contains(_aliceActorIri, bobFollowersBAgain);

        // The two sides agree: the IRI in alice's `following` (on A) is the same IRI in bob's `followers`
        // (on B).
        Assert.Contains(_bobActorIri, aliceFollowingA);
        Assert.Contains(_aliceActorIri, bobFollowersB);
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="Follow"/> activity (actor = <paramref name="actorIri"/>, object =
    /// <paramref name="targetIri"/>). The IRI is unique per call (a <see cref="Guid"/>) so a re-follow in
    /// the same cycle does not collide with the earlier follow's already-stored deterministic IRI.
    /// </summary>
    private static Follow BuildFollow(Iri actorIri, Iri targetIri) => new()
    {
        // Decision 055: the client sends the Follow's shape (no id); the server mints the id and returns
        // it in the 2xx body.
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(targetIri.Value) }],
    };

    /// <summary>
    /// Finds the IRI of the most recent <see cref="Follow"/> in an actor's outbox whose object is the
    /// given target. Decision 055 mints the Follow's id, so after publishing an id-less follow the
    /// learner reads the outbox to learn the minted id (for a subsequent Undo's object reference).
    /// </summary>
    private static async Task<Iri> FindFollowIriInOutboxAsync(
        IPersistenceProvider persistence, Iri actorIri, Iri targetIri)
    {
        var outbox = await persistence.Activities.GetOutboxAsync(actorIri);
        foreach (var item in outbox)
        {
            if (item is not Follow follow)
            {
                continue;
            }

            var objectIri = follow.Object?.FirstOrDefault().ResolveObjectIri();
            if (objectIri is { } iri && iri == targetIri && !string.IsNullOrWhiteSpace(follow.Id))
            {
                return new Iri(follow.Id);
            }
        }

        throw new InvalidOperationException(
            $"No Follow of {targetIri.Value} found in {actorIri.Value}'s outbox to learn its minted id from.");
    }

    /// <summary>
    /// Builds an <see cref="Undo"/> of a follow (un-follow): actor = the un-follower, object = the
    /// original <see cref="Follow"/> by its LEARNED (server-minted) IRI. The Undo's own id is minted by
    /// the server (decision 055).
    /// </summary>
    private static Undo BuildUndo(Iri actorIri, Iri originalFollowId) => new()
    {
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(originalFollowId.Value) }],
    };

    /// <summary>
    /// Publishes <paramref name="activity"/> to <paramref name="actorIri"/>'s outbox (the write surface
    /// for activities an actor authors), signed as <paramref name="actorIri"/> (key
    /// <paramref name="key"/>), through a hosted delivery worker whose transport routes to
    /// <paramref name="target"/>. The worker's <see cref="IActivityPubClient"/> runs the full
    /// JsonLd → signing pipeline, so the request is a correctly signed ActivityPub POST.
    /// </summary>
    private static async Task PublishToOutboxAsync(
        Iri actorIri, KeyPair key, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var queue = new InMemoryDeliveryQueue();
        var loggerFactory = NullLoggerFactory.Instance;
        var service = new Iris.Server.Delivery.DeliveryService(
            queue, loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryService>());
        var worker = new Iris.Server.Delivery.DeliveryWorker(
            queue, factory,
            () => target().CreateHandler(),
            Microsoft.Extensions.Options.Options.Create(
                new ActivityPubServerOptions { InstanceActorId = actorIri }),
            loggerFactory.CreateLogger<Iris.Server.Delivery.DeliveryWorker>());

        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService(_ => worker))
            .Build();

        await host.StartAsync(CancellationToken.None);
        try
        {
            await service.DeliverAsync(actorIri.OutboxOf(), activity);
            // Let the (single) delivery settle before returning.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Starts one instance: the local <c>handle</c> host whose outbound delivery worker routes to the
    /// peer's <c>TestServer</c> and signs as the local actor, and whose fetcher routes by actor-IRI host
    /// (self → self, peer → peer) so it can validate the peer's signature by fetching the peer's actor
    /// document.
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        KeyPair key, Iri actorIri,
        Func<TestServer> peer, Func<TestServer> self)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var peerHost = host == AHost ? BHost : AHost;
        var selfHandler = new LazyHandler(() => self().CreateHandler());
        var peerHandler = new LazyHandler(() => peer().CreateHandler());

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(keyStore, keyProvider, signer),
            DeliveryTransport = () => peerHandler,
            Fetcher = new RoutingFetcher(
                host, selfHandler, peerHost, peerHandler, key, actorIri),
        });
    }

    /// <summary>
    /// GETs a public <c>following</c>/<c>followers</c> collection on <paramref name="server"/> and returns
    /// the ordered IRIs it exposes (the <see cref="Iri"/> of each ordered item).
    /// </summary>
    private static async Task<IReadOnlyList<Iri>> ReadCollectionIrisAsync(TestServer server, Iri collectionIri)
    {
        // Read through the live store (?refresh=true bypasses the local collection-page response cache and
        // re-renders from the store), so a stale page-1 cache from an earlier read of the same collection
        // cannot mask the converged edge (the convergence invariant is about the live edge state). The
        // items are read from the raw JSON (via JsonDoc) rather than the ActivityStreams types: the
        // one-or-many converter emits a single item as a bare scalar, which the typed OrderedCollection
        // deserializes as a string (losing the IRI), so the raw shape is the reliable read path.
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);
        var json = await http.GetStringAsync($"{collectionIri.Value}?refresh=true");
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var irIs = new List<Iri>();
        foreach (var item in JsonDoc.GetItems(doc.RootElement))
        {
            var itemIri = JsonDoc.ItemId(item);
            if (!string.IsNullOrEmpty(itemIri))
            {
                irIs.Add(new Iri(itemIri));
            }
        }

        return irIs;
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

    // --- Private test doubles --------------------------------------------------------------

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance's actor documents based
    /// on the actor IRI's host (each instance's fetcher needs to reach both itself and the peer to
    /// validate signatures).
    /// </summary>
    private sealed class RoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public RoutingFetcher(
            string aHost, HttpMessageHandler aHandler,
            string bHost, HttpMessageHandler bHandler,
            KeyPair signingKey, Iri signingActor)
        {
            _ = signingActor;
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, "local", signingKey, aHandler),
                [bHost] = BuildFetcherFor(bHost, "local", signingKey, bHandler),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Host;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }
    }

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
}
