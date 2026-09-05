using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.13 integration test (F-07 moderation — <see cref="Block"/>): the actor's
/// <c>blocks</c> collection is served over the wire (an <c>OrderedCollection</c> of blocked-actor
/// links, advertised on the actor document), and an inbound <c>Block</c> (signed and delivered to the
/// target's inbox) is interpreted — the block edge is recorded so the blocked actor appears in the
/// blocker's <c>blocks</c> collection. The unit-level handler semantics (the local-blocked recording,
/// the both-remote no-op, the guards) are covered in <c>BlockActivityHandlerTests</c>.
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts two local actors — <c>bob</c> (the blocker) and
/// <c>carol</c> (the blocked, local). Bob blocks carol: a <see cref="Block"/> (actor = bob, object =
/// carol) delivered to carol's inbox (on the same instance), signed as bob. The instance validates the
/// signature (bob's key is local; the inbound key resolver fetches bob's actor document through the
/// in-process TestServer) and records the edge in its moderation store (bob → carol). Bob's own
/// <c>/blocks</c> collection is a public read endpoint (no signature needed).
/// </remarks>
[Collection("BlocksCollection")]
public sealed class BlocksCollectionIntegrationTests : IAsyncLifetime
{
    internal const string BHost = "b.domain.local";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    private readonly BlocksCollectionSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;
    private KeyPair _bobKey;

    public BlocksCollectionIntegrationTests(BlocksCollectionSharedHost fixture)
    {
        _fixture = fixture;
        _persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _bobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _carolActorIri = new Iri($"https://{BHost}/ap/v1/u/{Carol}");
        _bobKey = null!;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_persistence);
        var keyId = new Iri($"{_bobActorIri.Value}#key-1");
        _persistence.Keys.TryGetKey(keyId, out var key);
        _bobKey = (KeyPair)key!;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores bob + carol using <see cref="TestSeeder.SeedPersonWithExistingKey"/> — the actors are
    /// re-seeded (their store is cleared by <c>Reset()</c>) but the keys are <em>not</em> regenerated
    /// (the key store is preserved across resets), so the self-fetcher's copy of bob's key and the key
    /// the test signs with stay the same instance.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        TestSeeder.SeedPersonWithExistingKey(persistence, BHost, Bob, new Iri($"https://{BHost}/ap/v1/u/{Bob}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(persistence, BHost, Carol, new Iri($"https://{BHost}/ap/v1/u/{Carol}#key-1"));
    }

    // --- The actor document advertises the blocks collection --------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesBlocksCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The actor document advertises the blocks collection link (via the ActivityStreams extension
        // data, since the library's Person has no typed `blocks` property).
        Assert.Equal(
            $"https://{BHost}/ap/v1/u/{Bob}/blocks",
            doc.RootElement.GetProperty(IrisDocumentExtensions.DefaultNamespaceIri + CollectionExtensionNames.Blocks)
                .GetString());
    }

    // --- The blocks collection is an empty OrderedCollection before any block ----------

    [Fact]
    public async Task BlocksCollection_Empty_IsOrderedCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/blocks");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/blocks", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An inbound Block signed by the local blocker is recorded ----------------------

    [Fact]
    public async Task InboundBlock_SignedByLocalBlocker_RecordsBlockEdge()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        var statusCode = await client.BlockAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(202, statusCode.StatusCode);

        // The instance validated the signature (bob's key is local) and recorded the block edge: carol
        // is in bob's blocks (the forward edge), and bob knows he blocked carol.
        Assert.True(await _persistence.Moderation.IsBlockedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(_carolActorIri, await _persistence.Moderation.GetBlocksAsync(_bobActorIri));
    }

    // --- The blocker's own /blocks collection serves the recorded edge -----------------

    [Fact]
    public async Task InboundBlock_AppearsInBlockersBlocksCollection()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        await client.BlockAsync(_bobActorIri, _carolActorIri);

        // Bob's /blocks collection (a public read endpoint) serves the recorded edge (as a link to
        // carol).
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/blocks");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(_carolActorIri.Value, items[0]);
    }

    // --- A second Block appends to the blocks collection --------------------------------

    [Fact]
    public async Task InboundBlock_TwoBlocks_BothInBlocksCollection()
    {
        // A second local actor (dave) is blocked too, so the blocks collection carries two entries.
        var dave = TestSeeder.SeedPersonWithKey(_persistence, BHost, "dave");

        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        var status1 = await client.BlockAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(202, status1.StatusCode);
        var status2 = await client.BlockAsync(_bobActorIri, dave.ActorIri);
        Assert.Equal(202, status2.StatusCode);

        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/blocks");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToHashSet();
        Assert.Equal(2, items.Count);
        Assert.Contains(_carolActorIri.Value, items);
        Assert.Contains(dave.ActorIri.Value, items);
    }

    // --- The client can read back the blocks collection ---------------------------------

    [Fact]
    public async Task Client_GetBlocksAsync_ReadsBlocksCollection()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        await client.BlockAsync(_bobActorIri, _carolActorIri);

        // The client reads bob's blocks collection (via the real collection endpoint) and sees the
        // blocked actor's IRI (a plain link item deserializes to a Link with its Href).
        var blocked = new List<Iri>();
        await foreach (var item in client.GetBlocksAsync(_bobActorIri))
        {
            if (item is Link { Href: { } href })
            {
                blocked.Add(new Iri(href.ToString()));
            }
        }

        Assert.Single(blocked);
        Assert.Equal(_carolActorIri.Value, blocked[0].Value);
    }

    // --- F-07 (apply the block edge): a blocked follow is excluded from the feed -------

    [Fact]
    public async Task InboundBlock_BlocksFollow_IsExcludedFromFeed()
    {
        // bob follows carol and carol has a post in her outbox (so bob's feed would include it).
        await _persistence.Follows.RecordFollowAsync(_bobActorIri, _carolActorIri);
        const string noteIri = "https://b.domain.local/ap/v1/u/carol/notes/1";
        await _persistence.Activities.AddToOutboxAsync(_carolActorIri, new Create
        {
            Id = "https://b.domain.local/ap/v1/u/carol/creates/1",
            Actor = [new Link { Href = new Uri(_carolActorIri.Value) }],
            Object = [new Note { Id = noteIri, Content = ["carol post"] }],
        });

        // Sanity: before the block, carol's post IS in bob's followed feed.
        var before = await FeedNoteIrisAsync(_bobActorIri);
        Assert.Contains(noteIri, before);

        // bob blocks carol: the Block (actor = bob, object = carol) is delivered to carol's inbox.
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        await client.BlockAsync(_bobActorIri, _carolActorIri);

        // The block edge is applied: carol's content is now excluded from bob's followed feed.
        var after = await FeedNoteIrisAsync(_bobActorIri);
        Assert.DoesNotContain(noteIri, after);
    }

    // --- F-07 (un-block): an Undo of a Block removes the edge (and re-includes the feed) --

    [Fact]
    public async Task Unblock_AfterBlock_RemovesEdgeAndReIncludesFeed()
    {
        // bob follows carol and carol has a post in her outbox (so bob's feed includes it).
        await _persistence.Follows.RecordFollowAsync(_bobActorIri, _carolActorIri);
        const string noteIri = "https://b.domain.local/ap/v1/u/carol/notes/1";
        await _persistence.Activities.AddToOutboxAsync(_carolActorIri, new Create
        {
            Id = "https://b.domain.local/ap/v1/u/carol/creates/1",
            Actor = [new Link { Href = new Uri(_carolActorIri.Value) }],
            Object = [new Note { Id = noteIri, Content = ["carol post"] }],
        });

        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());

        // bob blocks carol (202), the edge is recorded, and carol's post drops out of bob's feed.
        // Decision 055: the server mints the Block's id, returned in DeliveryResult.MintedId.
        var blockResult = await client.BlockAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(202, blockResult.StatusCode);
        Assert.True(blockResult.MintedId != null, "the server should have minted the Block's id.");
        Assert.True(await _persistence.Moderation.IsBlockedAsync(_bobActorIri, _carolActorIri));
        Assert.DoesNotContain(noteIri, await FeedNoteIrisAsync(_bobActorIri));

        // bob un-blocks carol: the Undo of the Block (actor = bob, object = the original Block's
        // LEARNED id) is published to bob's outbox; the instance removes the recorded edge.
        var unblockResult = await client.UnblockAsync(_bobActorIri, new Iri(blockResult.MintedId!));
        Assert.Equal(202, unblockResult.StatusCode);
        Assert.False(await _persistence.Moderation.IsBlockedAsync(_bobActorIri, _carolActorIri));
        Assert.Empty(await _persistence.Moderation.GetBlocksAsync(_bobActorIri));

        // The edge is gone, so carol's post reappears in bob's followed feed.
        Assert.Contains(noteIri, await FeedNoteIrisAsync(_bobActorIri));
    }

    /// <summary>
    /// Reads bob's followed feed over the wire and returns the IRIs of the content objects (the
    /// <c>Note</c>s) it contains. The feed's items are the followed actors' <c>Create</c>s (objects);
    /// each item's embedded note IRI is read from its <c>object</c> (a one-or-many array of one, or a
    /// bare object).
    /// </summary>
    private async Task<IReadOnlyList<string>> FeedNoteIrisAsync(Iri actorIri)
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/feed");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("object", out var obj))
            .Select(e => e.GetProperty("object"))
            .Select(o => o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().First().GetProperty("id").GetString()!
                : o.GetProperty("id").GetString()!)
            .ToList();
    }

    // --- Helpers ----------------------------------------------------------------------

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
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

    internal static IActorDocumentFetcher BuildSelfFetcher(KeyPair authorKey, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>
/// Shared-host fixture for <see cref="BlocksCollectionIntegrationTests"/> (single instance,
/// b.domain.local, Handle=bob, carol as extra local actor). Seeds bob + carol with keys ONCE (the key
/// store is preserved across per-method resets), so the self-fetcher's copy of bob's key stays valid.
/// The test class resets + re-seeds the actors (without regenerating keys) before each method.
/// </summary>
public sealed class BlocksCollectionSharedHost : SharedHostFixture
{
    public BlocksCollectionSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        // Initial seed generates + stores the keys (the key store is preserved across resets).
        var bob = TestSeeder.SeedPersonWithKey(persistence, BlocksCollectionIntegrationTests.BHost, BlocksCollectionIntegrationTests.Bob);
        var carol = TestSeeder.SeedPersonWithKey(persistence, BlocksCollectionIntegrationTests.BHost, BlocksCollectionIntegrationTests.Carol);

        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = BlocksCollectionIntegrationTests.BHost,
            Handle = BlocksCollectionIntegrationTests.Bob,
            Persistence = persistence,
            ExtraLocalActors = [carol.ActorIri],
            Fetcher = BlocksCollectionIntegrationTests.BuildSelfFetcher(bob.Key, bob.ActorIri, () => serverRef().CreateHandler()),
        };
    }
}

/// <summary>
/// xunit collection definition for the blocks-collection shared-host fixture.
/// </summary>
[CollectionDefinition("BlocksCollection")]
public sealed class BlocksCollectionCollection : ICollectionFixture<BlocksCollectionSharedHost>
{
}
