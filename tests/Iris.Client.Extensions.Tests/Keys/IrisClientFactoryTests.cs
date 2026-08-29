using System.Net;
using System.Net.Http;
using Iris.Client;
using Iris.Core;
using SessionKeyStoreProvider = Iris.Client.Extensions.Keys.SessionKeyStoreProvider;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Extensions.Tests.Keys;

/// <summary>
/// Unit tests for <see cref="IrisClientFactory"/> and <see cref="IrisClientBuilder"/>: option
/// mapping into the client pipeline, the proxy-credentials invariant, the builder's seam wiring,
/// and dispose semantics.
/// </summary>
public sealed class IrisClientFactoryTests
{
    private const string Actor = "https://a.domain.local/ap/v1/u/alice";
    private const string ActorKey = "https://a.domain.local/ap/v1/u/alice#key-1";

    // --- Option mapping ---------------------------------------------------------------

    [Fact]
    public void Create_MapsOptions_NoProxy()
    {
        var (factory, recorder) = Build(useProxyFallback: false, enableRetry: true, maxAttempts: 7, timeout: TimeSpan.FromSeconds(11));

        factory.Create(new Iri(Actor));

        var seen = recorder.Seen.Single();
        Assert.Equal(new Iri(Actor), seen.ActorId!.Value);
        Assert.True(seen.EnableRetry);
        Assert.Equal(7, seen.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(11), seen.HttpClientTimeout);
        Assert.Null(seen.ProxyBaseUrl);
        Assert.Null(seen.ProxyCredentials);
    }

    [Fact]
    public void Create_ProxyFallbackEnabled_SetsProxyBaseAndCredentials()
    {
        var (factory, recorder) = Build(useProxyFallback: true, proxyCreds: new ProxyCredentials("alice", "pw"));

        factory.Create(new Iri(Actor));

        var seen = recorder.Seen.Single();
        Assert.NotNull(seen.ProxyBaseUrl);
        Assert.Equal(new Iri("http://localhost"), seen.ProxyBaseUrl!.Value); // the default ServerBaseUri
        Assert.NotNull(seen.ProxyCredentials);
        Assert.Equal("alice", seen.ProxyCredentials!.Username);
    }

    [Fact]
    public void Create_ProxyFallbackEnabled_WithoutCredentials_Throws()
    {
        var (factory, _) = Build(useProxyFallback: true, proxyCreds: null);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(new Iri(Actor)));
        Assert.Contains("ProxyCredentials", ex.Message);
    }

    [Fact]
    public void Create_ReturnsUsableClient()
    {
        var (factory, _) = Build(useProxyFallback: false);

        using var client = factory.Create(new Iri(Actor));

        Assert.NotNull(client);
    }

    // --- Builder ----------------------------------------------------------------------

    [Fact]
    public void Builder_WithoutAuthenticator_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            IrisClientBuilder.Create(new IrisClientOptions()).Build());
        Assert.Contains("IClientAuthenticator", ex.Message);
    }

    [Fact]
    public void Builder_Builds_Bundle_ExposesSeams()
    {
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .Build();

        Assert.NotNull(bundle.Session);
        Assert.NotNull(bundle.ClientFactory);
        Assert.IsType<InMemoryKeyStore>(bundle.KeyStore);
        Assert.IsType<InMemoryKeyProvider>(bundle.KeyProvider);
        // The session and the client factory share one key store: the session puts the key, the
        // factory's signer reads it.
        Assert.Same(bundle.KeyStore, bundle.Session.KeyStore);
    }

    [Fact]
    public void Builder_WithKeyStore_UsesProvidedStore()
    {
        var store = new InMemoryKeyStore();
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .WithKeyStore(store)
            .Build();

        Assert.Same(store, bundle.KeyStore);
        Assert.Same(store, bundle.Session.KeyStore);
    }

    [Fact]
    public void Builder_CreateClient_BuildsWorkingClient()
    {
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions { UseProxyFallback = false })
            .WithAuthenticator(new FakeAuthenticator())
            .Build();

        using var client = bundle.CreateClient(new Iri(Actor));
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Builder_Dispose_RemovesRegisteredKey()
    {
        // Seed a key for the actor so the session's LoginAsync (via the fake authenticator) can
        // register a real identity, then confirm dispose removes it.
        var store = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateEcP256(new Iri(ActorKey));
        store.PutKey(key);

        var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator(ActorDoc(Actor), key))
            .WithKeyStore(store)
            .Build();

        await bundle.Session.LoginAsync(new Iri(Actor));
        Assert.True(store.TryGetKey(new Iri(ActorKey), out _));

        bundle.Dispose();
        Assert.False(store.TryGetKey(new Iri(ActorKey), out _));
    }

    // --- Discovery (J-21): the bundle exposes a public handle→IRI path ----------------

    [Fact]
    public void Builder_Builds_Bundle_ExposesDiscovery()
    {
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .Build();

        Assert.NotNull(bundle.Discovery);
        // The default discovery service is the WebFinger-backed one.
        Assert.IsType<WebFingerDiscoveryService>(bundle.Discovery);
    }

    [Fact]
    public void Builder_WithDiscovery_UsesProvidedService()
    {
        var discovery = new RecordingDiscovery();
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .WithDiscovery(discovery)
            .Build();

        Assert.Same(discovery, bundle.Discovery);
    }

    [Fact]
    public async Task Bundle_ResolveActorAsync_DelegatesToDiscovery()
    {
        var discovery = new RecordingDiscovery(result: new Iri(Actor));
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .WithDiscovery(discovery)
            .Build();

        var resolved = await bundle.ResolveActorAsync("@alice@a.domain.local");

        Assert.Equal(new Iri(Actor), resolved);
        // Both the convenience method and the Discovery property route to the same service.
        Assert.Equal("@alice@a.domain.local", discovery.LastAccount);
        Assert.Same(bundle.Discovery, discovery);
    }

    [Fact]
    public async Task Bundle_ResolveActorAsync_UnknownHandle_ReturnsNull()
    {
        var discovery = new RecordingDiscovery(result: null);
        using var bundle = IrisClientBuilder.Create(new IrisClientOptions())
            .WithAuthenticator(new FakeAuthenticator())
            .WithDiscovery(discovery)
            .Build();

        Assert.Null(await bundle.ResolveActorAsync("@nobody@nowhere.local"));
    }

    // --- Helpers ----------------------------------------------------------------------

    private static (IrisClientFactory, RecordingClientFactory) Build(
        bool useProxyFallback,
        ProxyCredentials? proxyCreds = null,
        bool enableRetry = true,
        int maxAttempts = 3,
        TimeSpan? timeout = null)
    {
        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri("http://localhost"),
            UseProxyFallback = useProxyFallback,
            ProxyCredentials = proxyCreds,
            EnableRetry = enableRetry,
            MaxRetryAttempts = maxAttempts,
            HttpClientTimeout = timeout,
        };
        var store = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(store);
        var recorder = new RecordingClientFactory();
        var factory = new IrisClientFactory(recorder, provider, new SessionKeyStoreProvider(store), options);
        return (factory, recorder);
    }

    private static Actor ActorDoc(string id) => new()
    {
        Id = id,
        PreferredUsername = id[(id.LastIndexOf('/') + 1)..],
    };

    /// <summary>
    /// A fake <see cref="IActivityPubClientFactory"/> that records the
    /// <see cref="ActivityPubClientOptions"/> it is asked to create and returns a real (minimal)
    /// client so <see cref="Create_ReturnsUsableClient"/> has something concrete to hold.
    /// </summary>
    private sealed class RecordingClientFactory : IActivityPubClientFactory
    {
        public List<ActivityPubClientOptions> Seen { get; } = [];

        public IActivityPubClient Create(ActivityPubClientOptions options, HttpMessageHandler httpHandler)
        {
            Seen.Add(options);
            // A minimal client over the supplied transport is enough to prove the factory returns
            // a usable instance without exercising the real signing pipeline (covered by the
            // integration test).
            return new ActivityPubClient(new HttpClient(httpHandler, disposeHandler: false), null, null);
        }
    }

    private sealed class FakeAuthenticator : IClientAuthenticator
    {
        private readonly AuthenticatedActor? _result;

        public FakeAuthenticator()
        {
        }

        public FakeAuthenticator(Actor actor, KeyPair key)
        {
            _result = new AuthenticatedActor(actor, key);
        }

        public Task<AuthenticatedActor?> AuthenticateAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    /// <summary>
    /// A fake <see cref="IDiscoveryService"/> that returns a fixed result and records the account it
    /// was asked to resolve.
    /// </summary>
    private sealed class RecordingDiscovery : IDiscoveryService
    {
        private readonly Iri? _result;

        public RecordingDiscovery(Iri? result = null)
        {
            _result = result;
        }

        public string? LastAccount { get; private set; }

        public Task<Iri?> ResolveActorAsync(string account, CancellationToken ct = default)
        {
            LastAccount = account;
            return Task.FromResult(_result);
        }
    }
}
