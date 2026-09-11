using Iris.Core;

namespace Iris.Core.Tests.Identity;

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

    // --- Fragment-aware key IRI addressing (Phase 82.3) -------------------------
    //
    // A key IRI's fragment is semantically significant ({actor}#key-1 vs {actor}#key-2 name
    // DIFFERENT keys), so the store must address by the full fragment-aware Value. The default
    // Iri equality is fragment-blind (System.Uri semantics) and would conflate #key-1 with #key-2
    // and with the bare {actor} — a silent key-management corruption (a key stored under one
    // fragment "found" for a different one). InMemoryKeyStore now uses IriEqualityComparer to keep
    // them distinct (matching the durable EfKeyStore, which compares by Iri.Value).

    [Fact]
    public void DistinctFragments_AreDistinctEntries_NotConflated()
    {
        // Two keys under the SAME actor but DIFFERENT fragments must be stored and retrieved as
        // distinct entries. With fragment-blind equality, PutKey(#key-2) would overwrite #key-1
        // (same dictionary slot) and TryGetKey(#key-1) would return the #key-2 key.
        var actorBase = "https://a.domain.local/u/alice";
        var key1Iri = new Iri($"{actorBase}#key-1");
        var key2Iri = new Iri($"{actorBase}#key-2");
        using var store = new InMemoryKeyStore();
        using var key1 = KeyPairGenerator.GenerateRsa(key1Iri);
        using var key2 = KeyPairGenerator.GenerateRsa(key2Iri);
        store.PutKey(key1);
        store.PutKey(key2);

        // Both keys are present and distinct (no overwrite).
        Assert.True(store.TryGetKey(key1Iri, out var retrieved1));
        Assert.True(store.TryGetKey(key2Iri, out var retrieved2));
        Assert.Same(key1, retrieved1);
        Assert.Same(key2, retrieved2);
    }

    [Fact]
    public void KeyFragment_DoesNotMatchBareActorIri()
    {
        // A key stored under {actor}#key-1 must NOT be found by the bare {actor} IRI (no fragment).
        // With fragment-blind equality these are the "same" key and the bare-actor lookup would
        // return the #key-1 key.
        var actorIri = new Iri("https://a.domain.local/u/alice");
        var key1Iri = new Iri($"{actorIri}#key-1");
        using var store = new InMemoryKeyStore();
        using var key1 = KeyPairGenerator.GenerateRsa(key1Iri);
        store.PutKey(key1);

        // The keyed lookup succeeds...
        Assert.True(store.TryGetKey(key1Iri, out _));
        // ...but the bare-actor lookup (no fragment) does NOT.
        Assert.False(store.TryGetKey(actorIri, out _));
    }

    [Fact]
    public void RemoveKey_ByOneFragment_LeavesOtherFragment()
    {
        // Removing {actor}#key-1 must leave {actor}#key-2 intact.
        var actorBase = "https://a.domain.local/u/alice";
        var key1Iri = new Iri($"{actorBase}#key-1");
        var key2Iri = new Iri($"{actorBase}#key-2");
        using var store = new InMemoryKeyStore();
        using var key1 = KeyPairGenerator.GenerateRsa(key1Iri);
        using var key2 = KeyPairGenerator.GenerateRsa(key2Iri);
        store.PutKey(key1);
        store.PutKey(key2);

        Assert.True(store.RemoveKey(key1Iri));

        Assert.False(store.TryGetKey(key1Iri, out _));
        Assert.True(store.TryGetKey(key2Iri, out var remaining));
        Assert.Same(key2, remaining);
    }
}
