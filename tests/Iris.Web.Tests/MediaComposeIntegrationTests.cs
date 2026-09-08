using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 44.3 — <em>media attachment (image) on compose (F-27)</em>. They boot the
/// real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/>
/// and verify that: (1) the media upload endpoint (<c>POST /local/v1/u/{handle}/media</c>) stores the bytes
/// and returns the same-origin media IRI, (2) a note built via <see cref="ComposeNote.Build"/> with a media
/// attachment round-trips the <c>Image</c> attachment through the signed outbox pipeline to the object
/// store, and (3) the public object document carries the attachment (the data the feed's <c>ObjectView</c>
/// renders via <c>GetMediaAttachments</c>).
/// </summary>
public sealed class MediaComposeIntegrationTests : IDisposable
{
    private const string Base = "https://media.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public MediaComposeIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

        services.AddSingleton<IActorDocumentFetcher>(sp =>
            new LocalActorDocumentFetcher(sp.GetRequiredService<IPersistenceProvider>()));

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseStaticFiles();
                webApp.UseEndpoints(endpoints =>
                {
                    WebAppFactory.MapAuthEndpoints(endpoints);
                    endpoints.MapActivityPubEndpoints();
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;

        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _actorIri = new Iri($"{Base}/ap/v1/u/alice");

        var keyId = new Iri($"{_actorIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(keyId, out var existing) || existing is null)
        {
            throw new InvalidOperationException("Seeded actor key not found.");
        }
        _actorKey = (KeyPair)existing;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task MediaUpload_ReturnsSameOriginIri_AndStoresBytes()
    {
        var mediaClient = BuildMediaClient(_actorIri, "alice", "alice");

        // A tiny 1x1 transparent PNG.
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var result = await mediaClient.UploadAsync(_actorIri, bytes, "image/png", "pixel.png");

        // The media IRI is same-origin under /ap/v1/media/{id}.
        Assert.StartsWith($"{Base}/ap/v1/media/", result.MediaIri.Value);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("pixel.png", result.FileName);

        // The bytes are stored and retrievable by the same IRI.
        var persistence = GetPersistence();
        Assert.True(await persistence.Media.TryGetAsync(
            result.MediaIri, out var stored, out var storedType, out _, CancellationToken.None));
        Assert.Equal(bytes, stored);
        Assert.Equal("image/png", storedType);
    }

    [Fact]
    public async Task NoteWithMedia_RoundTripsImageAttachment()
    {
        var media = await UploadAsync();
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(
            _actorIri,
            "Look at this picture",
            to: [Public],
            mediaIri: media.MediaIri,
            mediaType: media.ContentType,
            mediaName: media.FileName);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, obj) = await FindCreatedNoteAsync("Look at this picture");
        Assert.NotNull(obj);
        // The feed's single read boundary resolves the attachment to the media IRI + file name.
        var (iri, name) = obj!.GetMediaAttachments().Single();
        Assert.Equal(media.MediaIri, iri);
        Assert.Equal("pixel.png", name);
    }

    [Fact]
    public async Task NoteWithoutMedia_HasNoAttachment()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "A text-only note", to: [Public]);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (_, obj) = await FindCreatedNoteAsync("A text-only note");
        Assert.NotNull(obj);
        Assert.Empty(obj!.GetMediaAttachments());
    }

    [Fact]
    public async Task NoteWithMedia_AppearsInPublicDocument()
    {
        var media = await UploadAsync();
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(
            _actorIri,
            "Wire media check",
            to: [Public],
            mediaIri: media.MediaIri,
            mediaType: media.ContentType,
            mediaName: media.FileName);

        var result = await client.PostNoteAsync(_actorIri, note);
        Assert.True(result.IsSuccess, $"Post should succeed, got HTTP {(int)result.StatusCode}: {result.Body}");

        var (objectIri, _) = await FindCreatedNoteAsync("Wire media check");
        Assert.NotNull(objectIri);

        var http = _server.CreateClient();
        var response = await http.GetAsync(objectIri!.ToString());
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        // The public document carries the Image attachment with the same-origin media url + mediaType.
        Assert.Contains(media.MediaIri.Value, json);
        Assert.Contains("\"mediaType\":\"image/png\"", json);
        Assert.Contains("pixel.png", json);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    /// <summary>Uploads a tiny 1x1 PNG via the media client and returns the result.</summary>
    private async Task<MediaUploadResult> UploadAsync(string fileName = "pixel.png")
    {
        var mediaClient = BuildMediaClient(_actorIri, "alice", "alice");
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        return await mediaClient.UploadAsync(_actorIri, bytes, "image/png", fileName);
    }

    /// <summary>
    /// Scans the actor's outbox for the <c>Create</c> whose embedded object's content contains
    /// <paramref name="contentFragment"/>, and returns the stored object (looked up by its IRI) plus the
    /// object IRI.
    /// </summary>
    private async Task<(Iri? ObjectIri, IObject? Object)> FindCreatedNoteAsync(string contentFragment)
    {
        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);

        foreach (var item in outbox)
        {
            if (item is not KristofferStrube.ActivityStreams.Create create
                || create.Object is not { } objects)
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as IObject;
            if (embedded is null || embedded.Content is null)
            {
                continue;
            }

            var content = string.Join(" ", embedded.Content);
            if (!content.Contains(contentFragment, StringComparison.Ordinal))
            {
                continue;
            }

            Iri? objectIri = embedded.Id is { Length: > 0 } id ? new Iri(id) : null;
            IObject? stored = null;
            if (objectIri is { } resolvedIri)
            {
                if (await persistence.Objects.TryGetObjectAsync(resolvedIri, out var foundObj) && foundObj is not null)
                {
                    stored = foundObj;
                }
            }

            return (objectIri, stored ?? embedded);
        }

        throw new InvalidOperationException($"No created note containing '{contentFragment}' found in the outbox.");
    }

    private IActivityPubClient BuildSignedClient(Iri actorIri, KeyPair key)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => _server.CreateHandler()));
    }

    private IMediaClient BuildMediaClient(Iri actorIri, string user, string pass)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_actorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, _actorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.CreateMediaClient(
            new ActivityPubClientOptions
            {
                ActorId = actorIri,
                EnableRetry = false,
                LocalCredentials = new ProxyCredentials(user, pass),
            },
            new LazyHandler(() => _server.CreateHandler()));
    }

    private sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence)
        : IActorDocumentFetcher
    {
        public async Task<KristofferStrube.ActivityStreams.Actor?> GetActorAsync(
            Iri actorIri, CancellationToken ct = default)
        {
            if (await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) && actor is not null)
            {
                return actor;
            }

            if (actorIri.Value.Contains("/c/"))
            {
                if (await persistence.Communities.TryGetCommunityAsync(actorIri, out var community, ct)
                    && community is not null)
                {
                    return community;
                }
            }

            return null;
        }
    }
}
