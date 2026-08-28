using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Testing;

/// <summary>
/// Unit tests for the shared test-seeding / JWK / JSON-document helpers hoisted into
/// <c>Iris.Testing</c> (Phase 10, test harness consolidation). These helpers were previously
/// copy-pasted across the <c>Iris.Server.Tests</c> integration suites; this suite pins their behavior
/// so a regression in the shared code is caught at the unit level before it breaks the integration
/// tests that depend on them.
/// </summary>
public sealed class TestHelperTests
{
    // --- TestSeeder -----------------------------------------------------------------

    [Fact]
    public async Task SeedPerson_StoresActor_UnderStandardIri()
    {
        var persistence = new InMemoryPersistenceProvider();
        var actorIri = TestSeeder.SeedPerson(persistence, "a.domain.local", "alice");

        Assert.Equal("https://a.domain.local/ap/v1/u/alice", actorIri.Value);
        var found = await persistence.ActorStore.TryGetActorAsync(actorIri, out var actor, CancellationToken.None);
        Assert.True(found, "the seeded actor should be retrievable");
        Assert.NotNull(actor);
        Assert.Equal("alice", actor!.PreferredUsername);
    }

    [Fact]
    public async Task SeedPersonWithKey_StoresActorAndKey_WithJwkInPublicKeyExtension()
    {
        var persistence = new InMemoryPersistenceProvider();
        var (key, actorIri, keyId) = TestSeeder.SeedPersonWithKey(persistence, "b.domain.local", "bob");

        Assert.Equal("https://b.domain.local/ap/v1/u/bob", actorIri.Value);
        Assert.Equal("https://b.domain.local/ap/v1/u/bob#key-1", keyId.Value);

        // The key is stored in the provider's key store under its IRI.
        Assert.True(persistence.Keys.TryGetKey(keyId, out var stored));
        Assert.Same(key, stored);

        // The actor carries the key's JWK in its publicKey extension, with the real x/y components.
        var found = await persistence.ActorStore.TryGetActorAsync(actorIri, out var actor, CancellationToken.None);
        Assert.True(found);
        using var jwk = JsonDocument.Parse(actor!.ExtensionData!["publicKey"].GetRawText());
        Assert.Equal(keyId.Value, jwk.RootElement.GetProperty("id").GetString());
        Assert.Equal(actorIri.Value, jwk.RootElement.GetProperty("owner").GetString());
        Assert.Equal("EC", jwk.RootElement.GetProperty("kty").GetString());
        Assert.Equal("P-256", jwk.RootElement.GetProperty("crv").GetString());
        Assert.Equal(Jwk.ExtractComponent(key, "x"), jwk.RootElement.GetProperty("x").GetString());
        Assert.Equal(Jwk.ExtractComponent(key, "y"), jwk.RootElement.GetProperty("y").GetString());
    }

    [Fact]
    public async Task SeedCommunity_StoresGroup_UnderStandardIri()
    {
        var persistence = new InMemoryPersistenceProvider();
        var communityIri = TestSeeder.SeedCommunity(persistence, "a.domain.local", "iris");

        Assert.Equal("https://a.domain.local/ap/v1/c/iris", communityIri.Value);
        var found = await persistence.Communities.TryGetCommunityAsync(communityIri, out var community, CancellationToken.None);
        Assert.True(found, "the seeded community should be retrievable");
        Assert.NotNull(community);
        Assert.Equal("iris", community!.PreferredUsername);
    }

    [Fact]
    public async Task AddMember_RecordsMembership()
    {
        var persistence = new InMemoryPersistenceProvider();
        var communityIri = TestSeeder.SeedCommunity(persistence, "a.domain.local", "iris");
        var aliceIri = TestSeeder.SeedPerson(persistence, "a.domain.local", "alice");

        TestSeeder.AddMember(persistence, communityIri, aliceIri);

        var isMember = await persistence.Communities.IsMemberAsync(communityIri, aliceIri, CancellationToken.None);
        Assert.True(isMember);
    }

    [Fact]
    public async Task AddCreateActivity_AppendsToOutbox()
    {
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = TestSeeder.SeedPerson(persistence, "a.domain.local", "alice");
        var activityIri = $"{aliceIri.Value}/activities/create-1";

        TestSeeder.AddCreateActivity(persistence, aliceIri, activityIri, "hello");

        var outbox = await persistence.Activities.GetOutboxAsync(aliceIri, CancellationToken.None);
        Assert.Single(outbox);
    }

    // --- Jwk ------------------------------------------------------------------------

    [Fact]
    public void ExtractComponent_ReturnsTheNamedJwkMember()
    {
        var key = KeyPairGenerator.GenerateEcP256(new Iri("https://a.domain.local/ap/v1/u/alice#key-1"));

        var x = Jwk.ExtractComponent(key, "x");
        var y = Jwk.ExtractComponent(key, "y");

        Assert.False(string.IsNullOrWhiteSpace(x));
        Assert.False(string.IsNullOrWhiteSpace(y));
        Assert.NotEqual(x, y); // the x and y components of a P-256 point are (almost surely) distinct
    }

    // --- JsonDoc --------------------------------------------------------------------

    [Fact]
    public void GetItems_NormalizesArrayAndSingleItem()
    {
        // An array of two items.
        using var array = JsonDocument.Parse("{\"items\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");
        var fromArray = JsonDoc.GetItems(array.RootElement);
        Assert.Equal(2, fromArray.Count);

        // A single item (not an array) — the one-or-many converter's single-element case.
        using var single = JsonDocument.Parse("{\"items\":{\"id\":\"a\"}}");
        var fromSingle = JsonDoc.GetItems(single.RootElement);
        Assert.Single(fromSingle);

        // No items property — an empty list.
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(JsonDoc.GetItems(empty.RootElement));
    }

    [Fact]
    public void ItemId_ReadsBareIriStringAndObjectId()
    {
        using var doc = JsonDocument.Parse("{\"items\":[\"https://x/a\", {\"id\":\"https://x/b\"}]}");
        var items = JsonDoc.GetItems(doc.RootElement);
        Assert.Equal("https://x/a", JsonDoc.ItemId(items[0]));
        Assert.Equal("https://x/b", JsonDoc.ItemId(items[1]));
    }
}
