using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.25 end-to-end test (F-16 — inbound community-membership primitives
/// <see cref="Offer"/>/<see cref="Invite"/>/<see cref="Join"/>/<see cref="Leave"/>): a community on
/// instance B (<c>iris</c>, <c>https://b.domain.local/…</c>) is administered by a server that manages
/// its membership via the ActivityStreams membership primitives rather than a <see cref="Follow"/> or
/// <c>Add</c>/<c>Remove</c>. A client signed as the remote actor <c>alice</c> (on instance A,
/// <c>https://a.domain.local/…</c>) POSTs a membership activity to the community's inbox — B validates
/// the signature (fetching alice's actor document from A to resolve her key), then B's
/// <see cref="MembershipActivityHandler"/> updates the community's member set.
/// </summary>
/// <remarks>
/// This proves the full inbound membership path end-to-end: signature validation (key resolution via
/// the sender's actor document) → store → interpret (modify the local community's member set). Covers
/// an <c>Invite</c> (adds the invited actor), a <c>Leave</c> (removes a member), the person-recipient
/// no-op, and an unresolvable-key rejection (401).
/// </remarks>
public sealed class MembershipFederationIntegrationTests : IDisposable
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
    private readonly Iri _aliceActorIri;
    private readonly Iri _communityIri;
    private readonly Iri _communityInboxIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;

    public MembershipFederationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (public actor document, so B can resolve alice's key to validate her signatures).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        // B hosts bob (the instance actor) and the community iris (the membership being changed).
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

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A signed Invite adds the invited actor to the local community's member set ----------

    [Fact]
    public async Task Invite_SignedByActor_DeliveredToCommunity_AddsMember()
    {
        var invite = BuildInvite(_aliceActorIri, _communityIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_communityInboxIri, invite);
        Assert.Equal(202, result.StatusCode);

        // B validated the signature (by fetching alice's actor doc from A to resolve her key) and stored
        // the Invite.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(invite.Id!), out var stored),
            "B should have stored the Invite after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Invite>(stored);

        // B's MembershipActivityHandler added the invited actor to the community's member set.
        Assert.True(
            await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri),
            "the invited actor should be a member of the community after the Invite");
    }

    // --- A signed Leave removes the actor from the local community's member set --------------

    [Fact]
    public async Task Leave_SignedByActor_DeliveredToCommunity_RemovesMember()
    {
        // Seed alice as an existing member (as a prior Invite or a follow would have recorded her).
        await _bPersistence.Communities.AddMemberAsync(_communityIri, _aliceActorIri);
        Assert.True(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));

        var leave = BuildLeave(_aliceActorIri, _communityIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_communityInboxIri, leave);
        Assert.Equal(202, result.StatusCode);

        // B stored the Leave and removed alice from the community's member set.
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(leave.Id!), out var stored));
        Assert.IsType<Leave>(stored);
        Assert.False(
            await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri),
            "alice should no longer be a member of the community after the Leave");
    }

    // --- A signed Join adds the joining actor to the local community's member set ------------

    [Fact]
    public async Task Join_SignedByActor_DeliveredToCommunity_AddsMember()
    {
        var join = BuildJoin(_aliceActorIri, _communityIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_communityInboxIri, join);
        Assert.Equal(202, result.StatusCode);

        // B stored the Join and added the joining actor to the community's member set.
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(join.Id!), out var stored));
        Assert.IsType<Join>(stored);
        Assert.True(
            await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri),
            "alice should be a member of the community after the Join");
    }

    // --- A signed Invite to a local person is a no-op (a person has no member set) -----------

    [Fact]
    public async Task Invite_SignedByActor_DeliveredToPerson_IsNoOp()
    {
        var invite = BuildInvite(_aliceActorIri, _bobActorIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_bobInboxIri, invite);
        Assert.Equal(202, result.StatusCode);

        // The Invite is stored (validated), but a person has no member set to add to. The recipient is
        // a person, so no community membership is recorded (the seeded community iris is untouched).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(invite.Id!), out _));
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
    }

    // --- An Invite signed by an unknown (unresolvable-key) actor is rejected -----------------

    [Fact]
    public async Task Invite_SignedByUnknownActor_IsRejected()
    {
        // A sender whose actor IRI is not served by A (no key to validate against) → the signature
        // cannot be verified → the inbox rejects the delivery (401), and nothing is stored or added.
        var unknownActorIri = new Iri($"https://{AHost}/ap/v1/u/nobody");
        var unknownKey = KeyPairGenerator.GenerateRsa(new Iri($"{unknownActorIri.Value}#key-1"));
        var invite = new Invite
        {
            Id = $"{unknownActorIri.Value}/invite-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_aliceActorIri.Value) }],
        };

        using var client = BuildDeliveryClient(unknownActorIri, unknownKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_communityInboxIri, invite);
        Assert.Equal(401, result.StatusCode);

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(invite.Id!), out _));
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
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

    private static Invite BuildInvite(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"{actorIri.Value}/invite-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(actorIri.Value) }],
    };

    private static Leave BuildLeave(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"{actorIri.Value}/leave-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(actorIri.Value) }],
    };

    private static Join BuildJoin(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"{actorIri.Value}/join-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(actorIri.Value) }],
    };
}
