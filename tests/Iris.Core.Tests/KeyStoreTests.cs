using Iris.Core;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryKeyStore"/> (implements <see cref="IKeyStore"/>).
/// </summary>
public class KeyStoreTests
{
    private static readonly Iri KeyIdA = new("https://a.domain.local/u/alice#main-key");
    private static readonly Iri KeyIdB = new("https://b.domain.local/u/bob#main-key");

    [Fact]
    public void PutThenTryGet_ReturnsSameKey()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);

        Assert.True(store.TryGetKey(KeyIdA, out var found));
        Assert.Same(key, found);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        using var store = new InMemoryKeyStore();

        Assert.False(store.TryGetKey(KeyIdA, out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Put_SameKey_ReplacesAndDisposesOld()
    {
        using var store = new InMemoryKeyStore();
        using var first = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(first);

        using var second = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(second);

        Assert.True(store.TryGetKey(KeyIdA, out var found));
        Assert.Same(second, found);
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrueAndEvicts()
    {
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);

        Assert.True(store.RemoveKey(KeyIdA));
        Assert.False(store.TryGetKey(KeyIdA, out _));
        Assert.False(store.RemoveKey(KeyIdA)); // second removal is a no-op
    }

    [Fact]
    public void KeysAreAddressableByDistinctIris()
    {
        using var store = new InMemoryKeyStore();
        using var keyA = KeyPairGenerator.GenerateRsa(KeyIdA);
        using var keyB = KeyPairGenerator.GenerateEcP256(KeyIdB);
        store.PutKey(keyA);
        store.PutKey(keyB);

        Assert.True(store.TryGetKey(KeyIdA, out var a));
        Assert.True(store.TryGetKey(KeyIdB, out var b));
        Assert.Same(keyA, a);
        Assert.Same(keyB, b);
    }

    [Fact]
    public void Put_NullKey_Throws()
    {
        using var store = new InMemoryKeyStore();

        Assert.Throws<ArgumentNullException>(() => store.PutKey(null!));
    }
}
