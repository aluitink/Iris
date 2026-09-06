using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.7 integration test (F-04 — <see cref="Like"/>): the actor's <c>liked</c> collection
/// is served over the wire (an <c>OrderedCollection</c> of liked-object links, advertised on the actor
/// document), and an inbound <c>Like</c> from a <em>local</em> actor (signed and delivered to the
/// instance) is interpreted — the like edge is recorded so the object appears in the actor's <c>liked</c>
/// collection. The community-recipient path (an inbound <c>Like</c> to a community inbox recorded in each
/// local member's outbox) is covered at the unit level in <c>LikeActivityHandlerTests</c>.
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts the local actor (bob) and the real
/// <c>/liked</c> collection endpoint. An inbound <c>Like</c> is delivered by a client signed as the local
/// actor (bob) — the instance validates the signature (bob's key is local; the inbound key resolver fetches
/// bob's actor document through the in-process TestServer) and stores the activity. The <c>liked</c>
/// collection is a public read endpoint (no signature needed).
/// </remarks>
public sealed class LikedCollectionIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly string _base = $"https://{BHost}";
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;

    public LikedCollectionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);
        _bobKey = bob.Key;
        _bobActorIri = bob.ActorIri;

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            Fetcher = BuildSelfFetcher(_bobKey, () => _server!),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- The actor document advertises the liked collection --------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesLikedCollection()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The actor document advertises the liked collection link (the ActivityStreams `liked` property).
        Assert.Equal($"{_base}/ap/v1/u/{Bob}/liked", doc.RootElement.GetProperty("liked").GetString());
    }

    // --- The liked collection is an empty OrderedCollection before any like ----------

    [Fact]
    public async Task LikedCollection_Empty_IsOrderedCollection()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/liked");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal($"{_base}/ap/v1/u/{Bob}/liked", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An inbound Like signed by the local actor records the like edge --------------

    [Fact]
    public async Task InboundLike_SignedByLocalActor_RecordsInLikedCollection()
    {
        // A local actor's like of a LOCAL object (stored in this instance's object store) records the
        // like edge (31.10: the edge is recorded when the object is local). The object is seeded here so
        // the instance stores it (a local note bob authored).
        var objectIri = new Iri($"{_base}/ap/v1/u/{Bob}/notes/n1");
        SeedLocalNote(objectIri);
        var like = BuildLike(_bobActorIri, objectIri);

        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _server.CreateHandler());
        var statusCode = await client.DeliverAsync(_bobActorIri.InboxOf(), like);
        Assert.Equal(202, statusCode.StatusCode);

        // The instance validated the signature (bob's key is local) and stored the Like.
        Assert.True(
            await _persistence.Activities.TryGetActivityAsync(new Iri(like.Id!), out var stored),
            "The instance should have stored the Like after validating the signature");
        Assert.NotNull(stored);
        Assert.IsType<Like>(stored);

        // The like edge is recorded: the object is in bob's liked collection.
        Assert.True(await _persistence.Likes.HasLikedAsync(_bobActorIri, objectIri));

        // ... and the <c>liked</c> collection endpoint serves it (as a link to the object).
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/liked");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(objectIri.Value, items[0]);
    }

    // --- A second Like appends to the liked collection --------------------------------

    [Fact]
    public async Task InboundLike_TwoLikes_BothInLikedCollection()
    {
        // Both objects are local (stored in this instance's object store), so both like edges are
        // recorded (31.10: the edge is recorded when the object is local).
        var object1 = new Iri($"{_base}/ap/v1/u/{Bob}/notes/n1");
        var object2 = new Iri($"{_base}/ap/v1/u/{Bob}/notes/n2");
        SeedLocalNote(object1);
        SeedLocalNote(object2);

        using var client = BuildDeliveryClient(_bobActorIri, _bobKey, _server.CreateHandler());

        var like1 = BuildLike(_bobActorIri, object1);
        var status1 = await client.DeliverAsync(_bobActorIri.InboxOf(), like1);
        Assert.Equal(202, status1.StatusCode);

        var like2 = BuildLike(_bobActorIri, object2);
        var status2 = await client.DeliverAsync(_bobActorIri.InboxOf(), like2);
        Assert.Equal(202, status2.StatusCode);

        // Both liked objects are in the liked collection (2 items).
        var response = await _http.GetAsync($"{_base}/ap/v1/u/{Bob}/liked");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToHashSet();
        Assert.Equal(2, items.Count);
        Assert.Contains(object1.Value, items);
        Assert.Contains(object2.Value, items);
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

    private static IActorDocumentFetcher BuildSelfFetcher(KeyPair authorKey, Func<TestServer> server)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        keyProvider.RegisterKey(actorIri, authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(server));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static Like BuildLike(Iri likerIri, Iri objectIri) => new()
    {
        Id = $"{likerIri}/activities/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(likerIri.Value) }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    /// <summary>
    /// Seeds a local <see cref="Note"/> into the instance's object store under the given IRI, so the
    /// object is local to this instance (31.10: a like of a local object records the like edge).
    /// </summary>
    private void SeedLocalNote(Iri noteIri)
    {
        _persistence.Objects.PutObjectAsync(new Note
        {
            Id = noteIri.Value,
            Content = ["a local note"],
            AttributedTo = [new Link { Href = new Uri(_bobActorIri.Value) }],
        }).GetAwaiter().GetResult();
    }

    }
