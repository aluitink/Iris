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
/// Phase 12 Slice 12.16 integration test (F-07 moderation — <see cref="Flag"/>): the actor's
/// <c>flags</c> collection is served over the wire (an <c>OrderedCollection</c> of flagged-actor links,
/// advertised on the actor document), and an inbound <see cref="Flag"/> (signed and delivered to the
/// target's inbox) is interpreted — the flag edge is recorded so the flagged actor appears in the
/// flagger's <c>flags</c> collection, and an <see cref="Undo"/> of the <see cref="Flag"/> (an un-flag)
/// removes it. The unit-level handler semantics (the local-flagged recording, the both-remote no-op, the
/// guards) are covered in <c>FlagActivityHandlerTests</c>.
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts two local actors — <c>bob</c> (the flagger) and
/// <c>carol</c> (the flagged, local). Bob flags carol: a <see cref="Flag"/> (actor = bob, object =
/// carol) delivered to carol's inbox (on the same instance), signed as bob. The instance validates the
/// signature (bob's key is local; the inbound key resolver fetches bob's actor document through the
/// in-process TestServer) and records the edge in its moderation store (bob → carol). Bob's own
/// <c>/flags</c> collection is a public read endpoint (no signature needed). Unlike a
/// <see cref="Block"/> a <see cref="Flag"/> is a report: it does not sever the relationship, so there is
/// no feed/delivery application — only the edge and the <c>flags</c> collection.
/// </remarks>
[Collection("FlagsCollection")]
public sealed class FlagsCollectionIntegrationTests : IAsyncLifetime
{
    internal const string BHost = "b.domain.local";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    private readonly FlagsCollectionSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly Iri _carolActorIri;
    private KeyPair _bobKey;

    public FlagsCollectionIntegrationTests(FlagsCollectionSharedHost fixture)
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

    // --- The actor document advertises the flags collection --------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesFlagsCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The actor document advertises the flags collection link (via the ActivityStreams extension
        // data, since the library's Person has no typed `flags` property).
        Assert.Equal(
            $"https://{BHost}/ap/v1/u/{Bob}/flags",
            doc.RootElement.GetProperty(IrisDocumentExtensions.DefaultNamespaceIri + CollectionExtensionNames.Flags)
                .GetString());
    }

    // --- The flags collection is an empty OrderedCollection before any flag ----------

    [Fact]
    public async Task FlagsCollection_Empty_IsOrderedCollection()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/flags");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"https://{BHost}/ap/v1/u/{Bob}/flags", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An inbound Flag signed by the local flagger is recorded ----------------------

    [Fact]
    public async Task InboundFlag_SignedByLocalFlagger_RecordsFlagEdge()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        var statusCode = await client.FlagAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(202, statusCode.StatusCode);

        // The instance validated the signature (bob's key is local) and recorded the flag edge: carol
        // is in bob's flags (the forward edge), and bob knows he flagged carol.
        Assert.True(await _persistence.Moderation.HasFlaggedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(_carolActorIri, await _persistence.Moderation.GetFlagsAsync(_bobActorIri));
    }

    // --- The flagger's own /flags collection serves the recorded edge -----------------

    [Fact]
    public async Task InboundFlag_AppearsInFlaggersFlagsCollection()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        await client.FlagAsync(_bobActorIri, _carolActorIri);

        // Bob's /flags collection (a public read endpoint) serves the recorded edge (as a link to
        // carol).
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/u/{Bob}/flags");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(_carolActorIri.Value, items[0]);
    }

    // --- The client can read back the flags collection ---------------------------------

    [Fact]
    public async Task Client_GetFlagsAsync_ReadsFlagsCollection()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        await client.FlagAsync(_bobActorIri, _carolActorIri);

        // The client reads bob's flags collection (via the real collection endpoint) and sees the
        // flagged actor's IRI (a plain link item deserializes to a Link with its Href).
        var flagged = new List<Iri>();
        await foreach (var item in client.GetFlagsAsync(_bobActorIri))
        {
            if (item is Link { Href: { } href })
            {
                flagged.Add(new Iri(href.ToString()));
            }
        }

        Assert.Single(flagged);
        Assert.Equal(_carolActorIri.Value, flagged[0].Value);
    }

    // --- F-07 (un-flag): an Undo of a Flag removes the edge ---------------------------

    [Fact]
    public async Task Unflag_AfterFlag_RemovesEdge()
    {
        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());

        // bob flags carol (202), the edge is recorded, and carol appears in bob's flags.
        // Decision 055: the server mints the Flag's id, returned in DeliveryResult.MintedId.
        var flagResult = await client.FlagAsync(_bobActorIri, _carolActorIri);
        Assert.Equal(202, flagResult.StatusCode);
        Assert.True(flagResult.MintedId != null, "the server should have minted the Flag's id.");
        Assert.True(await _persistence.Moderation.HasFlaggedAsync(_bobActorIri, _carolActorIri));
        Assert.Contains(_carolActorIri, await _persistence.Moderation.GetFlagsAsync(_bobActorIri));

        // bob un-flags carol: the Undo of the Flag (actor = bob, object = the original Flag's LEARNED
        // id) is published to bob's outbox; the instance removes the recorded edge.
        var unflagResult = await client.UnflagAsync(_bobActorIri, new Iri(flagResult.MintedId!));
        Assert.Equal(202, unflagResult.StatusCode);
        Assert.False(await _persistence.Moderation.HasFlaggedAsync(_bobActorIri, _carolActorIri));
        Assert.Empty(await _persistence.Moderation.GetFlagsAsync(_bobActorIri));
    }

    // --- A Flag does not sever the relationship (unlike a Block) ----------------------

    [Fact]
    public async Task InboundFlag_DoesNotExcludeFromFeed()
    {
        // bob follows carol and carol has a post in her outbox (so bob's feed includes it). A Flag of
        // carol is a report — it must NOT sever the relationship or exclude carol's content from the
        // feed (that is the Block's job).
        await _persistence.Follows.RecordFollowAsync(_bobActorIri, _carolActorIri);
        const string noteIri = "https://b.domain.local/ap/v1/u/carol/notes/1";
        await _persistence.Activities.AddToOutboxAsync(_carolActorIri, new Create
        {
            Id = "https://b.domain.local/ap/v1/u/carol/creates/1",
            Actor = [new Link { Href = new Uri(_carolActorIri.Value) }],
            Object = [new Note { Id = noteIri, Content = ["carol post"] }],
        });

        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _fixture.Server.CreateHandler());
        Assert.Equal(202, (await client.FlagAsync(_bobActorIri, _carolActorIri)).StatusCode);

        // The flag edge is recorded, but carol's content is still in bob's followed feed (a flag does
        // not block).
        Assert.True(await _persistence.Moderation.HasFlaggedAsync(_bobActorIri, _carolActorIri));
        var feed = await FeedNoteIrisAsync(_bobActorIri);
        Assert.Contains(noteIri, feed);
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
/// Shared-host fixture for <see cref="FlagsCollectionIntegrationTests"/> (single instance,
/// b.domain.local, Handle=bob, carol as extra local actor). Seeds bob + carol with keys ONCE (the key
/// store is preserved across per-method resets), so the self-fetcher's copy of bob's key stays valid.
/// The test class resets + re-seeds the actors (without regenerating keys) before each method.
/// </summary>
public sealed class FlagsCollectionSharedHost : SharedHostFixture
{
    public FlagsCollectionSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(persistence, FlagsCollectionIntegrationTests.BHost, FlagsCollectionIntegrationTests.Bob);
        var carol = TestSeeder.SeedPersonWithKey(persistence, FlagsCollectionIntegrationTests.BHost, FlagsCollectionIntegrationTests.Carol);

        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = FlagsCollectionIntegrationTests.BHost,
            Handle = FlagsCollectionIntegrationTests.Bob,
            Persistence = persistence,
            ExtraLocalActors = [carol.ActorIri],
            Fetcher = FlagsCollectionIntegrationTests.BuildSelfFetcher(bob.Key, bob.ActorIri, () => serverRef().CreateHandler()),
        };
    }
}

/// <summary>
/// xunit collection definition for the flags-collection shared-host fixture.
/// </summary>
[CollectionDefinition("FlagsCollection")]
public sealed class FlagsCollectionCollection : ICollectionFixture<FlagsCollectionSharedHost>
{
}
