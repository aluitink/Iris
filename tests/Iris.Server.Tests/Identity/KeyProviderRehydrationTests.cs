using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Server.Identity;
using Iris.Testing;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Identity;

/// <summary>
/// Phase 84.4 — <strong>Key-rotation durability</strong>: the in-memory actor→key binding
/// (<see cref="IKeyProvider"/>) is wiped on a host restart. <see cref="KeyProviderRehydration"/> is the
/// restart-restore path — it re-derives each local actor's key IRI from the durable actor document's
/// <c>publicKey.id</c> (re-stamped on every rotation, 84.2/84.3) and re-registers it. These unit tests
/// cover the resolution rules: a rotated key (<c>#key-2</c>) wins over the <c>#key-1</c> convention, a
/// legacy actor (no <c>publicKey</c>) falls back to <c>#key-1</c>, and a retired/absent key is skipped.
/// </summary>
public sealed class KeyProviderRehydrationTests
{
    private const string Host = "a.domain.local";
    private const string Handle = "iris";

    private readonly InMemoryPersistenceProvider _persistence = new();

    private async Task<(IKeyProvider Provider, InMemoryPersistenceProvider Persistence, Iri ActorIri, Iri OriginalKeyId)> SeedRotatedActorAsync()
    {
        // Seed the actor + its original key (#key-1), then simulate a rotation: mint #key-2, put it in the
        // store, re-stamp the actor document's publicKey.id to #key-2 (what KeyRotationService.RotateAsync does).
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);
        var actorIri = seeded.ActorIri;
        var originalKeyId = seeded.KeyId;
        var rotatedKeyId = new Iri($"{actorIri}#key-2");

        _persistence.Keys.PutKey(KeyPairGenerator.GenerateRsa(rotatedKeyId));

        // Re-stamp the stored actor document's publicKey.id to the rotated key (mirroring RotateAsync).
        var stored = await _persistence.Actors.TryGetActorAsync(actorIri, out var actor);
        Assert.True(stored && actor is not null);
        actor!.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
        {
            id = rotatedKeyId.Value,
            owner = actorIri.Value,
            publicKeyPem = _persistence.Keys.TryGetKey(rotatedKeyId, out var rotatedKey) && rotatedKey is not null
                ? rotatedKey.ExportPublicKeyPem()
                : throw new InvalidOperationException("rotated key must be in the store"),
            replaces = originalKeyId.Value,
        });
        await _persistence.Actors.PutActorAsync(actor);

        // A fresh in-memory provider (the "post-restart" state: empty bindings).
        var provider = new InMemoryKeyProvider(_persistence.Keys);
        return (provider, _persistence, actorIri, originalKeyId);
    }

    [Fact]
    public async Task Rehydrate_RotatedActor_RegistersCurrentKey_NotStaleKey1()
    {
        var (provider, _, actorIri, originalKeyId) = await SeedRotatedActorAsync();
        var rotatedKeyId = new Iri($"{actorIri}#key-2");

        var registered = await KeyProviderRehydration.RehydrateFromActorsAsync(provider, _persistence.Actors, _persistence.Keys);

        Assert.Equal(1, registered);
        // The rehydration must bind the actor to the ROTATED key (#key-2), not the stale #key-1.
        Assert.True(provider.TryGetIdentity(actorIri, out var identity) && identity is not null);
        Assert.Equal(rotatedKeyId, identity!.KeyId);
        Assert.NotEqual(originalKeyId, identity.KeyId);
    }

    [Fact]
    public async Task Rehydrate_LegacyActorWithoutPublicKey_FallsBackToKey1()
    {
        // A legacy actor: stored with no publicKey extension, but a #key-1 key present in the store.
        var legacyActor = new Person
        {
            Id = $"https://{Host}/ap/v1/u/legacy",
            PreferredUsername = "legacy",
            Name = ["legacy"],
        };
        var legacyKeyIri = new Iri($"{legacyActor.Id}#key-1");
        _persistence.Keys.PutKey(KeyPairGenerator.GenerateRsa(legacyKeyIri));
        await _persistence.Actors.PutActorAsync(legacyActor);

        var provider = new InMemoryKeyProvider(_persistence.Keys);
        var registered = await KeyProviderRehydration.RehydrateFromActorsAsync(provider, _persistence.Actors, _persistence.Keys);

        Assert.Equal(1, registered);
        Assert.True(provider.TryGetIdentity(new Iri(legacyActor.Id!), out var identity) && identity is not null);
        Assert.Equal(legacyKeyIri, identity!.KeyId);
    }

    [Fact]
    public async Task Rehydrate_RetiredKeyIsSkipped_NotRegistered()
    {
        // An actor whose publicKey.id names a key that is NOT in the store (it was retired) must be
        // skipped (not registered) — otherwise the actor would resolve to a key it can't sign with.
        var actorIriString = $"https://{Host}/ap/v1/u/orphan";
        var retiredKeyIri = new Iri($"{actorIriString}#key-2");
        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = "orphan",
            Name = ["orphan"],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(new
        {
            id = retiredKeyIri.Value,
            owner = actorIriString,
            publicKeyPem = "unused",
        });
        await _persistence.Actors.PutActorAsync(actor);
        // Note: the retired key is intentionally NOT put in the store.

        var provider = new InMemoryKeyProvider(_persistence.Keys);
        var registered = await KeyProviderRehydration.RehydrateFromActorsAsync(provider, _persistence.Actors, _persistence.Keys);

        Assert.Equal(0, registered);
        Assert.False(provider.TryGetIdentity(new Iri(actorIriString), out _));
    }

    [Fact]
    public async Task Rehydrate_EmptyStore_RegistersNothing()
    {
        var provider = new InMemoryKeyProvider(_persistence.Keys);
        var registered = await KeyProviderRehydration.RehydrateFromActorsAsync(provider, _persistence.Actors, _persistence.Keys);
        Assert.Equal(0, registered);
    }
}
