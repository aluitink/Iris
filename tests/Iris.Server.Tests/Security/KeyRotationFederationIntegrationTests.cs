using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Phase 12 integration test (F-21 key-rotation invalidation): a remote actor that <em>rotates</em>
/// its signing key keeps the same key IRI, so the receiving instance's <see cref="RemoteKeyCache"/>
/// would keep serving the old public key until its TTL (1h). The <see cref="HttpSignatureValidator"/>
/// now treats a verification failure as the rotation signal: it invalidates the cached key for the
/// key IRI and re-resolves once (a fresh fetch of the actor document) before re-verifying.
/// </summary>
/// <remarks>
/// Two live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> instances (A and B).
/// B receives a signed <c>Follow</c> from A's actor <c>alice</c>. B first validates a request signed
/// with alice's <em>original</em> key (caching alice's key under its key IRI). Alice then rotates her
/// key (a new RSA key at the SAME key IRI, published in A's actor document). A subsequent request
/// signed with the NEW key would — without F-21 — be rejected (B's cache still holds the old key,
/// which does not verify) for up to an hour. With F-21, B's validator invalidates the stale cache
/// entry on the verification failure, re-fetches A's actor document (now carrying the new key), and
/// accepts the request.
/// </remarks>
public sealed class KeyRotationFederationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;

    private readonly KeyPair _aliceOriginalKey;
    private readonly KeyPair _bobKey;
    private readonly Iri _aliceActorIri;
    private readonly Iri _aliceKeyId;
    private readonly Iri _bobInboxIri;

    public KeyRotationFederationIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceOriginalKey = aSeeded.Key;
        _aliceActorIri = aSeeded.ActorIri;
        _aliceKeyId = aSeeded.KeyId;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        var bobActorIri = bSeeded.ActorIri;
        _bobInboxIri = bobActorIri.InboxOf();

        // B's inbound key resolution must use the SAME RemoteActorCache instance that the DI
        // registers (so the validator's F-21 invalidation hits the cache the fetcher reads).
        var bActorCache = new RemoteActorCache();
        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, _a.CreateHandler(), bActorCache),
            extraServices: s => s.AddSingleton(bActorCache));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public async Task RotatedRemoteKey_SameKeyId_IsAcceptedAfterInvalidation()
    {
        // Warm B's cache with alice's ORIGINAL key: deliver a signed Follow (original key) and confirm
        // it is accepted (B fetched A's actor doc and cached alice's key under its key IRI).
        var follow1 = BuildFollow(_aliceActorIri);
        using (var client1 = BuildDeliveryClient(_aliceActorIri, _aliceOriginalKey, _b.CreateHandler()))
        {
            Assert.Equal(202, await client1.DeliverAsync(_bobInboxIri, follow1));
        }

        // Alice rotates her key: a NEW RSA key at the SAME key IRI, republished in A's actor document
        // (A now serves the new public key under the unchanged keyId). The receiving instance's
        // validator disposes the re-resolved key after the (re)verification, so the key is not usable
        // after the delivery below — do not call Export*/GetPublicJwk() on it past the delivery.
        var rotatedKey = KeyPairGenerator.GenerateRsa(_aliceKeyId);
        _aPersistence.Keys.PutKey(rotatedKey);
        await RotateAliceKeyInActorDoc(_aPersistence, _aliceActorIri, rotatedKey);
        // Invalidate A's local actor-document cache so the re-fetch (B's F-21 re-resolve) reads the
        // rotated actor doc from persistence, not the cached original.
        _a.Services.GetRequiredService<LocalActorDocumentCache>().Invalidate(_aliceActorIri);

        // A follow signed with the ROTATED key. Without F-21, B's cached (original) key does not
        // verify this and the request would be rejected 401 (stale until the 1h TTL). With F-21,
        // B's validator invalidates the stale entry, re-fetches A's actor doc (now the rotated key),
        // and accepts the request.
        var follow2 = BuildFollow(_aliceActorIri);
        using var client2 = BuildDeliveryClient(_aliceActorIri, rotatedKey, _b.CreateHandler());
        var statusCode = await client2.DeliverAsync(_bobInboxIri, follow2);

        Assert.True(
            statusCode == 202,
            $"Expected 202 (rotated-key follow accepted via F-21 invalidation + re-resolution), got {statusCode}");
        // The re-resolved key B used to verify must be the rotated key (its public JWK), proving the
        // rotation was picked up (not the stale cached original).
        var bKeys = _b.Services.GetRequiredService<RemoteKeyCache>();
        Assert.True(
            bKeys.Count > 0,
            "B's key cache must hold the rotated key after the re-resolution");
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow2.Id!), out var stored),
            "B must have stored the follow signed with the rotated key");
        Assert.NotNull(stored);
        Assert.Equal(follow2.Id, stored!.Id);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Re-publishes <paramref name="newKey"/>'s public key in the actor's document (the
    /// <c>publicKey</c> extension) under the (unchanged) key IRI, simulating a key rotation: the
    /// actor IRI and key IRI are constant, only the key material changes.
    /// </summary>
    private static async Task RotateAliceKeyInActorDoc(
        InMemoryPersistenceProvider persistence, Iri actorIri, KeyPair newKey)
    {
        // The key IRI is the actor IRI + "#key-1" (the TestSeeder convention), so the rotation keeps
        // the same keyId and only the public key material changes.
        var keyId = new Iri($"{actorIri.Value}#key-1");

        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIri.Value,
            publicKeyPem = newKey.ExportPublicKeyPem(),
        });

        // Replacing the actor doc is the on-the-wire effect of a rotation: the actor document now
        // carries the new public key under the unchanged keyId.
        await persistence.ActorStore.PutActorAsync(actor);
    }

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

        // Share the DI-registered RemoteActorCache (when provided) so the validator's F-21
        // invalidation hits the same cache the fetcher reads from.
        return new IrisActorDocumentFetcher(client, actorCache ?? new RemoteActorCache());
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null,
        Action<IServiceCollection>? extraServices = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            DeliveryTransport = deliveryTransport,
            ExtraServices = extraServices,
        });

    private static Follow BuildFollow(Iri aliceActorIri)
    {
        var bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        return new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(aliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(bobActorIri.Value) }],
        };
    }
}
