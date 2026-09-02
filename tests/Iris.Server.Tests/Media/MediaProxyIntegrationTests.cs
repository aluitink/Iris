using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Media;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 20.4 (d) media-proxy integration test: the browser-loadable external-media route
/// (<c>GET /ap/v1/media/proxy?url={originator-url}</c>, Decision 057). A cross-origin attachment
/// <c>url</c> is fetched once by the server (an unsigned outbound GET, via the <see cref="IMediaFetcher"/>
/// seam), stored (keyed by the URL + a server-internal content-hash dedupe), and served back from the
/// same origin, long-cacheable — so the browser never loads a cross-origin media host. A cache hit
/// (the URL was already stored) serves straight from the store with no outbound fetch. A fetch failure
/// (a dead or unreachable URL) is a <c>502 Bad Gateway</c> (the client's <c>&lt;img onerror&gt;</c> falls
/// back to a link-out). A missing or invalid <c>url</c> parameter is a <c>400</c>.
/// </summary>
/// <remarks>
/// Topology: a single instance (b.domain.local) hosting one local actor — <c>bob</c>. The
/// <see cref="IMediaFetcher"/> is a test fake (registered via the host factory's <c>ExtraServices</c>)
/// so the outbound fetch is deterministic: it returns fixed bytes for a known "good" remote URL and
/// <see langword="null"/> for a known "dead" URL, counting its calls so the cache-hit test can assert
/// no second fetch. The proxy route and the store are exercised end-to-end over the real in-process
/// <c>TestServer</c>.
/// </remarks>
public sealed class MediaProxyIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";

    private const string GoodRemoteUrl = "https://cdn.example.com/images/cat.png";
    private const string DeadRemoteUrl = "https://cdn.example.com/images/dead.png";
    private static readonly byte[] GoodPixels = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly FakeMediaFetcher _fetcher;
    private readonly KeyPair _bobKey;
    private readonly Iri _bobActorIri;
    private readonly Iri _bobKeyId;

    public MediaProxyIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        (_bobKey, _bobActorIri, _bobKeyId) = TestSeeder.SeedPersonWithKey(_persistence, BHost, Bob);

        _fetcher = new FakeMediaFetcher();

        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(username == Bob && password == "bob-password"));

        // A self-fetcher: resolves bob's public key from bob's own actor document (served by the same
        // in-process TestServer), so the signature-validation middleware can validate an inbound Create
        // signed as bob. The handler is lazy (the TestServer is created after the fetcher is wired).
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            IdentityKeys = BuildIdentityKeys(),
            Fetcher = BuildSelfFetcher(() => _server!.CreateHandler()),
            ExtraServices = s => s.AddSingleton<IMediaFetcher>(_fetcher),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    /// <summary>
    /// Builds a self-fetcher (bob's key + actor IRI + a lazy handler to the host's own TestServer) so
    /// the signature-validation middleware resolves bob's public key from bob's own actor document.
    /// </summary>
    private IActorDocumentFetcher BuildSelfFetcher(Func<HttpMessageHandler> handlerFactory)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_bobActorIri, _bobKeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = _bobActorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    /// <summary>
    /// Builds a test signing identity (key store + provider + signer) over bob's seeded key, so a host
    /// can sign an inbound Create as bob (the "local post" path).
    /// </summary>
    private IdentityKeys BuildIdentityKeys()
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_bobActorIri, _bobKeyId);
        var signer = new HttpSignatureSigner(keyStore);
        return new IdentityKeys(keyStore, keyProvider, signer);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A cross-origin url is fetched once, stored, and served from the same origin --------

    [Fact]
    public async Task Proxy_GoodUrl_FetchesStoresAndServesSameOrigin()
    {
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodRemoteUrl)}";

        var response = await _http.GetAsync(proxyUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        // Long-cacheable, immutable (the bytes are content-stable per source URL).
        Assert.Equal("max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal(GoodPixels, await response.Content.ReadAsByteArrayAsync());

        // The fetcher was called exactly once (the proxy fetched the remote URL once).
        Assert.Equal(1, _fetcher.CallCount);

        // The store recorded the source URL → media IRI mapping (the cache-hit key).
        var found = await _persistence.Media.TryGetMediaIriBySourceUrlAsync(new Iri(GoodRemoteUrl), out var mediaIri);
        Assert.True(found);
        var storedIri = mediaIri!.Value; // the Iri (Nullable<Iri>.Value)
        Assert.StartsWith($"https://{BHost}/ap/v1/media/", storedIri.Value);
    }

    // --- A second request for the same url is a cache hit (no second fetch) -----------------

    [Fact]
    public async Task Proxy_SameUrlTwice_SecondIsCacheHitNoRefetch()
    {
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodRemoteUrl)}";

        var first = await _http.GetAsync(proxyUrl);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, _fetcher.CallCount);

        var second = await _http.GetAsync(proxyUrl);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("image/png", second.Content.Headers.ContentType!.MediaType);
        Assert.Equal("max-age=31536000, immutable", second.Headers.CacheControl!.ToString());
        Assert.Equal(GoodPixels, await second.Content.ReadAsByteArrayAsync());

        // The cache hit served from the store — no second outbound fetch.
        Assert.Equal(1, _fetcher.CallCount);
    }

    // --- A dead url (fetch failure) is a 502 (the client falls back to a link-out) ----------

    [Fact]
    public async Task Proxy_DeadUrl_Returns502()
    {
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(DeadRemoteUrl)}";

        var response = await _http.GetAsync(proxyUrl);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // Nothing was stored for the dead URL.
        var found = await _persistence.Media.TryGetMediaIriBySourceUrlAsync(new Iri(DeadRemoteUrl), out _);
        Assert.False(found);
    }

    // --- A missing url parameter is a 400 ----------------------------------------------------

    [Fact]
    public async Task Proxy_MissingUrl_Returns400()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/media/proxy");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- A relative (non-absolute) url is a 400 (the proxy requires an absolute remote URL) --

    [Fact]
    public async Task Proxy_RelativeUrl_Returns400()
    {
        var response = await _http.GetAsync($"https://{BHost}/ap/v1/media/proxy?url=/local/cat.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- The proxy is public (no Basic auth) — the browser's <img> loads it unauthenticated --

    [Fact]
    public async Task Proxy_Public_NoAuthRequired()
    {
        // A fresh, unauthenticated HttpClient (no credentials) loads the proxied media.
        using var anonymous = new HttpClient(_server.CreateHandler(), disposeHandler: false);
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodRemoteUrl)}";

        var response = await anonymous.GetAsync(proxyUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Eager-warm: an inbound Create with a cross-origin attachment is pre-fetched --------

    [Fact]
    public async Task InboundCreate_CrossOriginAttachment_EagerWarmsAndProxyIsCacheHit()
    {
        // An inbound Create (a local post) embedding a note with a cross-origin Image attachment. The
        // CreateActivityHandler stores the note AND eager-warms the attachment (fetches it once via the
        // media fetcher into the media store, keyed by the source URL), so the media proxy later serves
        // it instantly (a cache hit, no second fetch).
        var create = BuildCreateWithCrossOriginAttachment();

        var result = await PostToInboxAsync(_server, _bobActorIri, create);
        Assert.Equal(202, result.StatusCode);

        // The eager-warm fetched the cross-origin URL once (the warm, not the proxy).
        Assert.Equal(1, _fetcher.CallCount);

        // The store has the source URL → media IRI mapping (warmed, not proxy-fetched).
        var found = await _persistence.Media.TryGetMediaIriBySourceUrlAsync(new Iri(GoodRemoteUrl), out var mediaIri);
        Assert.True(found);
        Assert.NotNull(mediaIri);

        // The proxy now serves it as a cache hit (no second outbound fetch).
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodRemoteUrl)}";
        var response = await _http.GetAsync(proxyUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GoodPixels, await response.Content.ReadAsByteArrayAsync());
        // Still exactly one fetch (the warm); the proxy was a cache hit.
        Assert.Equal(1, _fetcher.CallCount);
    }

    // --- Eager-warm disabled: an inbound Create does not pre-fetch (the proxy fetches lazily) -

    [Fact]
    public async Task InboundCreate_EagerWarmDisabled_ProxyFetchesLazily()
    {
        // A host with eager-warm disabled: the inbound Create stores the note but does NOT warm the
        // attachment (no fetch). The proxy then fetches it lazily on the first hit.
        var server = CreateHostWithEagerWarmDisabled();
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);
        var fetcher = (FakeMediaFetcher)server.Services.GetRequiredService<IMediaFetcher>();

        var create = BuildCreateWithCrossOriginAttachment();
        var result = await PostToInboxAsync(server, _bobActorIri, create);
        Assert.Equal(202, result.StatusCode);

        // No eager-warm fetch happened.
        Assert.Equal(0, fetcher.CallCount);
        var found = await _persistence.Media.TryGetMediaIriBySourceUrlAsync(new Iri(GoodRemoteUrl), out _);
        Assert.False(found);

        // The proxy fetches it lazily on the first hit.
        var proxyUrl = $"https://{BHost}/ap/v1/media/proxy?url={Uri.EscapeDataString(GoodRemoteUrl)}";
        var response = await http.GetAsync(proxyUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fetcher.CallCount);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="Create"/> embedding a note whose single <see cref="Image"/> attachment has a
    /// cross-origin <c>id</c> (the <see cref="GoodRemoteUrl"/>), exercising the eager-warm path (a
    /// cross-origin attachment the instance must pre-fetch).
    /// </summary>
    private static Create BuildCreateWithCrossOriginAttachment()
    {
        var note = new Note
        {
            Id = $"https://{BHost}/ap/v1/u/{Bob}/notes/{Guid.NewGuid():N}",
            Content = ["a note with a remote image"],
            Attachment = new IObjectOrLink[]
            {
                new Image { Id = GoodRemoteUrl, Name = ["cat.png"] },
            },
        };

        return new Create
        {
            Id = $"https://{BHost}/ap/v1/u/{Bob}/creates/{Guid.NewGuid():N}",
            Actor = new IObjectOrLink[] { new Link { Href = new Uri($"https://{BHost}/ap/v1/u/{Bob}") } },
            To = new IObjectOrLink[] { new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") } },
            Object = new IObjectOrLink[] { note },
        };
    }

    /// <summary>
    /// Posts <paramref name="activity"/> to the actor's inbox via a client that signs as the actor
    /// (the "local post" path), routed to the in-process <paramref name="server"/>. Returns the HTTP
    /// result (202 Accepted when the full inbound pipeline — signature validation + handler + warm — ran).
    /// </summary>
    private static async Task<DeliveryResult> PostToInboxAsync(TestServer server, Iri actorIri, Activity activity)
    {
        var keyStore = server.Services.GetRequiredService<IKeyStore>();
        var keyProvider = server.Services.GetRequiredService<IKeyProvider>();
        var signer = server.Services.GetRequiredService<ISignatureSigner>();
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            server.CreateHandler());
        return await client.DeliverAsync(actorIri.InboxOf(), activity);
    }

    /// <summary>
    /// Starts a second host (same actor, same persistence) with eager-warm disabled
    /// (<see cref="MediaOptions.EagerWarm"/> = <see langword="false"/>), to exercise the lazy-fetch path.
    /// The fake <see cref="IMediaFetcher"/> is re-registered so its call count is tracked.
    /// </summary>
    private TestServer CreateHostWithEagerWarmDisabled()
    {
        var credentialValidator = new BasicAuthCredentialValidator((iri, username, password) =>
            ValueTask.FromResult(username == Bob && password == "bob-password"));

        return ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _persistence,
            CredentialValidator = credentialValidator,
            IdentityKeys = BuildIdentityKeys(),
            Fetcher = BuildSelfFetcher(() => _server!.CreateHandler()),
            ExtraServices = s =>
            {
                s.AddSingleton<IMediaFetcher>(_fetcher);
                s.AddSingleton<IMediaWarmer, DisabledEagerWarmWarmer>();
            },
        });
    }

    /// <summary>
    /// A no-op <see cref="IMediaWarmer"/> (simulates <c>EagerWarm = false</c>): never fetches, so the
    /// inbound store path does not pre-fetch attachments (the proxy fetches lazily on the first hit).
    /// </summary>
    private sealed class DisabledEagerWarmWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// A deterministic <see cref="IMediaFetcher"/> test fake: returns fixed bytes for the known "good"
    /// remote URL and <see langword="null"/> for the known "dead" URL (simulating a fetch failure),
    /// counting its calls so the cache-hit test can assert no second fetch.
    /// </summary>
    private sealed class FakeMediaFetcher : IMediaFetcher
    {
        public int CallCount { get; private set; }

        public Task<FetchedMedia?> FetchAsync(Iri sourceUrl, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<FetchedMedia?>(sourceUrl.Value == GoodRemoteUrl
                ? new FetchedMedia(GoodPixels, "image/png")
                : null);
        }
    }
}
