using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 integration test for the <strong>object document</strong> endpoint
/// (<c>GET /ap/v1/o/{**path}</c>, F-02/F-03/F-10): a content object stored by a
/// <see cref="Create"/> is served by its IRI, an object refreshed by an <see cref="Update"/> serves the
/// updated content, and an object tombstoned by a <see cref="Delete"/> serves the AS2.0
/// <see cref="Tombstone"/> ({"type":"Tombstone","id":…,"formerType":[…]}) — not a <c>404</c>. The object
/// IRI is the path relative to the route prefix (e.g. the Note at
/// <c>https://{host}/ap/v1/u/{handle}/notes/n1</c> is served at <c>GET /ap/v1/o/u/{handle}/notes/n1</c>).
/// </summary>
public sealed class ObjectEndpointIntegrationTests : IDisposable
{
    private const string Host = "obj.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri NoteIri = new($"{ActorIri}/notes/n1");
    private static readonly Iri OtherIri = new($"{ActorIri}/notes/n2");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";
    private readonly Uri _actor = new(ActorIri.Value);

    public ObjectEndpointIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);
        _server = StartServer(_persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A stored object is served by its IRI -------------------------------------------

    [Fact]
    public async Task StoredObject_ServedByIri_ReturnsNote()
    {
        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(NoteIri.Value, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("content").GetString());
    }

    // --- An Update refreshes the served content ----------------------------------------

    [Fact]
    public async Task UpdatedObject_ServedByIri_ReturnsUpdatedContent()
    {
        // The local owner updates the stored object (an embedded updated Note).
        var update = BuildUpdate(NoteIri, "hello (edited)");
        await new UpdateActivityHandler(_persistence, new DefaultLocalActorResolver(_persistence))
            .HandleAsync(new InboxDelivery(ActorIri, update), update);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello (edited)", doc.RootElement.GetProperty("content").GetString());
    }

    // --- A Delete tombstones the served object (F-10) ---------------------------------

    [Fact]
    public async Task DeletedObject_ServedByIri_ReturnsTombstone()
    {
        // The local owner deletes the stored object (a bare link reference — the common Delete shape).
        var delete = BuildDelete(NoteIri);
        await new DeleteActivityHandler(_persistence, new DefaultLocalActorResolver(_persistence))
            .HandleAsync(new InboxDelivery(ActorIri, delete), delete);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The object IRI still resolves (no 404) and serves the AS2.0 "deleted" marker.
        Assert.Equal("Tombstone", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(NoteIri.Value, doc.RootElement.GetProperty("id").GetString());
        // formerType serializes as a single string for a single former type.
        Assert.Equal("Note", doc.RootElement.GetProperty("formerType").GetString());
    }

    // --- An unknown object IRI 404s ----------------------------------------------------

    [Fact]
    public async Task UnknownObject_Returns404()
    {
        var response = await _http.GetAsync(ObjectPath(new Iri($"{ActorIri}/notes/does-not-exist")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- A sibling object is unaffected by one object's deletion -----------------------

    [Fact]
    public async Task SiblingObject_UnaffectedByOtherDeletion()
    {
        var delete = BuildDelete(NoteIri);
        await new DeleteActivityHandler(_persistence, new DefaultLocalActorResolver(_persistence))
            .HandleAsync(new InboxDelivery(ActorIri, delete), delete);

        // The sibling note (n2) is untouched — still a Note, not a tombstone.
        var response = await _http.GetAsync(ObjectPath(OtherIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Seeds the instance: a local actor (alice) and two content objects (n1, n2) as a
    /// <see cref="Create"/> would have stored them (in the object store).
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = NoteIri.Value,
            Content = ["hello"],
            AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
        }).GetAwaiter().GetResult();
        persistence.Objects.PutObjectAsync(new Note
        {
            Id = OtherIri.Value,
            Content = ["sibling"],
            AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
        }).GetAwaiter().GetResult();
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
        });

    /// <summary>
    /// The object-document path for an object IRI: the IRI's absolute path (the object IRI IS the
    /// endpoint IRI — the catch-all route is at the /ap/v1 group root, so GET {objectIri} reaches the
    /// object-document endpoint).
    /// </summary>
    private string ObjectPath(Iri objectIri) => ObjectPathFor(objectIri);

    private static string ObjectPathFor(Iri objectIri) => new Uri(objectIri.Value).AbsolutePath;

    private Update BuildUpdate(Iri objectIri, string content) => new()
    {
        Id = $"{ActorIri}/updates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = _actor }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = _actor }],
            },
        ],
    };

    private Delete BuildDelete(Iri objectIri) => new()
    {
        Id = $"{ActorIri}/deletes/{Guid.NewGuid():N}",
        Actor = [new Link { Href = _actor }],
        Object = [new Link { Href = new Uri(objectIri.Value) }],
    };

    // --- E2E: the full signed wire lifecycle (Create → Update → Delete → GET) -----------
    //
    // The direct-handler tests above prove each handler's logic in isolation; this test proves the
    // whole wire path end-to-end: a Create, Update, and Delete each arrive over the HTTP inbox (signed
    // by the local actor, signature-validated by resolving the actor's key from its own actor document),
    // are stored, and the object endpoint reflects each mutation — served as a Note after Create/Update,
    // and as a Tombstone after Delete.

    [Fact]
    public async Task FullLifecycle_SignedCreateThenUpdateThenDelete_ObjectEndpointReflectsEach()
    {
        // Seed a local actor with a real signing key so the inbound activities are signature-valid.
        var persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(persistence, Host, Handle);

        // The instance: the self-fetcher resolves the actor's key from its own actor document (fetched
        // over the in-process TestServer) so the SignatureValidationMiddleware validates each signed
        // activity. Deferred (LazyHandler) because the TestServer does not yet exist during construction.
        TestServer? self = null;
        using var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            IdentityKeys = new IdentityKeys(persistence.Keys, new InMemoryKeyProvider(persistence.Keys), new HttpSignatureSigner(persistence.Keys)),
            Fetcher = BuildSelfFetcher(seeded.Key, ActorIri, () => self!),
        });
        self = server;

        // The local actor's client, signed with the actor's key, posting to the actor's own inbox over
        // the in-process TestServer (the same wire path a real federation sender uses).
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(seeded.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, seeded.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            server.CreateHandler());

        var inbox = ActorIri.InboxOf();
        var baseUri = new Uri(_base);

        // 1. Create — a signed Create arrives over the inbox; the embedded Note is stored and served.
        var createStatus = await client.DeliverAsync(inbox, BuildCreate(NoteIri, "hello"));
        Assert.Equal(202, createStatus);
        await AssertServesNoteAsync(server, NoteIri, "hello", baseUri);

        // 2. Update — a signed Update (embedded edited Note) arrives; the served content is refreshed.
        var updateStatus = await client.DeliverAsync(inbox, BuildUpdate(NoteIri, "hello (edited)"));
        Assert.Equal(202, updateStatus);
        await AssertServesNoteAsync(server, NoteIri, "hello (edited)", baseUri);

        // 3. Delete — a signed Delete (bare link reference) arrives; the object is tombstoned.
        var deleteStatus = await client.DeliverAsync(inbox, BuildDelete(NoteIri));
        Assert.Equal(202, deleteStatus);
        await AssertServesTombstoneAsync(server, NoteIri, baseUri);
    }

    private Create BuildCreate(Iri objectIri, string content) => new()
    {
        Id = $"{ActorIri}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = _actor }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = _actor }],
            },
        ],
    };

    private static async Task AssertServesNoteAsync(TestServer server, Iri objectIri, string expectedContent, Uri baseUri)
    {
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false) { BaseAddress = baseUri };
        var response = await http.GetAsync(ObjectPathFor(objectIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(expectedContent, doc.RootElement.GetProperty("content").GetString());
    }

    private static async Task AssertServesTombstoneAsync(TestServer server, Iri objectIri, Uri baseUri)
    {
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false) { BaseAddress = baseUri };
        var response = await http.GetAsync(ObjectPathFor(objectIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Tombstone", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(objectIri.Value, doc.RootElement.GetProperty("id").GetString());
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that reaches the actor's own instance so the
    /// <c>SignatureValidationMiddleware</c> validates a signed inbound activity by resolving the actor's
    /// key from its own actor document (deferred: the TestServer does not exist yet while the server is
    /// being constructed).
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(KeyPair key, Iri actorIri, Func<TestServer> selfServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

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
