using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Phase 84.2 — <strong>Local key-rotation lifecycle</strong>: the follow-up 82.3 documented. A local
/// actor's signing key can be rotated (mint a new key at the next free fragment, re-register the
/// actor→key binding so the signer uses it, re-stamp the actor document's <c>publicKey</c> with a
/// <c>replaces</c> pointer), the old key stays in the store during the overlap window (old-key
/// signatures still verify), and <c>RetireKey</c> removes it once the rotation is confirmed (a signature
/// made before retirement still verifies against the already-advertised public key).
/// </summary>
public sealed class KeyRotationServiceTests
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";

    private sealed record Fixture
    (
        InMemoryPersistenceProvider Persistence,
        KeyRotationService Rotation,
        KeyPair OriginalKey,
        Iri ActorIri,
        Iri OriginalKeyId,
        InMemoryKeyProvider KeyProvider
    );

    private static Fixture NewFixture()
    {
        var persistence = new InMemoryPersistenceProvider();
        var (key, actorIri, keyId) = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);

        // Use the persistence's own key store (single source of truth): the seeder already put the key in
        // persistence.Keys, so the rotation service and the test assertions operate on the same store.
        var keyStore = persistence.Keys;
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, keyId);

        var rotation = new KeyRotationService(
            persistence,
            keyStore,
            keyProvider,
            NullLogger<KeyRotationService>.Instance);

        return new Fixture(persistence, rotation, key, actorIri, keyId, keyProvider);
    }

    // ------------------------------------------------- rotation: resolvable new key + advertised

    [Fact]
    public async Task Rotate_ProducesResolvableNewKey_AndActorDocumentAdvertisesIt()
    {
        var fx = NewFixture();
        var originalKeyId = fx.OriginalKeyId; // {actor}#key-1

        var newKeyId = await fx.Rotation.RotateAsync(fx.ActorIri);

        // The new key is at the next free fragment (#key-2), is distinct from the old, and is resolvable.
        Assert.Equal($"{fx.ActorIri}#key-2", newKeyId.Value);
        Assert.NotEqual(originalKeyId.Value, newKeyId.Value);
        Assert.True(fx.KeyProvider.TryGetIdentity(fx.ActorIri, out var identity)
                   && identity is { KeyId: var keyId } && keyId.Value == newKeyId.Value,
            "the actor must be bound to the new key after rotation");

        // The actor document now advertises the new key (id + replaces = the old key IRI).
        var stored = await fx.Persistence.Actors.TryGetActorAsync(fx.ActorIri, out var actor);
        Assert.True(stored && actor is not null, "the actor must still be stored after rotation");
        var advertised = actor!.GetPublicKeyIri();
        Assert.NotNull(advertised);
        Assert.True(newKeyId == advertised, "the actor document must advertise the new key");

        // The replaces pointer names the old key (the read-side RemoteInboundKeyResolver evicts it).
        var publicKeyExt = actor.ExtensionData![ActivityPubExtensionNames.PublicKey];
        Assert.Equal(originalKeyId.Value, publicKeyExt.GetProperty("replaces").GetString());
    }

    // ------------------------------------------------- overlap window: old key still verifies

    [Fact]
    public async Task OverlapWindow_OldKeySignature_StillVerifies()
    {
        var fx = NewFixture();
        var message = "a message signed before rotation";

        // A signature made with the ORIGINAL key before the rotation.
        var originalKey = fx.OriginalKey;
        var signature = originalKey.Sign(System.Text.Encoding.UTF8.GetBytes(message));

        await fx.Rotation.RotateAsync(fx.ActorIri);

        // During the overlap window the old key is still in the store (fragment-aware, so #key-1 and
        // #key-2 co-exist), so a signature made before the rotation still verifies.
        Assert.True(fx.Persistence.Keys.TryGetKey(fx.OriginalKeyId, out var oldKey) && oldKey is not null,
            "the old key must remain in the store during the overlap window");
        Assert.True(oldKey!.Verify(System.Text.Encoding.UTF8.GetBytes(message), signature),
            "a pre-rotation signature must still verify during the overlap window");
    }

    // ------------------------------------------------- retirement: old key gone, pre-retire sig verifies

    [Fact]
    public async Task RetireKey_RemovesOldKey_ButPreRetirementSignature_StillVerifies()
    {
        var fx = NewFixture();
        var message = "a message signed before rotation";

        // A signature made with the ORIGINAL key before the rotation, and the public PEM captured up front
        // (a peer that fetched/cached the actor document holds exactly this).
        var signature = fx.OriginalKey.Sign(System.Text.Encoding.UTF8.GetBytes(message));
        var advertisedPem = fx.OriginalKey.ExportPublicKeyPem();

        await fx.Rotation.RotateAsync(fx.ActorIri);

        // The public key was already advertised (the actor document carried the old publicKeyPem before
        // the rotation). Retire the old key: it is removed from the store (and disposed, releasing its
        // crypto resources), so no new signatures can be made with it — but a signature made before
        // retirement still verifies against the already-advertised public key.
        var retired = fx.Rotation.RetireKey(fx.OriginalKeyId);
        Assert.True(retired);
        Assert.False(fx.Persistence.Keys.TryGetKey(fx.OriginalKeyId, out _),
            "the retired key must be removed from the store");

        // The (already-advertised) public key still verifies the pre-retirement signature.
        var advertised = KeyPair.FromPem(advertisedPem, KeyAlgorithm.Rsa, fx.OriginalKeyId);
        Assert.True(advertised.Verify(System.Text.Encoding.UTF8.GetBytes(message), signature),
            "a pre-retirement signature must still verify against the already-advertised public key");
    }

    // ------------------------------------------------- repeated rotation: #key-3

    [Fact]
    public async Task SecondRotation_UsesNextFreeFragment_Key3()
    {
        var fx = NewFixture();

        var second = await fx.Rotation.RotateAsync(fx.ActorIri);
        var third = await fx.Rotation.RotateAsync(fx.ActorIri);

        Assert.Equal($"{fx.ActorIri}#key-2", second.Value);
        Assert.Equal($"{fx.ActorIri}#key-3", third.Value);

        // The actor is bound to the latest key; the actor document advertises it.
        Assert.True(fx.KeyProvider.TryGetIdentity(fx.ActorIri, out var identity)
                   && identity!.KeyId.Value == third.Value);
        var storedAgain = await fx.Persistence.Actors.TryGetActorAsync(fx.ActorIri, out var actorAgain);
        Assert.True(storedAgain && actorAgain is not null);
        Assert.True(third == actorAgain!.GetPublicKeyIri(), "the actor document must advertise the latest key");
    }
}
