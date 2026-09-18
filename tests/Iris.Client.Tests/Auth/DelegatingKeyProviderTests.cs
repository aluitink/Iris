using Iris.Core.Identity;

namespace Iris.Client.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="Iris.Client.Auth.DelegatingKeyProvider"/> (Phase 82.3 — key-management
/// hardening). These lock the resolution ORDER (the in-memory primary is consulted first; the durable
/// store is only a fallback by the well-known <c>{actor}#key-1</c> convention) and the NO-KEY FAILURE
/// MODE (when neither source resolves the actor, the provider returns <c>false</c> + a null identity —
/// it never throws and never returns a null identity; the <c>KeyNotFoundException</c> is raised later,
/// by the <c>SigningHandler</c>, so a keyless actor is never signed with a null/wrong key). Before
/// 82.3 this provider had no tests at all: the fallback path and its failure mode were unverified.
/// </summary>
public sealed class DelegatingKeyProviderTests
{
    private static readonly Iri Actor = new("https://instance.example/ap/v1/u/alice");
    private static readonly Iri ActorKey = new("https://instance.example/ap/v1/u/alice#key-1");

    private static (InMemoryKeyStore Store, InMemoryKeyProvider Primary, ISigningKey Key) Build()
    {
        var store = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(ActorKey);
        store.PutKey(key);
        var primary = new InMemoryKeyProvider(store);
        return (store, primary, key);
    }

    [Fact]
    public void PrimaryHit_ReturnsPrimaryIdentity_DoesNotFallThroughToStore()
    {
        // The actor is registered with the primary (in-memory) provider. Resolution must return the
        // primary's identity WITHOUT consulting the durable store (the store is only a fallback).
        var (store, primary, _) = Build();
        primary.RegisterKey(Actor, ActorKey);
        var provider = new DelegatingKeyProvider(primary, store);

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.NotNull(identity);
        Assert.Equal(Actor, identity!.ActorId);
        Assert.Equal(ActorKey, identity.KeyId);
    }

    [Fact]
    public void PrimaryMiss_StoreHasKeyByConvention_ReturnsDurableIdentity()
    {
        // The actor is NOT registered with the primary, but its key lives in the durable store under
        // the well-known {actor}#key-1 IRI. The fallback must resolve it and return an identity whose
        // keyId is exactly that convention IRI (this is what makes a locally-provisioned actor
        // signable without an explicit RegisterKey call / restart — the gap the provider exists for).
        var (store, primary, _) = Build();
        // primary has no registration for Actor.
        var provider = new DelegatingKeyProvider(primary, store);

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.NotNull(identity);
        Assert.Equal(Actor, identity!.ActorId);
        Assert.Equal($"{Actor}#key-1", identity.KeyId.Value);
    }

    [Fact]
    public void PrimaryMiss_StoreMiss_ReturnsFalseAndNullIdentity_NeverThrows()
    {
        // NEITHER source can resolve the actor. The provider must return false + a null identity — it
        // must NOT throw and must NOT return a null identity wrapped in a true. The throw happens
        // later, in the SigningHandler (KeyNotFoundException), so a keyless actor is never signed with
        // a null/wrong key.
        var store = new InMemoryKeyStore(); // empty — no key for Actor
        var primary = new InMemoryKeyProvider(store); // no registration
        var provider = new DelegatingKeyProvider(primary, store);

        var result = provider.TryGetIdentity(Actor, out var identity);

        Assert.False(result);
        Assert.Null(identity);
    }

    [Fact]
    public void PrimaryMiss_StoreHasWrongKeyIri_ReturnsFalseAndNullIdentity()
    {
        // The store holds a key, but under a DIFFERENT IRI (not {actor}#key-1). The fallback must not
        // match it — only the convention IRI is consulted, so this actor is keyless from the provider's
        // point of view.
        var store = new InMemoryKeyStore();
        store.PutKey(KeyPairGenerator.GenerateRsa(new Iri("https://instance.example/ap/v1/u/alice#other-key")));
        var primary = new InMemoryKeyProvider(store);
        var provider = new DelegatingKeyProvider(primary, store);

        Assert.False(provider.TryGetIdentity(Actor, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void CustomKeyFragment_IsHonored_InFallbackLookup()
    {
        // A non-default fragment (e.g. a rotated key under {actor}#key-2) must be used in the fallback
        // lookup. This is what would let a future multi-key/rotation setup resolve the right key.
        var store = new InMemoryKeyStore();
        var key2 = KeyPairGenerator.GenerateRsa(new Iri($"{Actor}#key-2"));
        store.PutKey(key2);
        var primary = new InMemoryKeyProvider(store);
        var provider = new DelegatingKeyProvider(primary, store, keyFragment: "key-2");

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.NotNull(identity);
        Assert.Equal($"{Actor}#key-2", identity!.KeyId.Value);
    }

    [Fact]
    public void RegisterKey_DelegatesToPrimary()
    {
        // RegisterKey must delegate to the primary provider (the in-memory map) — the durable store is
        // write-through and not touched by the provider's registration.
        var (store, primary, _) = Build();
        var provider = new DelegatingKeyProvider(primary, store);

        provider.RegisterKey(Actor, ActorKey);

        Assert.True(primary.TryGetIdentity(Actor, out _));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var store = new InMemoryKeyStore();
        var primary = new InMemoryKeyProvider(store);
        Assert.Throws<ArgumentNullException>(() => new DelegatingKeyProvider(null!, store));
        Assert.Throws<ArgumentNullException>(() => new DelegatingKeyProvider(primary, null!));
    }
}
