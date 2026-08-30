using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.27 end-to-end test (F-17 — inbound intransitive activities <see cref="Read"/>/
/// <see cref="View"/>/<see cref="Listen"/>/<see cref="Travel"/>/<see cref="Arrive"/>): an actor on
/// instance A (<c>alice</c>, <c>https://a.domain.local/…</c>) POSTs an intransitive activity to a local
/// actor's or community's inbox on instance B (<c>https://b.domain.local/…</c>). B validates the
/// signature (fetching alice's actor document from A to resolve her key), stores the activity, and the
/// <see cref="IntransitiveActivityHandler"/> accepts it as a no-op (an acknowledgment of receipt that
/// changes no persistent state).
/// </summary>
/// <remarks>
/// This proves the full inbound intransitive path end-to-end: signature validation (key resolution via
/// the sender's actor document) → store → interpret (no-op acknowledgment). Covers a <c>Read</c>
/// (accepted, stored, no state change), a <c>Travel</c> (the <see cref="IntransitiveActivity"/>
/// derivative, accepted, stored), and an unresolvable-key rejection (401).
/// </remarks>
public sealed class IntransitiveFederationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;
    private readonly Iri _communityIri;
    private readonly Iri _communityInboxIri;

    public IntransitiveFederationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (public actor document, so B can resolve alice's key to validate her signatures).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        // B hosts bob (the instance actor) and the community iris.
        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();
        _communityIri = TestSeeder.SeedCommunity(_bPersistence, BHost, Community);
        _communityInboxIri = new Iri($"{_communityIri.Value}/inbox");

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(
            BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()));
    }

    private readonly InMemoryPersistenceProvider _aPersistence;

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A signed Read is accepted, stored, and is a no-op ---------------------------------

    [Fact]
    public async Task Read_SignedByActor_DeliveredToActor_IsAcceptedNoOp()
    {
        var read = BuildRead(_aliceActorIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_bobInboxIri, read);
        Assert.Equal(202, result.StatusCode);

        // B validated the signature (by fetching alice's actor doc from A to resolve her key) and stored
        // the Read.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(read.Id!), out var stored),
            "B should have stored the Read after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Read>(stored);

        // The Read is an acknowledgment of receipt — no persistent state is changed (no community member
        // is added, no like/block edge is recorded). The seeded community is untouched.
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
        Assert.Empty(await _bPersistence.Likes.GetLikedAsync(_aliceActorIri));
    }

    // --- A signed Travel (the IntransitiveActivity derivative) is accepted and stored -------

    [Fact]
    public async Task Travel_SignedByActor_DeliveredToCommunity_IsAcceptedNoOp()
    {
        var travel = BuildTravel(_aliceActorIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_communityInboxIri, travel);
        Assert.Equal(202, result.StatusCode);

        // B validated the signature and stored the Travel (the IntransitiveActivity derivative).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(travel.Id!), out var stored),
            "B should have stored the Travel after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Travel>(stored);

        // The Travel is an acknowledgment of receipt — no community member is added (it is not an
        // Offer/Invite/Join) and no like/block edge is recorded.
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
    }

    // --- A Read signed by an unknown (unresolvable-key) actor is rejected -------------------

    [Fact]
    public async Task Read_SignedByUnknownActor_IsRejected()
    {
        // A sender whose actor IRI is not served by A (no key to validate against) → the signature
        // cannot be verified → the inbox rejects the delivery (401), and nothing is stored.
        var unknownActorIri = new Iri($"https://{AHost}/ap/v1/u/nobody");
        var unknownKey = KeyPairGenerator.GenerateRsa(new Iri($"{unknownActorIri.Value}#key-1"));
        var read = BuildRead(unknownActorIri);

        using var client = BuildDeliveryClient(unknownActorIri, unknownKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_bobInboxIri, read);
        Assert.Equal(401, result.StatusCode);

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(read.Id!), out _));
    }

    // --- Helpers ----------------------------------------------------------------------------

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

    private static Read BuildRead(Iri actorIri) => new()
    {
        Id = $"{actorIri.Value}/read-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri($"{actorIri.Value}/notes/n1") }],
    };

    private static Travel BuildTravel(Iri actorIri) => new()
    {
        Id = $"{actorIri.Value}/travel-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri($"{actorIri.Value}/notes/n1") }],
    };
}
