using Iris.Client;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using KeyPair = Iris.Core.Identity.KeyPair;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.5.1 end-to-end test: the client's one-call community-creation operation —
/// <see cref="IActivityPubClient.CreateCommunityAsync"/> — is expressible as an ActivityStream
/// <c>Create</c> of a <c>Group</c> (authored by a person to their own outbox, the AP-native
/// outbox-publish pattern), and the server materializes the community (stores it in the community
/// store with a minted signing key) so the new community's document endpoint, <c>members</c>,
/// <c>feed</c>, and collections resolve. This completes the 19.6.1 invariant that every management
/// operation is a one-call client method (the create-community write path previously deferred as a
/// chicken-and-egg: a community can't publish to its own not-yet-existent outbox, so the creator's
/// outbox carries the <c>Create</c> instead).
/// </summary>
/// <remarks>
/// A person authors a <c>Create</c> whose embedded object is a <c>Group</c> with the IRI
/// <c>{base}/ap/v1/c/{name}</c> (the community's IRI on the creator's instance) and publishes it to
/// their own outbox. The server's outbox-publish handler, on seeing a <c>Create</c> whose embedded
/// object is a local <c>Group</c>, stores the community (minting a key on first creation, reusing it
/// on re-creation) and stamps the <c>publicKey</c> extension, so the community's document endpoint,
/// <c>members</c>, <c>feed</c>, and collections resolve.
/// </remarks>
public sealed class CommunityCreationIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";

    private readonly TestServer _server;
    private readonly IActivityPubClient _client;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;

    public CommunityCreationIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();

        // A hosts alice (a local actor who will author the community), seeded WITH a real signing key
        // (stored in the provider's Keys + the publicKey extension), so the server can verify a
        // person-signed Create posted to alice's outbox. The community does not exist yet — it is
        // created by the test via the client's CreateCommunityAsync.
        var seeded = TestSeeder.SeedPersonWithKey(_persistence, AHost, Alice);
        _aliceIri = seeded.ActorIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
            Fetcher = BuildSelfFetcher(_persistence, _aliceIri, () => _server!.CreateHandler()),
        });

        // A signed client signed AS ALICE (a person): CreateCommunityAsync builds a Create of a Group
        // with actor = alice and publishes it to alice's own outbox. The client uses the SAME key the
        // server expects (the seeded key), so the inbound signature verifies.
        _client = BuildClient(_aliceIri, seeded.Key, _server.CreateHandler());
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // --- The client's CreateCommunityAsync materializes the community -------------------------

    [Fact]
    public async Task CreateCommunityAsync_AuthoredByPerson_MaterializesCommunity()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");

        // Preconditions: the community does not exist yet.
        Assert.False(await _persistence.Communities.TryGetCommunityAsync(communityIri, out _));

        var result = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(result.IsSuccess, $"the person-authored Create of a Group must be accepted (got {result.StatusCode})");
        Assert.Equal(202, result.StatusCode);

        // The community is now stored in the community store (19.5.1 materialization).
        Assert.True(
            await _persistence.Communities.TryGetCommunityAsync(communityIri, out var community),
            "the community should be stored after the person-authored CreateCommunityAsync (19.5.1)");

        // The community's document (a Group) carries a publicKey extension (id, owner, publicKeyPem),
        // so its signing key resolves for inbound signature validation.
        Assert.NotNull(community);
        Assert.True(
            community.ExtensionData is { Count: > 0 } && community.ExtensionData.ContainsKey("publicKey"),
            "the created community's Group document must carry a publicKey extension (19.5.1 key minting)");

        // The community has no members yet (it was just created).
        Assert.Empty(await _persistence.Communities.GetMembersAsync(communityIri));
    }

    // --- Re-creating the same community is idempotent (key is reused, not re-minted) ----------

    [Fact]
    public async Task CreateCommunityAsync_Twice_IsIdempotentAndReusesKey()
    {
        var communityIri = new Iri($"https://{AHost}/ap/v1/c/devs");
        var keyId = new Iri($"{communityIri.Value}#key-1");

        var first = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(first.IsSuccess, $"the first CreateCommunityAsync must be accepted (got {first.StatusCode})");

        // Capture the minted public key (a re-creation must reuse it, not re-key the community).
        var firstKey = _persistence.Keys;
        Assert.True(firstKey.TryGetKey(keyId, out var firstMinted),
            "the first creation must mint a key at {community}#key-1");
        Assert.NotNull(firstMinted);
        var firstPem = firstMinted.ExportPublicKeyPem();

        // Re-create the same community (a second person-authored Create with the same name).
        var second = await _client.CreateCommunityAsync(_aliceIri, "devs", "Devs Community");
        Assert.True(second.IsSuccess, $"the second CreateCommunityAsync must be accepted (got {second.StatusCode})");

        // The community still resolves and the key is the SAME (reused, not re-minted).
        Assert.True(await _persistence.Communities.TryGetCommunityAsync(communityIri, out _));
        Assert.True(firstKey.TryGetKey(keyId, out var secondKey),
            "the re-creation must leave the existing key in place");
        Assert.NotNull(secondKey);
        // A re-creation must reuse the existing community key (not re-mint, which would break signatures).
        Assert.True(
            string.Equals(firstPem, secondKey.ExportPublicKeyPem(), StringComparison.Ordinal),
            "a re-creation must reuse the existing community key (not re-mint, which would break signatures)");
    }

    // --- A Create of a non-Group object is unchanged (no community materialization) -----------

    [Fact]
    public async Task PostNoteByPerson_DoesNotMaterializeCommunity()
    {
        // Alice posts a normal Note to her own outbox (the existing Create path). No community is
        // created (the embedded object is a Note, not a Group).
        var result = await _client.PostNoteAsync(_aliceIri, "hello");
        Assert.True(result.IsSuccess, $"the Note Create must be accepted (got {result.StatusCode})");

        // No community was created (the embedded object is a Note, not a Group).
        var wouldBeCommunity = new Iri($"https://{AHost}/ap/v1/c/devs");
        Assert.False(await _persistence.Communities.TryGetCommunityAsync(wouldBeCommunity, out _),
            "a Create of a Note must not materialize a community (19.5.1 is Group-only)");
    }

    /// <summary>
    /// Builds a signed <see cref="IActivityPubClient"/> (signed as <paramref name="actorIri"/>, key
    /// <paramref name="key"/>) whose transport routes to <paramref name="handler"/>.
    /// </summary>
    private static IActivityPubClient BuildClient(Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    /// <summary>
    /// Builds a self-referential <see cref="IActorDocumentFetcher"/>: the instance's fetcher reaches its
    /// OWN actor/community documents (so it can resolve the actor's signing key from its own document
    /// when validating a person-signed Create posted to its own outbox).
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(
        InMemoryPersistenceProvider persistence,
        Iri aliceIri,
        Func<HttpMessageHandler> transportFactory)
    {
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{aliceIri.Value}#key-1"));
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = aliceIri, EnableRetry = false },
            new LazyHandler(transportFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
