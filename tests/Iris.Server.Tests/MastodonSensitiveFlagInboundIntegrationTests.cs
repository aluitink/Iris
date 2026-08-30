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
/// Phase 13.4 — Mastodon <c>sensitive</c> flag inbound handling. <c>sensitive</c> is a boolean flag
/// Mastodon sets on objects (most commonly media — <c>Image</c>/<c>Video</c> — but also <c>Note</c>) to
/// indicate the content is not safe for work. It is not in the ActivityStreams 2.0 vocabulary the
/// library models (no <c>Sensitive</c> property on any object type), so it lands in
/// <c>ExtensionData</c> and is forwarded opaquely. This test proves the guarantee end-to-end over the
/// real signed inbox pipeline: a signed <see cref="Create"/> carrying an object with <c>sensitive:
/// true</c> is accepted, the object is stored, and it is served back with the <c>sensitive</c> flag
/// preserved verbatim.
/// </summary>
public sealed class MastodonSensitiveFlagInboundIntegrationTests : IDisposable
{
    private const string Host = "sensitive.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _key;
    private readonly string _base = $"https://{Host}";

    public MastodonSensitiveFlagInboundIntegrationTests()
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

    // --- A signed Create carrying a Note with `sensitive: true` is accepted and preserved ----

    [Fact]
    public async Task Inbox_AcceptsSensitiveNote_AndServesVerbatim()
    {
        var noteIri = new Iri($"{ActorIri.Value}/notes/1");
        var create = BuildCreate(noteIri, sensitive: true);
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body);

        // 202 Accepted: the server processed the Create.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The embedded Note is stored and served by its IRI, with the `sensitive` flag preserved
        // verbatim.
        var objectResponse = await _http.GetAsync(new Uri(noteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());

        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(noteIri.Value, doc.RootElement.GetProperty("id").GetString());
        // The `sensitive` boolean is forwarded as an opaque property (ExtensionData).
        Assert.True(doc.RootElement.GetProperty("sensitive").GetBoolean());
    }

    // --- A signed Create carrying a Note with `sensitive: false` is accepted and preserved ----

    [Fact]
    public async Task Inbox_AcceptsNonSensitiveNote_AndServesVerbatim()
    {
        var noteIri = new Iri($"{ActorIri.Value}/notes/2");
        var create = BuildCreate(noteIri, sensitive: false);
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var objectResponse = await _http.GetAsync(new Uri(noteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());

        // The `sensitive: false` is also preserved verbatim (not dropped or coerced).
        Assert.False(doc.RootElement.GetProperty("sensitive").GetBoolean());
    }

    // --- `sensitive` on an embedded Image (the realistic Mastodon media shape) ----------------

    [Fact]
    public async Task Inbox_AcceptsSensitiveImage_AndServesVerbatim()
    {
        // Mastodon most commonly marks media (Image/Video) as sensitive. The Image is embedded in the
        // Note's `image` property. The `sensitive` flag on the Image rides in ExtensionData.
        var noteIri = new Iri($"{ActorIri.Value}/notes/3");
        var imageIri = new Iri($"{ActorIri.Value}/media/1");
        var create = BuildCreateWithSensitiveImage(noteIri, imageIri);
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var objectResponse = await _http.GetAsync(new Uri(noteIri.Value).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await objectResponse.Content.ReadAsStringAsync());

        // The embedded Image is preserved, with its `sensitive` flag.
        var image = doc.RootElement.GetProperty("image");
        Assert.Equal(imageIri.Value, image.GetProperty("id").GetString());
        Assert.True(image.GetProperty("sensitive").GetBoolean());
    }

    // --- Helpers ------------------------------------------------------------------------

    private Create BuildCreate(Iri objectIri, bool sensitive) => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = objectIri.Value,
                Content = [sensitive ? "NSFW content" : "Safe content"],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["sensitive"] = JsonSerializer.SerializeToElement(sensitive),
                },
            },
        ],
    };

    private Create BuildCreateWithSensitiveImage(Iri noteIri, Iri imageIri) => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = noteIri.Value,
                Content = ["A post with sensitive media"],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
                    Image = [new Image
                    {
                        Id = imageIri.Value,
                        ExtensionData = new Dictionary<string, JsonElement>
                        {
                            ["sensitive"] = JsonSerializer.SerializeToElement(true),
                        },
                    }],
            },
        ],
    };

    /// <summary>
    /// Sends a signed POST to the given path with the given body, using the ServerToServer signature
    /// profile (which covers <c>content-type</c>).
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedPostAsync(string path, string body)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var date = DateTime.UtcNow.ToString("R");
        var digest = Signatures.ComputeDigest(bodyBytes);
        var contentType = ActivityJson.ActivityJsonContentType;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = Host,
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = contentType,
            [Signatures.DigestHeaderName] = digest,
        };

        var metadata = new HttpRequestMetadata("POST", path, Host, date, contentType, bodyBytes, headers);
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
            Fetcher = BuildSelfFetcher(factory, () => self!),
        });
        self = server;
        return server;
    }

    private static IActorDocumentFetcher BuildSelfFetcher(ActivityPubClientFactory factory, Func<TestServer> selfServer)
    {
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
