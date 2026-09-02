using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests.Media;

/// <summary>
/// Phase 20.4 (a) media round-trip integration test: a note's attachment is uploaded (a local,
/// Basic-authenticated multipart POST), set as the <c>url</c> of an <see cref="Image"/> attachment on a
/// note the actor authors, and served back from the same origin when the note is read. This proves the
/// full loop — <em>upload → post a note with the attachment → the note's attachment resolves to a
/// same-origin media IRI that serves the uploaded bytes</em> — so the browser loads the attachment from
/// the same origin, never a cross-origin media host (Decision 056 (b)).
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosts one local actor — <c>bob</c> (the instance's Handle
/// actor, Basic-authenticated as "bob"/"bob-password"). The test uploads a small image, authors a note
/// carrying an <see cref="Image"/> attachment whose <c>url</c> is the returned same-origin media IRI,
/// fetches the stored note back, and asserts the attachment's media IRI is same-origin, resolvable via
/// <see cref="IriExtensions.GetMediaAttachments"/>, and that serving it returns the uploaded bytes.
/// </remarks>
public sealed class MediaRoundTripIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _bobActorIri;
    private readonly IMediaClient _media;
    private readonly IActivityPubClient _client;

    public MediaRoundTripIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        var bob = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);
        _bobActorIri = bob.ActorIri;

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

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bob.Key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(bob.ActorIri, bob.Key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var options = new ActivityPubClientOptions
        {
            ActorId = bob.ActorIri,
            EnableRetry = false,
            LocalCredentials = new ProxyCredentials(Bob, "bob-password"),
        };
        _media = factory.CreateMediaClient(options, new LazyHandler(() => _server.CreateHandler()));
        _client = factory.Create(options, new LazyHandler(() => _server.CreateHandler()));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task Upload_PostNoteWithImageAttachment_ServedFromSameOrigin()
    {
        byte[] pixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // a PNG-ish blob

        // 1. Upload the attachment (a local, Basic-authenticated multipart POST) → the same-origin media IRI.
        var upload = await _media.UploadAsync(_bobActorIri, pixels, "image/png", "cat.png");
        Assert.StartsWith($"https://{BHost}/ap/v1/media/", upload.MediaIri.Value);

        // 2. Author a note carrying an Image attachment whose url is the media IRI (the same-origin path
        // the uploader set) and post it (a signed Create to the author's own outbox).
        var note = new Note
        {
            Content = ["look what I uploaded"],
            AttributedTo = [new Link { Href = _bobActorIri.Uri }],
            Attachment = [new Image
            {
                Url = [new Link { Href = upload.MediaIri.Uri }],
                Id = upload.MediaIri.Value,
                MediaType = upload.ContentType,
                Name = [upload.FileName],
            }],
        };
        var post = await _client.PostNoteAsync(_bobActorIri, note);
        Assert.True(post.IsSuccess, $"posting the note should succeed (got {(int)post.StatusCode})");

        // 3. Learn the embedded note's id from the 202 body (the server mints it, per decision 055). The
        // body is the created Create serialized as ActivityStreams JSON; deserialize it and read the
        // embedded note's id.
        var created = ActivityJson.Deserialize<Create>(post.Body);
        var noteId = created?.Object?
            .OfType<IObject>()
            .Select(o => o.Id)
            .FirstOrDefault(i => !string.IsNullOrEmpty(i));
        Assert.False(string.IsNullOrEmpty(noteId));

        // 4. Fetch the stored note back and assert its Image attachment resolves to the same-origin media
        // IRI (via the single boundary read GetMediaAttachments).
        var stored = await _client.GetObjectAsync(new Iri(noteId!));
        Assert.NotNull(stored);
        var mediaAttachments = stored!.GetMediaAttachments();
        var mediaIris = mediaAttachments.Select(m => m.Iri).ToArray();
        Assert.Contains(upload.MediaIri, mediaIris);

        // The media IRI is same-origin (the instance's /ap/v1/media/{id}, not a cross-origin media host).
        Assert.StartsWith($"https://{BHost}/ap/v1/media/", mediaIris.First(i => i == upload.MediaIri).Value);

        // 5. Serving the media IRI returns the uploaded bytes (the <img> the object view would load).
        var served = await _http.GetAsync(upload.MediaIri.Value);
        Assert.Equal(System.Net.HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(pixels, await served.Content.ReadAsByteArrayAsync());
    }

    // --- helpers ------------------------------------------------------------------------

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
