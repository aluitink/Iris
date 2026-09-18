using System.Net;
using System.Net.Http;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.18 integration test: privacy and visibility policy alignment. Verifies:
/// (1) a public post (as:Public in to) is visible in the public feed, the author's outbox,
///     and global search on the origin instance;
/// (2) a direct/DM post (specific actor in to, no as:Public, no cc) is stored in the author's
///     outbox on the origin instance — pinning the current behavior that the read path does NOT
///     filter by visibility (a known gap: the post is also visible in the public feed and search);
/// (3) a public post federated to a remote instance is visible in the remote instance's object
///     store and resolvable by deep link;
/// (4) a direct/DM post federated to a remote instance is stored on the remote (the current
///     behavior — federation does not suppress non-public content; a known gap).
/// </summary>
[Collection("CrossInstanceVisibility")]
public sealed class CrossInstanceVisibilityIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "vis-a.domain.local";
    internal const string BHost = "vis-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";

    private readonly CrossInstanceVisibilitySharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private HttpClient _aHttp;
    private HttpClient _bHttp;

    public CrossInstanceVisibilityIntegrationTests(CrossInstanceVisibilitySharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceKey = null!;
        _aHttp = null!;
        _bHttp = null!;
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _aPersistence.Keys.TryGetKey(new Iri($"{aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        _aHttp = new HttpClient(_fixture.ServerA.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_fixture.ServerB.CreateHandler(), disposeHandler: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    internal static void SeedForFixture(
        InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
        // alice (A) follows bob (B) so that public posts from alice federate to bob.
        aPersistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
    }

    // --- 1. Public post is visible in public feed, outbox, and search on origin -----------

    [Fact]
    public async Task PublicPost_VisibleInPublicFeed_Outbox_Search_OnOrigin()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-pub-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["publicvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(Iri.Public.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{AHost}/ap/v1/u/{Alice}/inbox"), create, () => _fixture.ServerA);

        // The note is in alice's outbox.
        var outbox = await _aPersistence.Activities.GetOutboxAsync(aliceIri);
        Assert.Contains(outbox, i => i is IObject { Id: var id } && id == noteIri.Value);

        // The note is in the public feed (GET /ap/v1/public/feed).
        var feedResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/public/feed?limit=50");
        feedResp.EnsureSuccessStatusCode();
        var feedDoc = JsonDocument.Parse(await feedResp.Content.ReadAsStringAsync());
        var feedItems = GetItemIds(feedDoc.RootElement);
        Assert.Contains(noteIri.Value, feedItems);

        // The note is in global search (GET /ap/v1/search?q=publicvisibility).
        var searchResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/search?q=publicvisibility");
        searchResp.EnsureSuccessStatusCode();
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchItems = GetItemIds(searchDoc.RootElement);
        Assert.Contains(noteIri.Value, searchItems);
    }

    // --- 2. Direct/DM post: stored in outbox (pin current no-visibility-filter behavior) --

    [Fact]
    public async Task DirectPost_StoredInOutbox_VisibleInPublicFeed_CurrentGap()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-dm-{Guid.NewGuid():N}");

        // A "direct" post: to = [bob] (no as:Public, no cc).
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["directvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{AHost}/ap/v1/u/{Alice}/inbox"), create, () => _fixture.ServerA);

        // The note is stored in alice's outbox (the author's outbox always contains their posts — the
        // author can always see their own DMs; the outbox is owner-scoped, not a public surface).
        var outbox = await _aPersistence.Activities.GetOutboxAsync(aliceIri);
        Assert.Contains(outbox, i => i is IObject { Id: var id } && id == noteIri.Value);

        // FIXED (139.2-s5): an anonymous request to the public feed must NOT surface the direct post —
        // its audience (to=[bob], no as:Public) names a recipient, so it is non-public and is filtered
        // out for a requester who is not bob.
        var feedResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/public/feed?limit=50");
        feedResp.EnsureSuccessStatusCode();
        var feedDoc = JsonDocument.Parse(await feedResp.Content.ReadAsStringAsync());
        var feedItems = GetItemIds(feedDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, feedItems);

        // FIXED (139.2-s5): an anonymous request to global search must NOT find the direct post either.
        var searchResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/search?q=directvisibility");
        searchResp.EnsureSuccessStatusCode();
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchItems = GetItemIds(searchDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, searchItems);
    }

    // --- 3. Public post federated to remote is visible in remote object store -------------

    [Fact]
    public async Task PublicPost_FederatedToRemote_VisibleInRemoteObjectStore()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-fed-pub-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["federatedpublicvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(Iri.Public.Value) }],
            }],
        };

        // Deliver to bob's inbox on B (simulating federation of alice's public post to bob).
        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // B stored the embedded note in its object store (the remote instance's production read path).
        // Note: the object-document HTTP endpoint 404s for cross-instance notes (it reconstructs IRIs
        // from its own BaseUri, so a remote host's IRI does not match B's store — a known limitation
        // documented in 136.16). The object store is the authoritative read path.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "B should have stored the federated public note in its object store.");
        Assert.NotNull(storedNote);
        Assert.Equal(noteIri.Value, storedNote!.Id);
    }

    // --- 4. Direct/DM post federated to remote IS stored (pin current no-suppression gap) -

    [Fact]
    public async Task DirectPost_FederatedToRemote_StoredOnRemote_CurrentGap()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-fed-dm-{Guid.NewGuid():N}");

        // A "direct" post: to = [bob] (no as:Public, no cc).
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["federateddirectvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        // Deliver to bob's inbox on B.
        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // CURRENT BEHAVIOR (known gap): the direct post is stored on the remote instance.
        // Iris's CreateActivityHandler stores the embedded object unconditionally — it does not
        // check the to/cc audience and does not suppress non-public content. The federation
        // audience rewrite (RewriteOutboundAudienceAsync) also appends all followers to cc,
        // clobbering the original direct visibility.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "B stored the federated direct post (current behavior — no visibility-based suppression).");
        Assert.NotNull(storedNote);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static async Task<bool> DeliverDirectlyAsync(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => target().CreateHandler()));

        var result = await client.DeliverAsync(inbox, activity, CancellationToken.None);
        return result.IsSuccess;
    }

    private static List<string> GetItemIds(JsonElement root)
    {
        var ids = new List<string>();
        // Prefer `orderedItems` (the canonical AS2.0 form, 139.1 F-7); fall back to `items`.
        var items = root.TryGetProperty("orderedItems", out var ordered)
            ? ordered
            : root.TryGetProperty("items", out var plain) ? plain : default;
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        return ids;
    }
}

/// <summary>
/// Shared two-host fixture for the visibility tests.
/// </summary>
public sealed class CrossInstanceVisibilitySharedHost : SharedTwoHostFixture
{
    public CrossInstanceVisibilitySharedHost()
        : base(BuildOptions(out _, out _))
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions(
        out InMemoryPersistenceProvider aPersistence, out InMemoryPersistenceProvider bPersistence)
    {
        aPersistence = new InMemoryPersistenceProvider();
        bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(
            aPersistence, CrossInstanceVisibilityIntegrationTests.AHost,
            CrossInstanceVisibilityIntegrationTests.Alice);
        var bob = TestSeeder.SeedPersonWithKey(
            bPersistence, CrossInstanceVisibilityIntegrationTests.BHost,
            CrossInstanceVisibilityIntegrationTests.Bob);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceVisibilityIntegrationTests.AHost,
            Handle = CrossInstanceVisibilityIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, alice.ActorIri, alice.KeyId),
            Fetcher = new VisibilityRoutingFetcher(
                CrossInstanceVisibilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceVisibilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                alice.Key, alice.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceVisibilityIntegrationTests.BHost,
            Handle = CrossInstanceVisibilityIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(bob.Key, bob.ActorIri, bob.KeyId),
            Fetcher = new VisibilityRoutingFetcher(
                CrossInstanceVisibilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceVisibilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                bob.Key, bob.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
        };

        return (optionsA, optionsB);
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, keyId);
        var signer = new HttpSignatureSigner(store);
        return new IdentityKeys(store, provider, signer);
    }
}

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
/// actor IRI's host.
/// </summary>
file sealed class VisibilityRoutingFetcher : IActorDocumentFetcher
{
    private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

    public VisibilityRoutingFetcher(
        string aHost, Func<HttpMessageHandler> aHandlerFactory,
        string bHost, Func<HttpMessageHandler> bHandlerFactory,
        KeyPair signingKey, Iri signingActor)
    {
        _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
        {
            [aHost] = BuildFetcherFor(aHost, aHandlerFactory, signingKey, signingActor),
            [bHost] = BuildFetcherFor(bHost, bHandlerFactory, signingKey, signingActor),
        };
    }

    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        var host = new Uri(actorIri.Value).Host;
        if (_fetchers.TryGetValue(host, out var fetcher))
        {
            return fetcher.GetActorAsync(actorIri, ct);
        }

        return Task.FromResult<Actor?>(null);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>xUnit collection definition.</summary>
[CollectionDefinition("CrossInstanceVisibility")]
public sealed class CrossInstanceVisibilityCollection : ICollectionFixture<CrossInstanceVisibilitySharedHost>
{
}
