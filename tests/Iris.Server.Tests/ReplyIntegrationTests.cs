using System.Net;
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
/// F-12 integration test (note replies / threading + <c>tag</c> mentions + <c>attachment</c>
/// interpretation): a content object's replies are recorded by the <see cref="CreateActivityHandler"/>
/// from the inbound <see cref="Create"/>'s <c>inReplyTo</c>, served as a paged <c>OrderedCollection</c>
/// at <c>GET {object-iri}/replies</c>, and round-tripped through the client's
/// <see cref="IActivityPubClient.GetRepliesAsync"/>. A client-side
/// <see cref="IActivityPubClient.PostReplyAsync"/> posts a reply (with <c>inReplyTo</c> + an
/// <c>@mention</c> <c>tag</c>) over the signed wire; the receiving handler records the parent → child
/// edge so the reply appears under the parent's replies collection.
/// </summary>
/// <remarks>
/// Single instance (a.domain.local, actor <c>alice</c>). The parent note (n1) and its replies (r1, r2)
/// are stored directly; r2 carries a mention <c>tag</c> and an <c>attachment</c> (interpretation
/// round-trip). The client round-trips are exercised against the same in-process <see cref="TestServer"/>
/// the server uses (a federation sender's wire path): a signed <see cref="IActivityPubClient"/> is built
/// with a transport that rewrites the client's absolute-IRI requests to the relative paths the TestServer
/// routes (the <see cref="TestServer"/> handler resolves relative paths against its own base, while the
/// client's pipeline <see cref="HttpClient"/> carries no <see cref="HttpClient.BaseAddress"/>, so an
/// absolute-IRI request would be rejected).
/// </remarks>
[Collection("ReplyIntegration")]
public sealed class ReplyIntegrationTests : IAsyncLifetime
{
    internal const string Host = "reply.domain.local";
    internal const string Handle = "alice";
    internal static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri ParentIri = new($"{ActorIri}/notes/n1");
    private static readonly Iri Reply1 = new($"{ActorIri}/notes/r1");
    private static readonly Iri Reply2 = new($"{ActorIri}/notes/r2");
    private static readonly Iri Mentioned = new($"https://{Host}/ap/v1/u/bob");

    private readonly ReplyIntegrationSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";
    private readonly Uri _actor = new(ActorIri.Value);

    public ReplyIntegrationTests(ReplyIntegrationSharedHost fixture)
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

    // --- The replies collection lists the objects that reply to the parent -----------

    [Fact]
    public async Task RepliesEndpoint_ListsReplies()
    {
        var response = await _http.GetAsync(RepliesPath(ParentIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(JsonDoc.ItemId).ToArray();
        Assert.Equal([Reply1.Value, Reply2.Value], items);
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(ParentIri.RepliesOf().Value, doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task RepliesEndpoint_NoReplies_ReturnsEmptyCollection()
    {
        // n3 is stored but has no replies → an empty OrderedCollection (not a 404).
        var n3 = new Iri($"{ActorIri}/notes/n3");
        var response = await _http.GetAsync(RepliesPath(n3));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task RepliesEndpoint_UnknownObject_Returns404()
    {
        var response = await _http.GetAsync(RepliesPath(new Iri($"{ActorIri}/notes/does-not-exist")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RepliesEndpoint_Paging_LimitBoundsItems()
    {
        var response = await _http.GetAsync(RepliesPath(ParentIri) + "?limit=1");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // limit=1 → the first page carries one item; totalItems still reflects the full count.
        Assert.Single(JsonDoc.GetItems(doc.RootElement));
        Assert.Equal(2, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- A reply's mention (tag) + attachment are served intact (interpretation) -----

    [Fact]
    public async Task Reply_MentionAndAttachment_ServedByObjectEndpoint()
    {
        // r2 (seeded) carries a Mention tag (→ bob) and an Image attachment (→ a media IRI).
        var response = await _http.GetAsync(ObjectPath(Reply2));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The mention: a tag entry carrying the mentioned actor's IRI. A single <see cref="Mention"/>
        // serializes as a scalar (its href) via the one-or-many converter, so the entry is normalized
        // before reading; the mention's IRI is the scalar string (or, for multiple tags, the href).
        Assert.True(root.TryGetProperty("tag", out var tag), "reply should carry a mention tag");
        var mentionIris = SingleElement(tag)
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : (e.TryGetProperty("href", out var h) ? h.GetString() : null))
            .Where(s => s is not null)
            .Cast<string>()
            .ToList();
        Assert.Contains(Mentioned.Value, mentionIris);

        // The attachment: an attachment entry (an Image) carrying the media IRI.
        Assert.True(root.TryGetProperty("attachment", out var attachment), "reply should carry an attachment");
        var image = SingleElement(attachment).Single(e => e.TryGetProperty("type", out var t) && t.GetString() == "Image");
        Assert.Equal("https://cdn.example.com/media/42.jpg", image.GetProperty("id").GetString());
    }

    // --- Client round-trip ----------------------------------------------------------

    [Fact]
    public async Task Client_GetRepliesAsync_RoundTrips()
    {
        using var client = CreateClient();
        var items = new List<string>();
        await foreach (var item in client.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        // The client reads the same replies collection the endpoint serves.
        Assert.Equal([Reply1.Value, Reply2.Value], items);
    }

    // --- E2E: a signed PostReplyAsync lands in the parent's replies collection ------

    [Fact]
    public async Task Client_PostReplyAsync_ReplySurfacesUnderParent()
    {
        using var client = CreateClient();

        var result = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "a fresh reply to n1",
            mentions: [Mentioned]);

        // The signed Create reaches alice's inbox; the handler records the parent → child edge.
        Assert.Equal(202, result.StatusCode);

        // The new reply is now listed under the parent's replies collection (3 total: r1, r2, new).
        using var reader = CreateClient();
        var items = new List<string>();
        await foreach (var item in reader.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        Assert.Equal(3, items.Count);
        Assert.Contains(Reply1.Value, items);
        Assert.Contains(Reply2.Value, items);
        var newReplyIri = items.Single(i => i != Reply1.Value && i != Reply2.Value);

        // The posted reply carries the inReplyTo (parent) and the @mention tag (interpretation
        // round-trip over the wire).
        var response = await _http.GetAsync(ObjectPath(new Iri(newReplyIri)));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(ParentIri.Value, SingleElement(root.GetProperty("inReplyTo")).Single().GetString());
        // The posted reply carries the @mention tag. A single Mention serializes as a bare string
        // (its href), not an object with type/href (the one-or-many converter), so the tag array's
        // single element is the mention's IRI as a string.
        Assert.True(root.TryGetProperty("tag", out var tag));
        var tags = SingleElement(tag);
        Assert.Equal(Mentioned.Value, tags.Single().GetString());
    }

    // --- E2E: a signed PostReplyAsync with a hashtag carries a Hashtag tag over the wire (54.14) ---

    [Fact]
    public async Task Client_PostReplyAsync_HashtagCarriesHashtagTag()
    {
        using var client = CreateClient();

        var result = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "a fresh reply #hashtag-roundtrip",
            hashtags: ["#hashtag-roundtrip"]);

        // The signed Create reaches alice's inbox; the handler records the parent → child edge.
        Assert.Equal(202, result.StatusCode);

        // Locate the new reply (the one that is not r1/r2) and fetch its stored document.
        using var reader = CreateClient();
        var items = new List<string>();
        await foreach (var item in reader.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        var known = new[] { Reply1.Value, Reply2.Value };
        var newReplyIri = items.Single(i => !known.Contains(i));

        var response = await _http.GetAsync(ObjectPath(new Iri(newReplyIri)));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The posted reply's `tag` carries a Hashtag object (type + name + href) — unlike a Mention
        // (which serializes as a bare href string), a Hashtag is a full object because the library has
        // no dedicated Hashtag type (it is a generic Object of type Hashtag).
        Assert.True(root.TryGetProperty("tag", out var tag));
        var tags = SingleElement(tag);
        var hashtag = tags.Single();
        Assert.Equal("object", hashtag.ValueKind.ToString().ToLowerInvariant());
        // The type list includes "Hashtag" (the base "Object" is appended by the library).
        var typeList = hashtag.GetProperty("type").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("Hashtag", typeList);
        Assert.Equal("#hashtag-roundtrip", hashtag.GetProperty("name").GetString());
        // The href points at this instance's hashtag search for that tag.
        var expectedHref = $"https://{Host}/search?q=%23hashtag-roundtrip";
        Assert.Equal(expectedHref, hashtag.GetProperty("href").GetString());
    }

    // --- 57.3: conversationId — the server sets the thread root IRI on replies -----------

    [Fact]
    public async Task Client_PostReplyAsync_ReplyCarriesConversationId()
    {
        using var client = CreateClient();

        // Post a reply to n1 (the parent). The server's EnsureConversationIdAsync resolves the
        // thread root: n1 has no conversationId (it was seeded directly, not through the
        // outbox-publish path), so the parent's own IRI is used.
        var result = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "a reply in a thread");

        Assert.Equal(202, result.StatusCode);

        // Locate the new reply (the one that is not r1/r2) and fetch its stored document.
        using var reader = CreateClient();
        var items = new List<string>();
        await foreach (var item in reader.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        var known = new[] { Reply1.Value, Reply2.Value };
        var newReplyIri = items.Single(i => !known.Contains(i));

        var response = await _http.GetAsync(ObjectPath(new Iri(newReplyIri)));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The reply's conversationId is the parent's IRI (the thread root).
        Assert.True(root.TryGetProperty("conversationId", out var convId), "reply should carry a conversationId");
        Assert.Equal(ParentIri.Value, convId.GetString());
    }

    // --- 57.3: conversationId — an explicit conversationId is preserved -------------------

    [Fact]
    public async Task Client_PostReplyAsync_ExplicitConversationId_Preserved()
    {
        using var client = CreateClient();

        // Post a reply with an explicit conversationId (the thread root). The server's
        // EnsureConversationIdAsync sees it is already set and preserves it.
        var customConvId = new Iri($"https://{Host}/ap/v1/u/alice/notes/thread-root");
        var result = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "a reply with explicit conversationId",
            conversationIri: customConvId);

        Assert.Equal(202, result.StatusCode);

        using var reader = CreateClient();
        var items = new List<string>();
        await foreach (var item in reader.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items.Add(ResolveIri(item));
        }

        var known = new[] { Reply1.Value, Reply2.Value };
        var newReplyIri = items.Single(i => !known.Contains(i));

        var response = await _http.GetAsync(ObjectPath(new Iri(newReplyIri)));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The reply's conversationId is the explicitly-supplied thread root, not the parent's IRI.
        Assert.True(root.TryGetProperty("conversationId", out var convId), "reply should carry a conversationId");
        Assert.Equal(customConvId.Value, convId.GetString());
    }

    // --- 57.3: conversationId — a second reply in the same thread inherits the root --------

    [Fact]
    public async Task Client_PostReplyAsync_SecondReply_InheritsConversationId()
    {
        using var client = CreateClient();

        // First, post a reply to n1. The server sets its conversationId to n1's IRI.
        var result1 = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "first reply in thread");
        Assert.Equal(202, result1.StatusCode);

        // Find the first reply's IRI.
        using var reader = CreateClient();
        var items1 = new List<string>();
        await foreach (var item in reader.GetRepliesAsync(ParentIri, new CollectionQuery { Limit = 10 }))
        {
            items1.Add(ResolveIri(item));
        }
        var known = new[] { Reply1.Value, Reply2.Value };
        var firstNewReply = items1.Single(i => !known.Contains(i));

        // Now post a second reply to the FIRST reply (a nested reply). The server's
        // EnsureConversationIdAsync looks up the first reply's conversationId (which is n1's IRI)
        // and uses it — the thread root is preserved across nesting.
        var result2 = await client.PostReplyAsync(
            ActorIri,
            new Iri(firstNewReply),
            "nested reply in same thread");
        Assert.Equal(202, result2.StatusCode);

        // Find the nested reply (it's under firstNewReply's replies, not directly under ParentIri).
        using var reader2 = CreateClient();
        var nestedItems = new List<string>();
        await foreach (var item in reader2.GetRepliesAsync(new Iri(firstNewReply), new CollectionQuery { Limit = 10 }))
        {
            nestedItems.Add(ResolveIri(item));
        }
        Assert.Single(nestedItems);
        var nestedReplyIri = nestedItems[0];

        var response = await _http.GetAsync(ObjectPath(new Iri(nestedReplyIri)));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The nested reply's conversationId is the thread root (n1's IRI), inherited from the
        // first reply's conversationId.
        Assert.True(root.TryGetProperty("conversationId", out var convId), "nested reply should carry a conversationId");
        Assert.Equal(ParentIri.Value, convId.GetString());
    }

    // --- 57.3: conversationId — inbound Pleroma-style conversationId is preserved ---------

    [Fact]
    public async Task Inbound_Note_WithConversationId_Preserved()
    {
        // A Pleroma server sends a note with a pre-set conversationId. The server's
        // EnsureConversationIdAsync sees it is already set and preserves it (does not overwrite).
        var pleromaConvId = new Iri("https://pleroma.example/objects/thread-123");
        var note = new Note
        {
            Id = $"{ActorIri}/notes/pleroma-1",
            Content = ["a note from pleroma"],
            AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
        };
        note.SetConversationId(pleromaConvId);
        await _persistence.Objects.PutObjectAsync(note);

        var response = await _http.GetAsync(ObjectPath(new Iri($"{ActorIri}/notes/pleroma-1")));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("conversationId", out var convId), "note should carry a conversationId");
        Assert.Equal(pleromaConvId.Value, convId.GetString());
    }

    // --- Helpers --------------------------------------------------------------------

    /// <summary>
    /// Seeds the instance: a local actor (alice, with a signing key for the signed-wire round-trip),
    /// the parent note (n1), two stored replies (r1, r2 — r2 carries a mention tag + an attachment),
    /// and a sibling note (n3) with no replies. The parent → reply edges are recorded as the
    /// <see cref="CreateActivityHandler"/> would.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        // Alice's actor is restored with her existing key (the key store is preserved across resets;
        // her data-store actors are cleared by Reset()). The objects + reply edges are re-seeded.
        TestSeeder.SeedPersonWithExistingKey(persistence, Host, Handle, new Iri($"{ActorIri.Value}#key-1"));

        var actor = new Link { Href = new Uri(ActorIri.Value) };

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = ParentIri.Value,
            Content = ["parent note"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = Reply1.Value,
            Content = ["first reply"],
            AttributedTo = [actor],
            InReplyTo = [new Link { Href = new Uri(ParentIri.Value) }],
        }).GetAwaiter().GetResult();

        var reply2 = new Note
        {
            Id = Reply2.Value,
            Content = ["second reply (mentions bob, has an image)"],
            AttributedTo = [actor],
            InReplyTo = [new Link { Href = new Uri(ParentIri.Value) }],
            Tag = [new Mention { Href = new Uri(Mentioned.Value) }],
            Attachment = [new Image { Id = "https://cdn.example.com/media/42.jpg" }],
        };
        persistence.Objects.PutObjectAsync(reply2).GetAwaiter().GetResult();

        persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"{ActorIri}/notes/n3",
            Content = ["sibling, no replies"],
            AttributedTo = [actor],
        }).GetAwaiter().GetResult();

        // The parent → child reply edges (as the Create handler would record them).
        persistence.Replies.RecordReplyAsync(ParentIri, Reply1).GetAwaiter().GetResult();
        persistence.Replies.RecordReplyAsync(ParentIri, Reply2).GetAwaiter().GetResult();
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
    /// federation sender uses). The client builds its requests from absolute IRIs (e.g.
    /// <c>https://reply.domain.local/…</c>); a <see cref="LazyHandler"/> forwards each request through a
    /// plain <see cref="HttpClient"/> (no <see cref="HttpClient.BaseAddress"/>) so the
    /// <see cref="TestServer"/> <c>ClientHandler</c> routes the absolute URI's path correctly.
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
    /// The object-document path for an object IRI (mirrors <c>ObjectEndpointIntegrationTests</c>):
    /// <c>/ap/v1/o/</c> + the IRI's path relative to the route prefix.
    /// </summary>
    private static string ObjectPath(Iri objectIri)
    {
        // The object IRI IS the endpoint IRI (the catch-all route is at the /ap/v1 group root, so
        // GET {objectIri} reaches the object-document endpoint). The path is the object IRI's absolute
        // path (e.g. /ap/v1/u/alice/notes/n1).
        return new Uri(objectIri.Value).AbsolutePath;
    }

    /// <summary>
    /// The replies-collection path for an object IRI: <c>ObjectPath(objectIri) + "/replies"</c>.
    /// </summary>
    private static string RepliesPath(Iri objectIri) => $"{ObjectPath(objectIri)}/replies";

    /// <summary>
    /// Normalizes a JSON-LD one-or-many property (the ActivityStreams converter emits a single entry as
    /// a scalar string/object, not an array) to a list of elements.
    /// </summary>
    private static List<JsonElement> SingleElement(JsonElement element)
        => element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray()]
            : [element];

    /// <summary>
    /// Resolves the IRI of a replies-collection item: an embedded object contributes its <c>Id</c>; a
    /// link contributes its <c>Href</c> (the server serves reply IRIs as links, but a client may also
    /// receive an embedded object).
    /// </summary>
    private static string ResolveIri(IObjectOrLink item) => item switch
    {
        IObject { Id: { } id } => id,
        ILink { Href: { } href } => href.ToString(),
        _ => throw new InvalidOperationException("replies item carries no IRI"),
    };
}

/// <summary>
/// Shared-host fixture for <see cref="ReplyIntegrationTests"/> (single instance, reply.domain.local,
/// actor alice). Seeds alice with a key ONCE (the key store is preserved across per-method resets), so
/// the self-fetcher's copy of alice's key and the key the client signs with stay the same instance.
/// The test class resets + re-seeds (actors without regenerating keys, objects, reply edges) before
/// each method.
/// </summary>
public sealed class ReplyIntegrationSharedHost : SharedHostFixture
{
    public ReplyIntegrationSharedHost()
        : base(BuildOptions())
    {
    }

    private static ActivityPubHostOptions BuildOptions()
    {
        var persistence = new InMemoryPersistenceProvider();
        // Initial seed generates + stores alice's key (the key store is preserved across resets).
        var (_, _, _) = TestSeeder.SeedPersonWithKey(persistence, ReplyIntegrationTests.Host, ReplyIntegrationTests.Handle);
        var keyId = new Iri($"{ReplyIntegrationTests.ActorIri.Value}#key-1");
        persistence.Keys.TryGetKey(keyId, out var key);

        var serverRef = SharedHostFixture.ServerRefFor(persistence);

        return new ActivityPubHostOptions
        {
            Host = ReplyIntegrationTests.Host,
            Handle = ReplyIntegrationTests.Handle,
            Persistence = persistence,
            Fetcher = ReplyIntegrationTests.BuildSelfFetcher(key!, serverRef),
        };
    }
}

/// <summary>
/// xunit collection definition for the reply shared-host fixture.
/// </summary>
[CollectionDefinition("ReplyIntegration")]
public sealed class ReplyIntegrationCollection : ICollectionFixture<ReplyIntegrationSharedHost>
{
}
