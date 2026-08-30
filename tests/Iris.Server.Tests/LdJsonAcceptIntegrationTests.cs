using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 13.2 — <c>application/ld+json</c> accept behavior. Decision #4: Iris produces
/// <c>application/activity+json</c> on outbound and accepts <em>both</em>
/// <c>application/activity+json</c> and <c>application/ld+json</c> on inbound. This test proves the
/// accept half: a signed federation activity delivered with <c>Content-Type: application/ld+json</c>
/// is accepted by the inbox (202) and processed (the activity is stored and the embedded object is
/// served) — the server does not reject the legacy MIME type.
/// </summary>
public sealed class LdJsonAcceptIntegrationTests : IDisposable
{
    private const string Host = "ldjson.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri NoteIri = new($"{ActorIri}/notes/n1");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _key;
    private readonly string _base = $"https://{Host}";

    public LdJsonAcceptIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _key = Seed(_persistence);
        _server = StartServer(_persistence, _key);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A signed Create with Content-Type: application/ld+json is accepted ----------------

    [Fact]
    public async Task Inbox_AcceptsLdJsonContentType()
    {
        // Build a signed Create activity and deliver it with Content-Type: application/ld+json
        // (the legacy MIME type some older implementations use). The ServerToServer signature profile
        // covers content-type, so the signature is computed over "application/ld+json" — if the
        // server rejected the content type, the activity would not be processed.
        var create = BuildCreate(NoteIri, "an ld+json note");
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync(
            $"/ap/v1/u/{Handle}/inbox",
            body,
            contentType: ActivityJson.JsonLdContentType);

        // 202 Accepted: the server processed the activity despite the legacy content type.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // --- The accepted ld+json activity is actually processed (object stored) ----------------

    [Fact]
    public async Task Inbox_AcceptsLdJson_AndProcessesActivity()
    {
        var create = BuildCreate(NoteIri, "an ld+json note (processed)");
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync(
            $"/ap/v1/u/{Handle}/inbox",
            body,
            contentType: ActivityJson.JsonLdContentType);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The embedded Note was stored and is served by its IRI — proving the activity was not just
        // accepted but actually processed (deserialized, the Create handler ran, the object was stored).
        var objectResponse = await _http.GetAsync(new Uri(NoteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("an ld+json note (processed)", doc.RootElement.GetProperty("content").GetString());
    }

    // --- A signed Follow with Content-Type: application/ld+json is accepted ----------------

    [Fact]
    public async Task Inbox_AcceptsLdJson_FollowActivity()
    {
        // A Follow (not just a Create) with the legacy content type is also accepted.
        var follow = new Follow
        {
            Id = $"{ActorIri.Value}/follows/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(ActorIri.Value) }],
            Object = [new Link { Href = new Uri("https://remote.example.org/u/bob") }],
        };
        var body = ActivityJson.Serialize(follow);
        var response = await SendSignedPostAsync(
            $"/ap/v1/u/{Handle}/inbox",
            body,
            contentType: ActivityJson.JsonLdContentType);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Sends a signed POST request to the given path with the given body and content type. The
    /// ServerToServer signature profile covers <c>(request-target) host date digest content-type</c>,
    /// so the content type is part of the signed base.
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedPostAsync(string path, string body, string contentType)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var date = DateTime.UtcNow.ToString("R");
        var digest = Signatures.ComputeDigest(bodyBytes);
        var host = Host;
        var pathAndQuery = path;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = host,
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = contentType,
            [Signatures.DigestHeaderName] = digest,
        };

        var metadata = new HttpRequestMetadata(
            "POST",
            pathAndQuery,
            host,
            date,
            contentType,
            bodyBytes,
            headers);

        var identity = new SystemIdentity(ActorIri, _key.KeyId);
        var signature = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base + path))
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, digest);
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, date);
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        return await _http.SendAsync(request);
    }

    private static Create BuildCreate(Iri objectIri, string content) => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [content],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
            },
        ],
    };

    private static KeyPair Seed(InMemoryPersistenceProvider persistence)
    {
        var keyId = new Iri($"{ActorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);

        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        // Advertise the public key in the actor document so the server's RemoteInboundKeyResolver can
        // resolve the signing key by keyId (it fetches the actor doc and extracts publicKey).
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = ActorIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });

        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        persistence.Keys.PutKey(key);
        return key;
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence, KeyPair key)
    {
        // The server's RemoteInboundKeyResolver fetches the actor's document to resolve the signing
        // key. For a self-federation test (the actor signs as itself and posts to its own inbox), the
        // fetcher must reach the actor's own instance — i.e. this same TestServer. A LazyHandler defers
        // resolution until the first fetch (the TestServer does not exist yet during construction).
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        TestServer? self = null;
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            Fetcher = BuildSelfFetcher(factory, key, () => self!),
        });
        self = server;
        return server;
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that reaches the actor's own instance so the
    /// <c>SignatureValidationMiddleware</c> can resolve the actor's key from its own actor document.
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(
        ActivityPubClientFactory factory, KeyPair key, Func<TestServer> selfServer)
    {
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
