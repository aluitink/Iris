using System.Net;
using System.Net.Http.Headers;
using Iris.Core;

namespace Iris.Client.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="Iris.Client.Auth.InMemoryKeyProvider"/> (implements <see cref="IKeyProvider"/>).
/// </summary>
public class InMemoryKeyProviderTests
{
    private static readonly Iri ActorA = new("https://a.domain.local/u/alice");
    private static readonly Iri KeyIdA = new("https://a.domain.local/u/alice#main-key");

    [Fact]
    public void TryGetIdentity_Registered_ReturnsIdentity()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);

        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(ActorA, KeyIdA);

        Assert.True(provider.TryGetIdentity(ActorA, out var identity));
        Assert.NotNull(identity);
        Assert.Equal(ActorA, identity!.ActorId);
        Assert.Equal(KeyIdA, identity.KeyId);
    }

    [Fact]
    public void TryGetIdentity_UnregisteredActor_ReturnsFalse()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);

        var provider = new InMemoryKeyProvider(store);
        // Key is in the store but not registered for this actor.
        Assert.False(provider.TryGetIdentity(ActorA, out _));
    }

    [Fact]
    public void TryGetIdentity_UnknownKey_ReturnsFalse()
    {
        using var store = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(store);
        // Registered, but the key Iri is not in the store.
        provider.RegisterKey(ActorA, new("https://a.domain.local/u/alice#missing"));
        Assert.False(provider.TryGetIdentity(ActorA, out _));
    }
}
