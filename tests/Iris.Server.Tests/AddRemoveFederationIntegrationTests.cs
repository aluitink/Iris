using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.20 / Phase 19.5.2 end-to-end test (F-09 — inbound <see cref="Add"/>/
/// <see cref="Remove"/> collection-modification primitives, with the 19.5.2 self-management gate): a
/// community on instance B (<c>iris</c>, <c>https://b.domain.local/…</c>) is managed via the
/// ActivityStreams <c>Add</c>/<c>Remove</c> primitives. A client signed as the remote actor <c>alice</c>
/// (on instance A, <c>https://a.domain.local/…</c>) POSTs an <c>Add</c> to the community's inbox — B
/// validates the signature (fetching alice's actor document from A to resolve her key) and stores the
/// activity, but B's <see cref="AddActivityHandler"/> now applies the 19.5.2 authorization: only the
/// community manages its own membership, so an <c>Add</c> whose <em>actor is a different actor</em> does
/// NOT add that actor as a member. The same holds for <see cref="RemoveActivityHandler"/> (a
/// <c>Remove</c> posted by alice does not remove alice).
/// </summary>
/// <remarks>
/// This proves the full inbound <c>Add</c>/<c>Remove</c> path end-to-end: signature validation (key
/// resolution via the sender's actor document) → store → interpret (the 19.5.2 self-management gate
/// rejects a membership edit whose actor is not the community). The community's own membership
/// management (a community-signed <c>Add</c>/<c>Remove</c> that DOES modify the member set, with the
/// community feed and <c>members</c> collection reflecting the change) is covered by
/// <see cref="CommunityMembershipManagementIntegrationTests"/>. A third test covers the no-op when the
/// recipient is a local person (a person's followers are maintained by the follow lifecycle, not
/// <c>Add</c>/<c>Remove</c>).
/// </remarks>
public sealed class AddRemoveFederationIntegrationTests : IDisposable
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

    public AddRemoveFederationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (public actor document, so B can resolve alice's key to validate her signatures).
        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;

        // B hosts bob (the instance actor) and the community iris (the collection being modified).
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

    // --- A signed Add by a remote actor does NOT add that actor (19.5.2 self-management) -----

    [Fact]
    public async Task Add_SignedByRemoteActor_DeliveredToCommunity_DoesNotAddMember()
    {
        var add = BuildAdd(_aliceActorIri, _communityIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_communityInboxIri, add);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature (by fetching alice's actor doc from A to resolve her key) and stored
        // the Add.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(add.Id!), out var stored),
            "B should have stored the Add after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Add>(stored);

        // The 19.5.2 gate: only the community manages its own membership. The Add's actor is alice (not
        // the community), so B's AddActivityHandler does NOT add alice as a member.
        Assert.False(
            await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri),
            "an Add whose actor is not the community must not add a member (19.5.2 self-management gate)");
    }

    // --- A signed Remove by a remote actor does NOT remove that actor (19.5.2 self-management) -

    [Fact]
    public async Task Remove_SignedByRemoteActor_DeliveredToCommunity_DoesNotRemoveMember()
    {
        // Seed alice as an existing member (as a prior community-managed Add would have recorded her).
        await _bPersistence.Communities.AddMemberAsync(_communityIri, _aliceActorIri);
        Assert.True(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));

        var remove = BuildRemove(_aliceActorIri, _communityIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_communityInboxIri, remove);
        Assert.Equal(202, statusCode.StatusCode);

        // B stored the Remove.
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(remove.Id!), out var stored));
        Assert.IsType<Remove>(stored);

        // The 19.5.2 gate: the Remove's actor is alice (not the community), so B's RemoveActivityHandler
        // does NOT remove alice from the member set.
        Assert.True(
            await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri),
            "a Remove whose actor is not the community must not remove a member (19.5.2 self-management gate)");
    }

    // --- A signed Add to a local person is a no-op (follow lifecycle owns person followers) ---

    [Fact]
    public async Task Add_SignedByActor_DeliveredToPerson_IsNoOp()
    {
        var add = BuildAdd(_aliceActorIri, _bobActorIri);

        using var client = BuildDeliveryClient(_aliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_bobInboxIri, add);
        Assert.Equal(202, statusCode.StatusCode);

        // The Add is stored (validated), but a person's followers are not modified by Add/Remove. The
        // recipient is a person, so no community membership is recorded (the seeded community iris is
        // untouched) and no follow edge is created (a person's followers are owned by the follow
        // lifecycle, not Add/Remove).
        Assert.True(await _bPersistence.Activities.TryGetActivityAsync(new Iri(add.Id!), out _));
        Assert.False(await _bPersistence.Communities.IsMemberAsync(_communityIri, _aliceActorIri));
        Assert.False(await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, _aliceActorIri));
    }

    // --- An Add signed by an unknown (unresolvable-key) actor is rejected ---------------------

    [Fact]
    public async Task Add_SignedByUnknownActor_IsRejected()
    {
        // A sender whose actor IRI is not served by A (no key to validate against) → the signature
        // cannot be verified → the inbox rejects the delivery (401), and nothing is stored or added.
        var unknownActorIri = new Iri($"https://{AHost}/ap/v1/u/nobody");
        var unknownKey = KeyPairGenerator.GenerateRsa(new Iri($"{unknownActorIri.Value}#key-1"));
        var add = new Add
        {
            Id = $"{unknownActorIri.Value}/add-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(_aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(_communityIri.Value) }],
        };

        using var client = BuildDeliveryClient(unknownActorIri, unknownKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(_communityInboxIri, add);
        Assert.Equal(401, statusCode.StatusCode);

        Assert.False(await _bPersistence.Activities.TryGetActivityAsync(new Iri(add.Id!), out _));
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

    private static Add BuildAdd(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"{actorIri.Value}/add-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(actorIri.Value) }],
    };

    private static Remove BuildRemove(Iri actorIri, Iri communityIri) => new()
    {
        Id = $"{actorIri.Value}/remove-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(actorIri.Value) }],
        Object = [new Link { Href = new Uri(actorIri.Value) }],
    };
}
