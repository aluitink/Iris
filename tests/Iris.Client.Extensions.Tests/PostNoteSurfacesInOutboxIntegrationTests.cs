using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Client.Extensions.Tests;

/// <summary>
/// Phase 11 Slice 11.6 end-to-end test (gap J-6 — the server half of "post a note", J-8): a real
/// <see cref="TestServer"/> runs the Iris ActivityPub server. A local actor posts a note through the
/// client's one-call <see cref="IActivityPubClient.PostNoteAsync"/> (a signed <see cref="KristofferStrube.ActivityStreams.Create"/>
/// delivered to the author's own inbox). The server's <see cref="CreateActivityHandler"/> records the
/// <see cref="KristofferStrube.ActivityStreams.Create"/> in the author's outbox, so the post is
/// <em>surfaced</em> — reading the author's outbox collection endpoint over HTTP returns the post. This
/// proves the "post → surfaced in feed" journey is complete for a local actor: the post is not only
/// accepted and stored, it is visible in the author's own outbox.
/// </summary>
public sealed class PostNoteSurfacesInOutboxIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Author = "alice";
    private const string Password = "correct-horse-battery";
    private const string AuthorIri = $"https://{Host}/ap/v1/u/{Author}";
    private const string AuthorKeyIri = $"{AuthorIri}#key-1";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;

    public PostNoteSurfacesInOutboxIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // The author gets a real signing key (embedded as a JWK in the actor doc) so the server's
        // SignatureValidationMiddleware can verify the signed Create.
        var (authorKey, _, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Author);

        // The server's inbound key resolver must fetch the author's actor doc to verify the post's
        // signature. In a single-instance test that doc lives on THIS server, so the fetcher is wired
        // to reach the in-process TestServer. The TestServer is created by ActivityPubHostFactory.Create
        // (below), which is the very call that wires the fetcher — a chicken-and-egg. The LazyHandler
        // therefore captures a Func<TestServer> (deferred to first use) rather than a server reference.
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Author,
            Persistence = _persistence,
            CredentialValidator = new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = username == Author &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }),
            Fetcher = BuildSelfFetcher(authorKey, () => _server!),
        });
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task PostNoteAsync_SignedCreateIsRecordedInAuthorOutbox()
    {
        // Authenticate as the author (Basic auth → owner-only doc + PEM key).
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(AuthorIri), Author, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false,
            // The in-process TestServer transport does not clone the request between sends, so a
            // retried post (RetryHandler) would re-send the same HttpRequestMessage and be rejected.
            // Real deployments use a socket transport (which clones internally); disable retry here to
            // keep the single-attempt post on the in-process wire.
            EnableRetry = false,
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        await bundle.Session.LoginAsync(new Iri(AuthorIri));

        // Build a signed client routed to the in-process server and post a note. The client builds the
        // Create (with the embedded Note) and delivers it to the author's own inbox, signed as the author.
        using var client = bundle.CreateClient(new Iri(AuthorIri), _server.CreateHandler());
        var status = await client.PostNoteAsync(new Iri(AuthorIri), "hello — now surfaced in my outbox");
        Assert.Equal(202, status);

        // THE SERVER HALF (J-8): the CreateActivityHandler recorded the Create in the author's outbox.
        // Read the author's outbox collection endpoint over HTTP and assert the post is surfaced there.
        var http = _server.CreateClient();
        var response = await http.GetAsync($"/ap/v1/u/{Author}/outbox");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var itemsElement = doc.RootElement.GetProperty("items");

        // The outbox has a single item (the one post), so the OneOrMultipleConverter serialized
        // "items" as a JSON object (not an array). Handle both shapes: an array of items, or a single
        // item object. Extract each item's "id" (a link → its IRI).
        List<string> itemIds = itemsElement.ValueKind switch
        {
            JsonValueKind.Array => itemsElement.EnumerateArray().Select(ItemId).ToList(),
            JsonValueKind.Object => [ItemId(itemsElement)],
            _ => throw new InvalidOperationException($"unexpected items shape: {itemsElement.ValueKind}"),
        };

        // The outbox contains the Create the author just posted (its IRI, as a link). The post is
        // surfaced: a local member's note is now visible in the author's own feed.
        Assert.Contains(itemIds, id => id.StartsWith(AuthorIri));

        static string ItemId(JsonElement e)
            => e.ValueKind == JsonValueKind.Object ? e.GetProperty("id").GetString()! : e.GetString()!;
    }

    // --- Helpers ----------------------------------------------------------------------

    private static IActorDocumentFetcher BuildSelfFetcher(KeyPair authorKey, Func<TestServer> server)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(AuthorIri), authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = new Iri(AuthorIri), EnableRetry = false },
            new LazyHandler(server));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private sealed class LazyHandler(Func<TestServer> server) : HttpMessageHandler
    {
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= server().CreateHandler(), disposeHandler: false);

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
