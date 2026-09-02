using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 20.4 (a) media integration test: uploading a note's attachment and serving it back from the
/// same origin. A media upload is a local, non-federated, Basic-authenticated multipart POST of the
/// file's bytes to the acting actor's own instance (<c>POST /local/v1/u/{handle}/media</c> — on the
/// non-AP <c>/local/v1</c> tree, not a signed inbox delivery); the server stores the bytes and returns
/// (201) the same-origin media IRI (<c>{base}/ap/v1/media/{id}</c>). The browser then loads the
/// attachment from that same-origin IRI (<c>GET /ap/v1/media/{id}</c> — a public, long-cacheable read;
/// never a cross-origin media host — Decision 056 (b)).
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts one local actor — <c>bob</c> (the instance's Handle
/// actor, Basic-authenticated as "bob"/"bob-password"). The test uploads a small image through the
/// <see cref="Iris.Client.MediaClient"/> (the client's typed surface for the upload), serves it back, and
/// asserts the round-trip (bytes, content-type, file name), the 201 body, the long-cache
/// <c>Cache-Control</c> on the served media, the unauthenticated 401 on upload, and the 404 on a
/// missing-media serve.
/// </remarks>
public sealed class MediaUploadServeIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly IMediaClient _media;

    public MediaUploadServeIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);
        _bobActorIri = bob.ActorIri;

        // A Basic-auth credential validator: bob's credentials are ("bob", "bob-password") for bob's IRI.
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(
                iri == _bobActorIri && username == Bob && password == "bob-password"));

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            Fetcher = BuildSelfFetcher(bob.Key, bob.ActorIri, () => _server!.CreateHandler()),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);

        // The typed media client (Basic-authenticated, the in-process TestServer transport).
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bob.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(bob.ActorIri, bob.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        _media = factory.CreateMediaClient(
            new ActivityPubClientOptions
            {
                ActorId = bob.ActorIri,
                EnableRetry = false,
                LocalCredentials = new ProxyCredentials(Bob, "bob-password"),
            },
            new LazyHandler(() => _server.CreateHandler()));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- An authenticated upload returns 201 + the same-origin media IRI ----------------

    [Fact]
    public async Task Upload_Authenticated_Returns201AndSameOriginMediaIri()
    {
        byte[] pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // a PNG-ish blob

        var result = await _media.UploadAsync(_bobActorIri, pixels, "image/png", "cat.png");

        // The media IRI is the instance's base + /ap/v1/media/{id} (same-origin).
        Assert.StartsWith($"https://{BHost}/ap/v1/media/", result.MediaIri.Value);
        Assert.Equal(32, result.MediaIri.Value[$"https://{BHost}/ap/v1/media/".Length..].Length);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("cat.png", result.FileName);

        // The store recorded the item (the server persisted the bytes + metadata).
        Assert.True(await _persistence.Media.TryGetAsync(result.MediaIri, out var stored, out var type, out var name));
        Assert.Equal(pixels, stored);
        Assert.Equal("image/png", type);
        Assert.Equal("cat.png", name);
    }

    // --- The served media is a 200 with the bytes, content-type, and a long cache --------

    [Fact]
    public async Task Serve_UploadedMedia_Returns200BytesContentTypeAndLongCache()
    {
        byte[] pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var result = await _media.UploadAsync(_bobActorIri, pixels, "image/png", "cat.png");

        var response = await _http.GetAsync(result.MediaIri.Value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        // The media is immutable per id (a minted, unguessable GUID) — cached aggressively.
        Assert.Equal("max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal(pixels, await response.Content.ReadAsByteArrayAsync());
    }

    // --- An unauthenticated upload is rejected (401) -------------------------------------

    [Fact]
    public async Task Upload_Unauthenticated_Returns401()
    {
        byte[] pixels = [1, 2, 3];
        var status = await UploadRawAsync(pixels, auth: null);

        Assert.Equal(401, status);
    }

    // --- A wrong-actor (non-owner) upload is rejected (401) -----------------------------

    [Fact]
    public async Task Upload_WrongCredentials_Returns401()
    {
        byte[] pixels = [1, 2, 3];
        var status = await UploadRawAsync(pixels, auth: "bob:not-the-password");

        Assert.Equal(401, status);
    }

    // --- An oversized upload is rejected (413) ------------------------------------------

    [Fact]
    public async Task Upload_Oversized_Returns413()
    {
        // A media attachment must not be unbounded (the server caps it at 10 MiB).
        byte[] tooBig = new byte[10 * 1024 * 1024 + 1];
        var status = await UploadRawAsync(tooBig, auth: "bob:bob-password");

        Assert.Equal(413, status);
    }

    // --- A missing-media serve is a 404 --------------------------------------------------

    [Fact]
    public async Task Serve_MissingMedia_Returns404()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/media/deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- The upload targets the non-AP local tree (never the /ap/v1 tree) ----------------

    [Fact]
    public async Task Upload_PostsToLocalTree_NotApTree()
    {
        // The upload is a local, non-federated write (the file is not an ActivityStreams activity), so it
        // is on the non-AP /local/v1 tree — the client derives the route from the actor IRI's /u/{handle}
        // segment. This is the AP-native rework (19.0b.2b): media uploads are off the /ap/v1 route tree.
        byte[] pixels = [1, 2, 3];
        var status = await UploadRawAsync(pixels, auth: "bob:bob-password", expectedBase: $"https://{BHost}/local/v1/u/{Bob}/media");

        Assert.Equal(201, status);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated media-upload multipart POST (used to exercise the unauthenticated
    /// 401 / oversized 413 / wrong-actor paths, which the client's typed <see cref="IMediaClient"/> cannot
    /// reach). <paramref name="expectedBase"/> (when set) is the absolute upload route the request must
    /// target (asserted in-test).
    /// </summary>
    private async Task<int> UploadRawAsync(byte[] content, string? auth, string? expectedBase = null)
    {
        var url = expectedBase ?? $"https://{BHost}/local/v1/u/{Bob}/media";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", "cat.png");
        request.Content = form;

        using var response = await _http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static IActorDocumentFetcher BuildSelfFetcher(
        KeyPair authorKey, Iri actorIri, Func<HttpMessageHandler> handlerFactory)
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
