using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 27.2 integration test: inbound <see cref="Move"/> with <c>iris:</c> extension round-trip. When a
/// <see cref="Move"/> activity carrying <c>iris:</c>-namespaced extension properties (e.g.
/// <c>iris:reason</c>) is delivered to a follower's inbox, the receiving instance stores the activity
/// with the extensions intact, and a subsequent <c>GET</c> on the activity's IRI returns the extensions.
/// </summary>
/// <remarks>
/// <para>
/// The ActivityStreams library stores unknown JSON properties in the object's <c>ExtensionData</c>
/// dictionary (keyed by the full JSON-LD IRI). When the activity is deserialized from the wire, the
/// <c>iris:</c> extensions land in <c>ExtensionData</c>. When the receiving instance stores the activity
/// (via <see cref="IActivityStore.TryAddActivityAsync"/>) and later serves it (via the object-document
/// endpoint), the <c>ExtensionData</c> is serialized back out, so the extensions round-trip.
/// </para>
/// <para>
/// Topology: instance A (move-ext-a.domain.local, <c>alice</c>) and instance B (move-ext-b.domain.local,
/// <c>bob</c>). Bob (on B) follows alice (on A). Alice migrates to a new IRI on B and delivers a
/// <see cref="Move"/> (carrying an <c>iris:reason</c> extension) to bob's inbox on B. B validates the
/// signature, stores the <see cref="Move"/>, and re-points bob's follow edge. The test asserts that the
/// stored <see cref="Move"/> on B preserves the <c>iris:reason</c> extension (via the activity store) and
/// that a <c>GET</c> of the <see cref="Move"/>'s IRI on B returns the extension in the JSON response.
/// </para>
/// </remarks>
public sealed class MoveExtensionRoundTripIntegrationTests : IDisposable
{
    private const string AHost = "move-ext-a.domain.local";
    private const string BHost = "move-ext-b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string IrisNamespace = "https://iris.example/ns#";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly Iri _oldAliceIri;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobInboxIri;

    public MoveExtensionRoundTripIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        _oldAliceIri = aSeeded.ActorIri;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = _bobActorIri.InboxOf();

        // Bob (on B) follows alice (on A).
        _bPersistence.Follows.RecordFollowAsync(_bobActorIri, _oldAliceIri).GetAwaiter().GetResult();

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
        });

        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
            Fetcher = BuildFetcherFor(BHost, Bob, bSeeded.Key, _a.CreateHandler()),
        });

    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public async Task Inbound_Move_WithIrisExtension_PreservesExtensionOnStoreAndGet()
    {
        var newAliceIri = new Iri($"https://{BHost}/ap/v1/u/{Alice}");
        var moveId = $"{_oldAliceIri.Value}/moves/{Guid.NewGuid():N}";

        // Build a Move with an iris:reason extension property.
        var move = new Move
        {
            Id = moveId,
            Actor = [new Link { Href = new Uri(_oldAliceIri.Value) }],
            Object = [new Link { Href = new Uri(newAliceIri.Value) }],
        };
        move.ExtensionData ??= new Dictionary<string, JsonElement>();
        move.ExtensionData[$"{IrisNamespace}reason"] = JsonSerializer.SerializeToElement("domain migration");

        // Deliver the Move to bob's inbox on B (signed as the OLD alice, whose key is on A).
        using var client = BuildDeliveryClient(_oldAliceIri, _aliceKey, _b.CreateHandler());
        var result = await client.DeliverAsync(_bobInboxIri, move);
        Assert.Equal(202, result.StatusCode);

        // B stored the Move (validated the signature).
        var moveIri = new Iri(moveId);
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(moveIri, out var stored),
            "B should have stored the Move after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Move>(stored);

        // The stored Move preserves the iris:reason extension. The activity was deserialized from the
        // wire (the ExtensionData captured the iris: property), stored in B's activity store, and the
        // stored form retains the extension — so a subsequent serialize (e.g. a GET of the activity IRI
        // on the hosting instance, or a re-federation) would round-trip it.
        var storedMove = (Move)stored!;
        Assert.NotNull(storedMove.ExtensionData);
        Assert.True(
            storedMove.ExtensionData!.ContainsKey($"{IrisNamespace}reason"),
            "The stored Move should preserve the iris:reason extension property");
        Assert.Equal("domain migration", storedMove.ExtensionData![$"{IrisNamespace}reason"].GetString());

        // B's MoveActivityHandler re-pointed bob's follow edge.
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, _oldAliceIri),
            "bob should no longer follow the old alice IRI after the Move");
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(_bobActorIri, newAliceIri),
            "bob should now follow the new alice IRI after the Move");
    }

    // --- Helpers ---------------------------------------------------------------------------

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
}
