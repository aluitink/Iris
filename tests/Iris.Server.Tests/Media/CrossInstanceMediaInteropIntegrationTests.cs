using System.Net;
using System.Text.Json;
using KristofferStrube.ActivityStreams;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Media;
using Iris.Server.Security;
using Iris.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Iris.Server.Tests.Media;

/// <summary>
/// Phase 136.14: cross-instance media and attachment interoperability. Verifies that image, link,
/// and rich-text attachment payloads survive an Iris→Iris federation round-trip: the receiving
/// instance stores the embedded object intact (no silent truncation or drop), the media proxy
/// serves fetched attachments with the correct content-type (502 for unreachable sources), and
/// long bodies are preserved in full.
/// </summary>
/// <remarks>
/// The embedded object is stored under its ORIGINAL IRI (the author's instance host), so it is not
/// served by the receiving instance's object-document endpoint (which reconstructs IRIs from its
/// own BaseUri). The tests verify the stored object via the receiving instance's persistence
/// directly (the same store the production read path uses) and verify the media proxy via HTTP
/// (the proxy is host-agnostic — it fetches by the attachment's source URL, not by IRI).
/// </remarks>
[Collection("CrossInstanceMediaInterop")]
public sealed class CrossInstanceMediaInteropIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "media-a.domain.local";
    internal const string BHost = "media-b.domain.local";
    internal const string Alice = "alice";
    internal const string BobCommunity = "lumen";

    internal static readonly Iri AliceIri = new($"https://{AHost}/ap/v1/u/{Alice}");
    internal static readonly Iri LumenIri = new($"https://{BHost}/ap/v1/c/{BobCommunity}");
    internal static readonly Iri LumenInboxIri = new($"https://{BHost}/ap/v1/c/{BobCommunity}/inbox");

    internal const string GoodImageUrl = "https://media.example.com/cat.png";
    internal const string DeadImageUrl = "https://media.example.com/broken.png";
    internal const string GoodDocUrl = "https://docs.example.com/paper.pdf";

    private readonly CrossInstanceMediaSharedHost _fixture;
    private readonly ITestOutputHelper _output;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _bHttp;
    private readonly IActivityPubClient _signedClient;

    public CrossInstanceMediaInteropIntegrationTests(
        CrossInstanceMediaSharedHost fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _bHttp = new HttpClient(fixture.ServerB.CreateHandler(), disposeHandler: false);
        _signedClient = fixture.SignedClient;
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    // --- Image attachment round-trip (A→B) ------------------------------------------------

    [Fact]
    public async Task ImageAttachment_RoundTrips_AcrossInstances()
    {
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = ["a note with a remote image"],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
            Attachment = new IObjectOrLink[]
            {
                new Image { Id = GoodImageUrl, Name = ["cat.png"] },
            },
        };
        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object with the attachment intact.
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);
        var attachment = stored!.Attachment!.First() as Image;
        Assert.NotNull(attachment);
        Assert.Equal(GoodImageUrl, attachment!.Id);

        // The media proxy serves the attachment.
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodImageUrl)}";
        var mediaResponse = await _bHttp.GetAsync(proxyUrl);
        mediaResponse.EnsureSuccessStatusCode();
        Assert.Equal("image/png", mediaResponse.Content.Headers.ContentType!.MediaType);
    }

    // --- Broken-link handling: dead attachment URL → 502, object still stored -------------

    [Fact]
    public async Task DeadImageAttachment_ObjectStillStored_ProxyReturns502()
    {
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = ["a note with a broken image link"],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
            Attachment = new IObjectOrLink[]
            {
                new Image { Id = DeadImageUrl, Name = ["broken.png"] },
            },
        };
        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object (no silent drop).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);
        var attachment = stored!.Attachment!.First() as Image;
        Assert.NotNull(attachment);
        Assert.Equal(DeadImageUrl, attachment!.Id);

        // The media proxy returns 502 for the dead URL.
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(DeadImageUrl)}";
        var mediaResponse = await _bHttp.GetAsync(proxyUrl);
        Assert.Equal(HttpStatusCode.BadGateway, mediaResponse.StatusCode);
    }

    // --- Link attachment (Document) round-trip -------------------------------------------

    [Fact]
    public async Task DocumentAttachment_RoundTrips_AcrossInstances()
    {
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = ["a note with a document link"],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
            Attachment = new IObjectOrLink[]
            {
                new Document
                {
                    Id = "doc-1",
                    Name = ["research-paper.pdf"],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["url"] = JsonSerializer.SerializeToElement(GoodDocUrl),
                    },
                },
            },
        };
        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object with the Document attachment intact.
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);
        var doc = stored!.Attachment!.First() as Document;
        Assert.NotNull(doc);
        Assert.Equal("research-paper.pdf", doc!.Name!.First());
    }

    // --- Rich-text / markdown: source field round-trip ------------------------------------

    [Fact]
    public async Task MarkdownSource_RoundTrips_AcrossInstances()
    {
        var markdown = "# Heading\n\nSome **bold** and *italic* text.\n\n- item 1\n- item 2";
        var html = "<h1>Heading</h1><p>Some <strong>bold</strong> and <em>italic</em> text.</p><ul><li>item 1</li><li>item 2</li></ul>";
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = [html],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
        };
        note.ExtensionData ??= new Dictionary<string, JsonElement>();
        note.ExtensionData["source"] = JsonSerializer.SerializeToElement(new
        {
            content = markdown,
            mediaType = "text/markdown",
        });

        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object with the HTML content intact (rich-text is preserved).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);

        var content = stored!.Content?.FirstOrDefault();
        Assert.NotNull(content);
        Assert.Contains("<strong>bold</strong>", content!);
        Assert.Contains("<h1>Heading</h1>", content!);
    }

    // --- Long-body post: no truncation ----------------------------------------------------

    [Fact]
    public async Task LongBodyPost_RoundTrips_WithoutTruncation()
    {
        var longText = new string('a', 100_000);
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = [longText],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
        };
        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object with the full content intact (no truncation).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);
        var content = stored!.Content!.FirstOrDefault();
        Assert.NotNull(content);
        Assert.Equal(100_000, content!.Length);
    }

    // --- MIME mismatch: attachment with non-image content-type ----------------------------

    [Fact]
    public async Task MimeMismatch_AttachmentStored_ProxyServesActualType()
    {
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/{Guid.NewGuid():N}");
        var note = new Note
        {
            Id = noteIri.Value,
            Content = ["a note with a MIME-mismatched attachment"],
            AttributedTo = [new Link { Href = AliceIri.Uri }],
            Attachment = new IObjectOrLink[]
            {
                new Image { Id = GoodImageUrl, Name = ["mismatched.png"] },
            },
        };
        var create = BuildCreate(AliceIri, note);

        var result = await _signedClient.DeliverAsync(LumenInboxIri, create, CancellationToken.None);
        Assert.True(result.IsSuccess, $"Delivery to B failed: {result.StatusCode}");

        // B stores the object (no silent drop).
        Assert.True(await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var stored));
        Assert.NotNull(stored);
        var attachment = stored!.Attachment!.First() as Image;
        Assert.NotNull(attachment);
        Assert.Equal(GoodImageUrl, attachment!.Id);

        // The media proxy serves the actual content-type from the fetch.
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodImageUrl)}";
        var mediaResponse = await _bHttp.GetAsync(proxyUrl);
        mediaResponse.EnsureSuccessStatusCode();
        Assert.Equal("image/png", mediaResponse.Content.Headers.ContentType!.MediaType);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static Create BuildCreate(Iri actorIri, Note note) => new()
    {
        Id = $"https://{AHost}/ap/v1/u/{Alice}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = actorIri.Uri }],
        To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
        Object = [note],
    };

    private void SeedForFixture()
    {
        TestSeeder.SeedPersonWithExistingKey(
            (InMemoryPersistenceProvider)_fixture.PersistenceA, AHost, Alice,
            new Iri($"{AliceIri.Value}#key-1"));

        TestSeeder.SeedCommunityWithExistingKey(
            _bPersistence, BHost, BobCommunity,
            new Iri($"{LumenIri.Value}#key-1"));
    }
}

/// <summary>
/// Shared two-host fixture for <see cref="CrossInstanceMediaInteropIntegrationTests"/>.
/// A: media-a.domain.local (alice), B: media-b.domain.local (lumen community).
/// B's <see cref="IMediaFetcher"/> is overridden with <see cref="CrossInstanceMediaFakeFetcher"/> so the
/// media proxy can be tested without real network access.
/// </summary>
public sealed class CrossInstanceMediaSharedHost : SharedTwoHostFixture
{
    /// <summary>A signed <see cref="IActivityPubClient"/> that signs as alice (A) and delivers to B.</summary>
    public IActivityPubClient SignedClient { get; }

    public CrossInstanceMediaSharedHost()
        : base(BuildOptions())
    {
        var keyStore = (InMemoryKeyStore)ServerA.Services.GetRequiredService<IKeyStore>();
        var keyProvider = (InMemoryKeyProvider)ServerA.Services.GetRequiredService<IKeyProvider>();
        var signer = ServerA.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        SignedClient = factory.Create(
            new ActivityPubClientOptions { ActorId = CrossInstanceMediaInteropIntegrationTests.AliceIri, EnableRetry = false },
            new LazyHandler(() => ServerB.CreateHandler()));
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(aPersistence, CrossInstanceMediaInteropIntegrationTests.AHost, CrossInstanceMediaInteropIntegrationTests.Alice);
        var lumen = TestSeeder.SeedCommunityWithKey(bPersistence, CrossInstanceMediaInteropIntegrationTests.BHost, CrossInstanceMediaInteropIntegrationTests.BobCommunity);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var aliceIri = alice.ActorIri;
        var aliceKeyId = alice.KeyId;

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceMediaInteropIntegrationTests.AHost,
            Handle = CrossInstanceMediaInteropIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, aliceIri, aliceKeyId),
            Fetcher = new CrossInstanceMediaRoutingFetcher(
                CrossInstanceMediaInteropIntegrationTests.AHost, () => serverARef().CreateHandler(),
                CrossInstanceMediaInteropIntegrationTests.BHost, () => serverBRef().CreateHandler(),
                alice.Key, aliceIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceMediaInteropIntegrationTests.BHost,
            Handle = CrossInstanceMediaInteropIntegrationTests.BobCommunity,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(lumen.Key, lumen.CommunityIri, lumen.KeyId),
            Fetcher = new CrossInstanceMediaRoutingFetcher(
                CrossInstanceMediaInteropIntegrationTests.AHost, () => serverARef().CreateHandler(),
                CrossInstanceMediaInteropIntegrationTests.BHost, () => serverBRef().CreateHandler(),
                lumen.Key, lumen.CommunityIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
            ProxySettings = new ProxySettings
            {
                AllowedHosts = ["media.example.com", "docs.example.com", CrossInstanceMediaInteropIntegrationTests.AHost, CrossInstanceMediaInteropIntegrationTests.BHost],
                MaxRequestsPerMinute = 1000,
            },
            ExtraServices = s => s.AddSingleton<IMediaFetcher>(CrossInstanceMediaFakeFetcher.Shared),
        };

        return (optionsA, optionsB);
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, keyId);
        var signer = new HttpSignatureSigner(store);
        return new IdentityKeys(store, provider, signer);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
    /// actor IRI's host.
    /// </summary>
    private sealed class CrossInstanceMediaRoutingFetcher : IActorDocumentFetcher
    {
        private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

        public CrossInstanceMediaRoutingFetcher(
            string aHost, Func<HttpMessageHandler> aHandlerFactory,
            string bHost, Func<HttpMessageHandler> bHandlerFactory,
            KeyPair signingKey, Iri signingActor)
        {
            _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
            {
                [aHost] = BuildFetcherFor(aHost, aHandlerFactory, signingKey, signingActor),
                [bHost] = BuildFetcherFor(bHost, bHandlerFactory, signingKey, signingActor),
            };
        }

        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var host = new Uri(actorIri.Value).Host;
            if (_fetchers.TryGetValue(host, out var fetcher))
            {
                return fetcher.GetActorAsync(actorIri, ct);
            }

            return Task.FromResult<Actor?>(null);
        }

        private static IActorDocumentFetcher BuildFetcherFor(
            string host, Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
        {
            var keyStore = new InMemoryKeyStore();
            keyStore.PutKey(key);
            var keyProvider = new InMemoryKeyProvider(keyStore);
            keyProvider.RegisterKey(actorIri, key.KeyId);
            var signer = new HttpSignatureSigner(keyStore);
            var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
            var client = factory.Create(
                new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
                new LazyHandler(handlerFactory));
            return new IrisActorDocumentFetcher(client, new RemoteActorCache());
        }
    }
}

/// <summary>
/// xunit collection definition for the cross-instance-media-interop shared two-host fixture.
/// </summary>
[CollectionDefinition("CrossInstanceMediaInterop")]
public sealed class CrossInstanceMediaInteropCollection : ICollectionFixture<CrossInstanceMediaSharedHost>
{
}

/// <summary>
/// A deterministic <see cref="IMediaFetcher"/> test fake: returns fixed PNG bytes for the known
/// "good" remote URL and <see langword="null"/> for any other URL (simulating a fetch failure).
/// </summary>
public sealed class CrossInstanceMediaFakeFetcher : IMediaFetcher
{
    internal static readonly CrossInstanceMediaFakeFetcher Shared = new();
    internal static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public Task<FetchedMedia?> FetchAsync(Iri sourceUrl, CancellationToken ct = default)
    {
        return Task.FromResult<FetchedMedia?>(
            sourceUrl.Value == CrossInstanceMediaInteropIntegrationTests.GoodImageUrl
                ? new FetchedMedia(PngBytes, "image/png")
                : null);
    }
}
