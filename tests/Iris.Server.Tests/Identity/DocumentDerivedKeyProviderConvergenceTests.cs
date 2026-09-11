using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Convergence tests for <see cref="DocumentDerivedKeyProvider"/> (Phase 84.6, shared-state scale-out —
/// the convergence half). These prove the two-instance divergence is real and that the provider's
/// <c>RefreshFromActorsAsync</c> converges a running instance to a rotation performed on <em>another</em>
/// instance over the same persistence — no restart.
/// </summary>
/// <remarks>
/// Two <see cref="DocumentDerivedKeyProvider"/> instances share one <see cref="InMemoryPersistenceProvider"/>
/// (its actor store + key store) — the topology of two app instances over one origin. Instance A rotates
/// (a <see cref="KeyRotationService"/> over the shared store updates the shared key store + the persisted
/// actor document + A's own provider map). Instance B's map is untouched by A's rotation (the in-process
/// map is per-instance) — so before a refresh, B still signs with the <em>old</em> key (the divergence).
/// After B runs <c>RefreshFromActorsAsync</c>, B's map re-derives from the persisted document and B signs
/// with the <em>new</em> key (convergence).
/// </remarks>
public sealed class DocumentDerivedKeyProviderConvergenceTests
{
    [Fact]
    public async Task RotateOnInstanceA_InstanceB_ConvergesOnRefresh()
    {
        // One shared persistence = two instances over one origin.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, originalKeyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        // Two providers over the same key store (one per instance). Both start by refreshing from the
        // shared documents, so both resolve the seeded #key-1.
        var instanceA = new DocumentDerivedKeyProvider(persistence.Keys);
        var instanceB = new DocumentDerivedKeyProvider(persistence.Keys);
        Assert.Equal(1, await instanceA.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));
        Assert.Equal(1, await instanceB.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));

        // Both resolve the seeded key (baseline: converged at #key-1).
        Assert.True(instanceA.TryGetIdentity(actorIri, out var aBefore));
        Assert.True(instanceB.TryGetIdentity(actorIri, out var bBefore));
        Assert.Equal(originalKeyId, aBefore!.KeyId);
        Assert.Equal(originalKeyId, bBefore!.KeyId);

        // Instance A rotates: mints #key-2, stores it in the SHARED key store, re-stamps the actor
        // document, and re-binds A's OWN provider map. B's map is untouched (per-instance).
        var rotation = new KeyRotationService(
            persistence,
            persistence.Keys,
            instanceA,
            NullLogger<KeyRotationService>.Instance);
        var newKeyId = await rotation.RotateAsync(actorIri);
        Assert.NotEqual(originalKeyId, newKeyId);

        // Divergence: A signs with the new key, B still signs with the old key (its map was not updated
        // by A's rotation). This is the silent failure the provider's refresh is meant to close.
        Assert.True(instanceA.TryGetIdentity(actorIri, out var aAfter));
        Assert.Equal(newKeyId, aAfter!.KeyId);
        Assert.True(instanceB.TryGetIdentity(actorIri, out var bStale));
        Assert.Equal(originalKeyId, bStale!.KeyId);

        // Convergence: B re-derives its map from the persisted document (which A re-stamped) and now
        // resolves the new key — no restart.
        Assert.Equal(1, await instanceB.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));
        Assert.True(instanceB.TryGetIdentity(actorIri, out var bConverged));
        Assert.Equal(newKeyId, bConverged!.KeyId);
    }

    [Fact]
    public async Task Refresh_SkipsRetiredKey_DropsActorFromMap()
    {
        // When the current key is retired (removed from the store) and not re-rotated, the refresh does
        // not (re)bind it — the actor drops out of the map (TryGetIdentity fails), rather than pointing at
        // a key that no longer exists.
        var persistence = new InMemoryPersistenceProvider();
        var (_, actorIri, keyId) = TestSeeder.SeedPersonWithKey(persistence, "a.domain.local", "alice");

        var provider = new DocumentDerivedKeyProvider(persistence.Keys);
        Assert.Equal(1, await provider.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));
        Assert.True(provider.TryGetIdentity(actorIri, out var before));
        Assert.Equal(keyId, before!.KeyId);

        // Retire the only key (the document still advertises it, but the material is gone).
        Assert.True(persistence.Keys.RemoveKey(keyId));

        // The refresh re-derives the same key IRI from the document, but the store no longer has it →
        // the actor is dropped from the map.
        Assert.Equal(0, await provider.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));
        Assert.False(provider.TryGetIdentity(actorIri, out _));
    }

    [Fact]
    public async Task Refresh_LegacyActorWithoutPublicKey_FallsBackToKey1()
    {
        // A legacy actor whose document has no publicKey extension is bound to the #key-1 convention (the
        // key must be present in the store).
        var persistence = new InMemoryPersistenceProvider();
        var actorIri = TestSeeder.SeedPerson(persistence, "a.domain.local", "legacy");
        var keyId = new Iri($"{actorIri}#key-1");
        persistence.Keys.PutKey(KeyPairGenerator.GenerateRsa(keyId));

        var provider = new DocumentDerivedKeyProvider(persistence.Keys);
        Assert.Equal(1, await provider.RefreshFromActorsAsync(persistence.Actors, persistence.Keys));
        Assert.True(provider.TryGetIdentity(actorIri, out var identity));
        Assert.Equal(keyId, identity!.KeyId);
    }
}
