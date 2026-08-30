using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.4 end-to-end test (F-08 — inbound <see cref="Move"/> / actor migration): an actor on
/// instance A (alice, <c>https://a.domain.local/…</c>) migrates to a new IRI on instance B
/// (<c>https://b.domain.local/…</c>). A client signed as the OLD alice POSTs a <c>Move</c> to the inbox of
/// a local follower (bob, on B) — the <c>Move</c> is signature-validated by B (B fetches the OLD alice's
/// actor document from A to resolve her key), then B's <see cref="MoveActivityHandler"/> re-points the
/// local follow edge <c>bob → oldIri</c> to <c>bob → newIri</c>.
/// </summary>
/// <remarks>
/// This proves the full inbound <c>Move</c> path end-to-end: signature validation (key resolution via the
/// moving actor's own actor document) → store → interpret (re-point the local follow edge). The re-pointing
/// is what keeps a migrating actor reachable: after the <c>Move</c>, the instance follows the actor's new
/// IRI instead of the dead old one. A second test covers the community-follows-set re-pointing.
/// </remarks>
public sealed class MoveFederationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _oldAliceIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;

    public MoveFederationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _oldAliceIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();

        // The moving actor's local follower: bob (on B) follows the old alice (on A). The edge is recorded
        // in B's follow store (B owns bob's follow state).
        _bPersistence.Follows.RecordFollowAsync(_bobActorIri, _oldAliceIri).GetAwaiter().GetResult();

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(
            BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A Move signed by the moving actor re-points the local follow edge -----------

    [Fact]
    public async Task Move_SignedByMovingActor_RepointsLocalFollowerEdge()
    {
        var newAliceIri = new Iri($"https://{BHost}/ap/v1/u/{Alice}");
        var move = BuildMove(_oldAliceIri, newAliceIri);

        // A client signed as the OLD alice (her key is on A), routing to B's TestServer.
        using var client = BuildDeliveryClient(_oldAliceIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_bobInboxIri, move);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature (by fetching the OLD alice's actor doc from A to resolve her key) and
        // stored the Move.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(move.Id!), out var stored),
            "B should have stored the Move after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Move>(stored);

        // B's MoveActivityHandler re-pointed the local follow edge: bob no longer follows the old IRI, and
        // now follows the new IRI.
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, _oldAliceIri),
            "bob should no longer follow the old alice IRI after the Move");
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, newAliceIri),
            "bob should now follow the new alice IRI after the Move");
    }

    // --- A Move re-points a local community's follows set -----------------------------

    [Fact]
    public async Task Move_SignedByMovingActor_RepointsLocalCommunityFollow()
    {
        var communityIri = TestSeeder.SeedCommunity(_bPersistence, BHost, Community);
        // The community (on B) follows the old alice (on A): recorded in the community's follows set.
        await _bPersistence.Communities.AddFollowAsync(communityIri, _oldAliceIri);

        var newAliceIri = new Iri($"https://{BHost}/ap/v1/u/{Alice}");
        var move = BuildMove(_oldAliceIri, newAliceIri);

        using var client = BuildDeliveryClient(_oldAliceIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_bobInboxIri, move);
        Assert.Equal(202, statusCode.StatusCode);

        // The community's follows set is re-pointed: it no longer follows the old IRI, and now follows the
        // new IRI.
        var follows = await _bPersistence.Communities.GetFollowsAsync(communityIri);
        Assert.DoesNotContain(_oldAliceIri, follows);
        Assert.Contains(newAliceIri, follows);
    }

    // --- A Move with no local follower/community edge is a no-op ----------------------

    [Fact]
    public async Task Move_SignedByMovingActor_WithNoLocalEdge_IsStoredButNoOp()
    {
        // A fresh persistence where bob does NOT follow the old alice and no community follows her.
        var bPersistence = new InMemoryPersistenceProvider();
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        using var b = StartServer(
            BHost, Bob, bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));

        var newAliceIri = new Iri($"https://{BHost}/ap/v1/u/{Alice}");
        var move = BuildMove(_oldAliceIri, newAliceIri);

        using var client = BuildDeliveryClient(_oldAliceIri, _aliceKey, b.CreateHandler());
        var statusCode = await client.DeliverAsync(bSeeded.ActorIri.InboxOf(), move);
        Assert.Equal(202, statusCode.StatusCode);

        // The Move is stored (validated), but there is no local edge to re-point: the follow store is
        // empty on both the old and the new IRI.
        Assert.True(await bPersistence.Activities.TryGetActivityAsync(new Iri(move.Id!), out _));
        Assert.Empty(await bPersistence.Follows.GetFollowersAsync(_oldAliceIri));
        Assert.Empty(await bPersistence.Follows.GetFollowersAsync(newAliceIri));
    }

    // --- Helpers ----------------------------------------------------------------------

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
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

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
        });

    private static Move BuildMove(Iri oldActorIri, Iri newActorIri) => new()
    {
        Id = $"{oldActorIri}/moves/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(oldActorIri.Value) }],
        Object = [new Link { Href = new Uri(newActorIri.Value) }],
    };
}
