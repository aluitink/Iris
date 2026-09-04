using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 26 slice 26.4 end-to-end test (F-25 — <see cref="Iris.Server.Inbox.MoveActivityHandler"/>'s
/// key-rotation cache invalidation): a remote actor (alice, on A) migrates to a new IRI with a <em>new</em>
/// signing key. The receiving instance (B) has already validated a delivery from the <em>old</em> alice
/// (caching her actor document in <see cref="Iris.Server.Security.RemoteActorCache"/> and her public key
/// in <see cref="Iris.Server.Security.RemoteKeyCache"/>). When the <see cref="Move"/> is delivered, B's
/// <see cref="Iris.Server.Inbox.MoveActivityHandler"/> re-points the local follow edge <em>and</em> clears
/// the moving actor's stale cache entries, so the next key resolution fetches fresh rather than serving
/// the stale cached old key.
/// </summary>
/// <remarks>
/// Two live in-process <see cref="TestServer"/> instances (A and B). B's bob follows the old alice (on A).
/// The test runs three phases:
/// <list type="number">
/// <item><description><strong>Warm.</strong> The old alice (old IRI, old key) delivers a signed
/// <see cref="Follow"/> to bob. B resolves her key by fetching A's actor document and <em>caches</em> it
/// (old actor doc in the <see cref="Iris.Server.Security.RemoteActorCache"/>, old key in the
/// <see cref="Iris.Server.Security.RemoteKeyCache"/>). The delivery is accepted (202).</description></item>
/// <item><description><strong>Move.</strong> The old alice delivers a <see cref="Move"/> (old IRI → new
/// IRI, the new IRI living on A with a new key). B validates it (the old key is still valid on A at this
/// point) and the <see cref="Iris.Server.Inbox.MoveActivityHandler"/> re-points bob's edge to the new IRI
/// and invalidates the old IRI's cache entries.</description></item>
/// <item><description><strong>Rotate.</strong> A completes the migration: the old IRI's actor document is
/// decommissioned (removed) and the new IRI's document (carrying the new key) is already seeded. Now a
/// delivery signed with the <em>old</em> key is rejected (401) — B's cache for the old key IRI was cleared
/// by the <see cref="Move"/>, so B re-fetches A's old IRI, gets 404, and cannot resolve the key — whereas
/// a delivery signed with the <em>new</em> key is accepted (202) — B resolves the new key by fetching the
/// new actor document fresh.</description></item>
/// </list>
/// The non-vacuous signal is the contrast in phase 3: the <em>same</em> old-key signature that was accepted
/// in phase 1 is rejected after the <see cref="Move"/>. Without the <see cref="Move"/>'s cache invalidation,
/// B's <see cref="Iris.Server.Security.RemoteKeyCache"/> would still hold the old key and the phase-3
/// old-key delivery would be accepted (202) from the stale cache — so the 401 proves the invalidation ran.
/// </remarks>
public sealed class MoveKeyRotationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string NewAlice = "alice-2";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;

    private readonly KeyPair _oldAliceKey;
    private readonly Iri _oldAliceIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;

    public MoveKeyRotationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _oldAliceKey = aSeeded.Key;
        _oldAliceIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();

        // bob (on B) follows the old alice (on A): the edge is recorded in B's follow store.
        _bPersistence.Follows.RecordFollowAsync(_bobActorIri, _oldAliceIri).GetAwaiter().GetResult();

        // B's inbound key resolution must read the SAME RemoteActorCache the DI registers, so the
        // MoveActivityHandler's RemoteActorCache.Invalidate(oldIri) clears the entry the fetcher reads.
        // (The RemoteKeyCache is the DI default; the handler and the validator both resolve it by type.)
        var bActorCache = new RemoteActorCache();
        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(
            BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler(), bActorCache),
            extraServices: s => s.AddSingleton(bActorCache));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public async Task Move_InvalidateOldKey_OldKeyRejectedAfterMove_NewKeyAcceptedAfterMove()
    {
        var bKeyCache = _b.Services.GetRequiredService<RemoteKeyCache>();

        // --- Phase 1: warm B's caches with the old alice's key -------------------------------
        // The old alice (old IRI, old key) delivers a Follow. B resolves her key by fetching A's actor
        // document and caches both the actor doc (RemoteActorCache[oldIri]) and the key
        // (RemoteKeyCache[oldIri#key-1]). Accepted.
        var follow1 = BuildFollow(_oldAliceIri);
        using (var client1 = BuildDeliveryClient(_oldAliceIri, _oldAliceKey, _b.CreateHandler()))
        {
            var result1 = await client1.DeliverAsync(_bobInboxIri, follow1);
            Assert.True(
                result1.StatusCode == 202,
                $"Phase 1: expected 202 (old-key follow accepted, caches warm), got {result1.StatusCode}");
        }

        // B's key cache holds exactly the old key (the stale entry the Move must clear).
        Assert.True(
            bKeyCache.Count == 1,
            $"Phase 1: B's key cache must hold the old alice's key after the warm delivery (count={bKeyCache.Count})");

        // --- Phase 2: the Move (old IRI -> new IRI on A, new key) ----------------------------
        // The new IRI lives on A (same host, new path). Seed the new alice on A with her own key (so B can
        // fetch her document later and we can sign as her). The Move itself is signed by the OLD alice
        // (her key is still valid on A at this point), so B validates it against the still-valid old key.
        var newAliceSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, NewAlice);
        var newAliceIri = newAliceSeeded.ActorIri;
        var newAliceKey = newAliceSeeded.Key;

        var move = BuildMove(_oldAliceIri, newAliceIri);
        using (var clientMove = BuildDeliveryClient(_oldAliceIri, _oldAliceKey, _b.CreateHandler()))
        {
            var resultMove = await clientMove.DeliverAsync(_bobInboxIri, move);
            Assert.True(
                resultMove.StatusCode == 202,
                $"Phase 2: expected 202 (Move validated + re-pointed + caches invalidated), got {resultMove.StatusCode}");
        }

        // B's MoveActivityHandler re-pointed bob's edge to the new IRI (the old edge is gone).
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, _oldAliceIri),
            "Phase 2: bob should no longer follow the old alice IRI after the Move");
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, newAliceIri),
            "Phase 2: bob should now follow the new alice IRI after the Move");

        // The Move cleared the old IRI's cache entries (F-25). B's key cache no longer holds the old key.
        Assert.True(
            bKeyCache.Count == 0,
            $"Phase 2: B's key cache must be cleared for the old alice's key IRI after the Move (F-25 invalidation) (count={bKeyCache.Count})");

        // --- Phase 3: complete the migration on A -------------------------------------------
        // Decommission the old IRI (remove its actor doc so B's re-fetch gets 404) and invalidate A's
        // local actor-document cache so A's endpoint reflects the removal.
        await _aPersistence.ActorStore.RemoveActorAsync(_oldAliceIri);
        _a.Services.GetRequiredService<LocalActorDocumentCache>().Invalidate(_oldAliceIri);

        // 3a: a delivery signed with the OLD key is now REJECTED. B's cache for the old key IRI was
        // cleared by the Move, so B re-fetches A's old IRI (now 404) and cannot resolve the key.
        // Without the Move's invalidation, B would serve the stale cached old key and accept this (202).
        var followOld = BuildFollow(_oldAliceIri);
        using (var clientOld = BuildDeliveryClient(_oldAliceIri, _oldAliceKey, _b.CreateHandler()))
        {
            var resultOld = await clientOld.DeliverAsync(_bobInboxIri, followOld);
            Assert.True(
                resultOld.StatusCode == 401,
                $"Phase 3a: expected 401 (old key rejected after Move invalidated the cache), got {resultOld.StatusCode}");
        }

        // 3b: a delivery signed with the NEW key (new IRI) is ACCEPTED. B resolves the new key by fetching
        // the new actor document fresh (the new IRI was never cached).
        var followNew = BuildFollow(newAliceIri);
        using (var clientNew = BuildDeliveryClient(newAliceIri, newAliceKey, _b.CreateHandler()))
        {
            var resultNew = await clientNew.DeliverAsync(_bobInboxIri, followNew);
            Assert.True(
                resultNew.StatusCode == 202,
                $"Phase 3b: expected 202 (new key accepted via fresh fetch of the new actor doc), got {resultNew.StatusCode}");
        }

        // B stored the new-key follow (the new alice is now a known, validated actor on B).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(followNew.Id!), out var stored),
            "Phase 3b: B must have stored the follow signed with the new key");
        Assert.NotNull(stored);
        Assert.Equal(followNew.Id, stored!.Id);
    }

    // --- Helpers ------------------------------------------------------------------------

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
        string host, string handle, KeyPair bobKey, HttpMessageHandler handler,
        RemoteActorCache? actorCache = null)
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
            handler);

        return new IrisActorDocumentFetcher(client, actorCache ?? new RemoteActorCache());
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        Action<IServiceCollection>? extraServices = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            ExtraServices = extraServices,
        });

    private static Follow BuildFollow(Iri actorIri)
    {
        var bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        return new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(bobActorIri.Value) }],
        };
    }

    private static Move BuildMove(Iri oldActorIri, Iri newActorIri) => new()
    {
        Id = $"{oldActorIri.Value}/moves/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(oldActorIri.Value) }],
        Object = [new Link { Href = new Uri(newActorIri.Value) }],
    };
}
