using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;

namespace Iris.Client.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="DelegatingKeyProvider"/> (slice 33.3): a local actor whose key is in the
/// durable key store is resolvable even when the in-process key provider has no registration for it.
/// </summary>
public sealed class DelegatingKeyProviderTests
{
    private static readonly Iri Actor = new("https://iris.luit.ink/ap/v1/u/andrew");
    private static readonly Iri KeyId = new($"{Actor}#key-1");

    [Fact]
    public void TryGetIdentity_RegisteredWithPrimary_ReturnsFromPrimary()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var primary = new InMemoryKeyProvider(store);
        primary.RegisterKey(Actor, KeyId);
        var provider = new DelegatingKeyProvider(primary, store);

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.Equal(Actor, identity!.ActorId);
        Assert.Equal(KeyId, identity.KeyId);
    }

    [Fact]
    public void TryGetIdentity_NotInPrimary_ButKeyInStore_FallsBackToStore()
    {
        // The 33.3 scenario: the key is in the (durable) store under {actor}#key-1, but the in-process
        // provider was never told about the actor (a different provisioning path wrote the key). The
        // delegating provider resolves it from the store so the actor is signable without a restart.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var primary = new InMemoryKeyProvider(store); // no RegisterKey for Actor
        var provider = new DelegatingKeyProvider(primary, store);

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.Equal(Actor, identity!.ActorId);
        Assert.Equal(KeyId, identity.KeyId);
    }

    [Fact]
    public void TryGetIdentity_NotInPrimary_AndNoKeyInStore_ReturnsFalse()
    {
        using var store = new InMemoryKeyStore();
        var primary = new InMemoryKeyProvider(store);
        var provider = new DelegatingKeyProvider(primary, store);

        // Neither registered nor a key in the store: genuinely unresolvable.
        Assert.False(provider.TryGetIdentity(Actor, out _));
    }

    [Fact]
    public void TryGetIdentity_DifferentFragment_ResolvesThatFragment()
    {
        // A non-default key fragment is honored (the convention is {actor}#{fragment}).
        using var store = new InMemoryKeyStore();
        var altKeyId = new Iri($"{Actor}#alt");
        using var key = KeyPairGenerator.GenerateRsa(altKeyId);
        store.PutKey(key);

        var primary = new InMemoryKeyProvider(store);
        var provider = new DelegatingKeyProvider(primary, store, keyFragment: "alt");

        Assert.True(provider.TryGetIdentity(Actor, out var identity));
        Assert.Equal(altKeyId, identity!.KeyId);
    }

    [Fact]
    public void RegisterKey_DelegatesToPrimary()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var primary = new InMemoryKeyProvider(store);
        var provider = new DelegatingKeyProvider(primary, store);

        // A registration through the delegating provider is visible via the primary (fast path).
        provider.RegisterKey(Actor, KeyId);
        Assert.True(primary.TryGetIdentity(Actor, out _));
    }
}
