using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

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
public sealed class ReplyIntegrationTests : IDisposable
{
    private const string Host = "reply.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri ParentIri = new($"{ActorIri}/notes/n1");
    private static readonly Iri Reply1 = new($"{ActorIri}/notes/r1");
    private static readonly Iri Reply2 = new($"{ActorIri}/notes/r2");
    private static readonly Iri Mentioned = new($"https://{Host}/ap/v1/u/bob");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";
    private readonly Uri _actor = new(ActorIri.Value);

    public ReplyIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);

        // A self-fetcher resolves alice's key from her own actor document (fetched over the in-process
        // TestServer) so the SignatureValidationMiddleware validates the signed requests the client
        // sends (a bare default fetcher uses a real HttpClientHandler, which cannot reach the in-process
        // server → the signed POST is rejected). Deferred (LazyHandler) because the TestServer does not
        // yet exist during construction.
        var keyId = new Iri($"{ActorIri.Value}#key-1");
        _persistence.Keys.TryGetKey(keyId, out var key);
        TestServer? self = null;
        _server = StartServer(_persistence, BuildSelfFetcher(key!, () => self!));
        self = _server;
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
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

        var status = await client.PostReplyAsync(
            ActorIri,
            ParentIri,
            "a fresh reply to n1",
            mentions: [Mentioned]);

        // The signed Create reaches alice's inbox; the handler records the parent → child edge.
        Assert.Equal(202, status);

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

    // --- Helpers --------------------------------------------------------------------

    /// <summary>
    /// Seeds the instance: a local actor (alice, with a signing key for the signed-wire round-trip),
    /// the parent note (n1), two stored replies (r1, r2 — r2 carries a mention tag + an attachment),
    /// and a sibling note (n3) with no replies. The parent → reply edges are recorded as the
    /// <see cref="CreateActivityHandler"/> would.
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var (_, aliceIri, _) = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);
        var _ = aliceIri;

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

    private static TestServer StartServer(InMemoryPersistenceProvider persistence, IActorDocumentFetcher fetcher)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            Fetcher = fetcher,
        });

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that reaches the actor's own instance so the
    /// <c>SignatureValidationMiddleware</c> validates a signed inbound activity by resolving the actor's
    /// key from its own actor document (deferred: the TestServer does not exist yet while the server is
    /// being constructed).
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(ISigningKey key, Func<TestServer> selfServer)
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
            new LazyHandler(() => _server.CreateHandler()));
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

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers to a <see cref="TestServer"/> created after this
    /// handler (chicken-and-egg: the server's own fetcher must reach the in-process server, which does not
    /// exist yet while the server is being constructed).
    /// </summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> innerFactory) : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory = innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            return _client.SendAsync(clone, cancellationToken);
        }
    }
}
