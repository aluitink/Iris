using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Decision 056 (d) integration test — the per-object interaction counters (like / boost counts). A
/// content object's likers / announcers are recorded in the <see cref="ILikeStore"/> /
/// <see cref="IAnnounceStore"/> reverse indexes and served as <em>full, non-paged</em>
/// <c>OrderedCollection</c>s at <c>GET {object-iri}/likes</c> and <c>GET {object-iri}/shares</c>
/// respectively. These are <em>extension</em> collections (not core ActivityStreams <c>Object</c>
/// properties — the only core object collection is <c>replies</c>), exposed under the <em>bare,
/// non-namespaced</em> terms the wider ActivityPub ecosystem uses for object-side interaction
/// collections, so an ecosystem client reads the count uniformly for local and external objects. A
/// client reads the counts through <see cref="IActivityPubClient.GetLikesAsync"/> /
/// <see cref="IActivityPubClient.GetSharesAsync"/>.
/// </summary>
/// <remarks>
/// Single instance (a.domain.local, actor <c>alice</c>). The note (n1) is stored directly, and its
/// likers / announcers are recorded as the <see cref="LikeActivityHandler"/> /
/// <see cref="AnnounceActivityHandler"/> would. A sibling note (n3) has no likers / announcers (an empty
/// collection, not a 404). An unknown object 404s. The client round-trips are exercised against the
/// same in-process <see cref="TestServer"/> the server uses (the same wire path <see cref="ReplyIntegrationTests"/>
/// uses).
/// </remarks>
[Collection("InteractionCollection")]
public sealed class InteractionCollectionIntegrationTests : IAsyncLifetime
{
    internal const string Host = "interaction.domain.local";
    internal const string Handle = "alice";
    internal static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri BobIri = new($"https://{Host}/ap/v1/u/bob");
    private static readonly Iri CarolIri = new($"https://{Host}/ap/v1/u/carol");
    private static readonly Iri Note1 = new($"{ActorIri}/notes/n1");
    private static readonly Iri Note3 = new($"{ActorIri}/notes/n3");

    private readonly InteractionCollectionSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";

    public InteractionCollectionIntegrationTests(InteractionCollectionSharedHost fixture)
    {
        _fixture = fixture;
        _persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_persistence);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    // --- The /likes collection lists the object's likers (the like counter) ----------------

    [Fact]
    public async Task LikesEndpoint_ListsLikers()
    {
        var response = await _http.GetAsync(LikesPath(Note1));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(JsonDoc.ItemId).ToArray();
        // Two likers (bob, carol) — order is not guaranteed (a set), so assert as a set.
        Assert.Equal(2, items.Length);
        Assert.Contains(BobIri.Value, items);
        Assert.Contains(CarolIri.Value, items);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(Note1.LikesOf().Value, doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task LikesEndpoint_NoLikes_ReturnsEmptyCollection()
    {
        // n3 is stored but has no likers → an empty OrderedCollection (not a 404).
        var response = await _http.GetAsync(LikesPath(Note3));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task LikesEndpoint_UnknownObject_Returns404()
    {
        var response = await _http.GetAsync(LikesPath(new Iri($"{ActorIri}/notes/does-not-exist")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- The /shares collection lists the object's announcers (the boost counter) -----------

    [Fact]
    public async Task SharesEndpoint_ListsAnnouncers()
    {
        var response = await _http.GetAsync(SharesPath(Note1));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(JsonDoc.ItemId).ToArray();
        // One announcer (bob).
        Assert.Equal([BobIri.Value], items);
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(Note1.SharesOf().Value, doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SharesEndpoint_NoBoosts_ReturnsEmptyCollection()
    {
        var response = await _http.GetAsync(SharesPath(Note3));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task SharesEndpoint_UnknownObject_Returns404()
    {
        var response = await _http.GetAsync(SharesPath(new Iri($"{ActorIri}/notes/does-not-exist")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Client round-trip (counts read uniformly via the bare extension terms) -------------

    [Fact]
    public async Task Client_GetLikesAsync_CountsLikers()
    {
        using var client = CreateClient();
        var items = new List<string>();
        await foreach (var item in client.GetLikesAsync(Note1, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        Assert.Equal(2, items.Count);
        Assert.Contains(BobIri.Value, items);
        Assert.Contains(CarolIri.Value, items);
    }

    [Fact]
    public async Task Client_GetSharesAsync_CountsAnnouncers()
    {
        using var client = CreateClient();
        var items = new List<string>();
        await foreach (var item in client.GetSharesAsync(Note1, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        Assert.Equal([BobIri.Value], items);
    }

    [Fact]
    public async Task Client_GetLikesAsync_NoLikes_YieldsNothing()
    {
        using var client = CreateClient();
        var count = 0;
        await foreach (var _ in client.GetLikesAsync(Note3, new CollectionQuery { Limit = 10 }))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Seeds the instance: a local actor (alice, with a signing key for the signed-wire round-trip),
    /// the note (n1) with two likers (bob, carol) and one announcer (bob), and a sibling note (n3) with
    /// no likers / announcers. The like / announce edges are recorded as the <see cref="LikeActivityHandler"/>
    /// / <see cref="AnnounceActivityHandler"/> would.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var (_, aliceIri, _) = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);
        var _ = aliceIri;

        var actor = new Link { Href = new Uri(ActorIri.Value) };

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note1.Value,
            Content = ["a note that gets liked and boosted"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Note3.Value,
            Content = ["a note with no interactions"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        // The like edges (as the Like handler would record them): bob + carol like n1.
        persistence.Likes.RecordLikeAsync(BobIri, Note1).GetAwaiter().GetResult();
        persistence.Likes.RecordLikeAsync(CarolIri, Note1).GetAwaiter().GetResult();

        // The announce edge (as the Announce handler would record it): bob boosts n1.
        persistence.Announces.RecordAnnounceAsync(BobIri, Note1).GetAwaiter().GetResult();
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that reaches the actor's own instance so the
    /// <c>SignatureValidationMiddleware</c> validates a signed inbound activity by resolving the actor's
    /// key from its own actor document (deferred: the TestServer does not exist yet while the server is
    /// being constructed).
    /// </summary>
    internal static IActorDocumentFetcher BuildSelfFetcher(ISigningKey key, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// A client signed as alice that reaches this instance's in-process server (the same wire path a
    /// federation sender uses).
    /// </summary>
    private IActivityPubClient CreateClient()
    {
        var keyStore = new InMemoryKeyStore();
        var keyId = new Iri($"{ActorIri.Value}#key-1");
        _persistence.Keys.TryGetKey(keyId, out var key);
        keyStore.PutKey(key!);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, keyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => _fixture.Server.CreateHandler()));
    }

    /// <summary>
    /// The object-document path for an object IRI (the object IRI IS the endpoint IRI).
    /// </summary>
    private static string ObjectPath(Iri objectIri) => new Uri(objectIri.Value).AbsolutePath;

    /// <summary>
    /// The <c>likes</c>-collection path for an object IRI: <c>ObjectPath(objectIri) + "/likes"</c>.
    /// </summary>
    private static string LikesPath(Iri objectIri) => $"{ObjectPath(objectIri)}/likes";

    /// <summary>
    /// The <c>shares</c>-collection path for an object IRI: <c>ObjectPath(objectIri) + "/shares"</c>.
    /// </summary>
    private static string SharesPath(Iri objectIri) => $"{ObjectPath(objectIri)}/shares";

    /// <summary>
    /// Resolves the IRI of a collection item: an embedded object contributes its <c>Id</c>; a link
    /// contributes its <c>Href</c> (the server serves the actor IRIs as links, but a client may also
    /// receive an embedded object).
    /// </summary>
    private static string ResolveIri(IObjectOrLink item) => item switch
    {
        IObject { Id: { } id } => id,
        ILink { Href: { } href } => href.ToString(),
        _ => throw new InvalidOperationException("collection item carries no IRI"),
    };
}

/// <summary>
/// Shared-host fixture for <see cref="InteractionCollectionIntegrationTests"/> (single instance,
/// interaction.domain.local, actor alice). Builds the self-fetcher with a deferred server reference
/// (<c>this.Server</c>) so the fetcher's <see cref="LazyHandler"/> factory resolves the host after it is
/// fully built. Built once per xunit collection; the test class resets + reseeds before each method.
/// </summary>
public sealed class InteractionCollectionSharedHost : SharedHostFixture
{
    public InteractionCollectionSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        InteractionCollectionIntegrationTests.SeedForFixture(persistence);

        var keyId = new Iri($"{InteractionCollectionIntegrationTests.ActorIri.Value}#key-1");
        persistence.Keys.TryGetKey(keyId, out var key);

        // Deferred server reference: the TestServer does not exist yet while the server is being
        // constructed. The LazyHandler defers CreateHandler() until a request, at which point the
        // SharedHostFixture constructor has registered the server in ServerRegistry.
        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = InteractionCollectionIntegrationTests.Host,
            Handle = InteractionCollectionIntegrationTests.Handle,
            Persistence = persistence,
            Fetcher = InteractionCollectionIntegrationTests.BuildSelfFetcher(key!, serverRef),
        };
    }
}

/// <summary>
/// xunit collection definition for the interaction-collection shared-host fixture.
/// </summary>
[CollectionDefinition("InteractionCollection")]
public sealed class InteractionCollectionCollection : ICollectionFixture<InteractionCollectionSharedHost>
{
}
