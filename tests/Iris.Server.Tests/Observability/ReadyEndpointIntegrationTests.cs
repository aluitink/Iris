using System.Net;
using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Observability;

/// <summary>
/// Phase 30.2 integration tests for the <c>GET /ap/v1/ready</c> readiness probe: a freshly-started
/// instance that has not yet loaded its signing key material is not ready (503, <c>ready: false</c>);
/// once the instance actor's key is registered + resolvable it is ready (200, <c>ready: true</c>); and a
/// host that binds its own <see cref="IReadinessGate"/> controls the report (a custom always-ready gate
/// yields 200 on a key-less instance; a custom never-ready gate yields 503 even on a fully-keyed one).
/// The probe is public (no ActivityPub signature) so a load balancer / orchestrator can reach it, and is
/// distinct from <c>GET /ap/v1/health</c> (liveness): an instance can be up but not yet ready.
/// </summary>
public sealed class ReadyEndpointIntegrationTests
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Base = $"https://{AHost}";

    [Fact]
    public async Task Ready_NotLoaded_Returns503NotReady()
    {
        var (server, _, _) = StartKeyless();
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        HttpResponseMessage response = await http.GetAsync($"{Base}/ap/v1/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Ready_AfterKeyLoaded_Returns200Ready()
    {
        var (server, keyStore, keyProvider) = StartKeyless();
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        // Before the key is loaded: not ready.
        var before = await http.GetAsync($"{Base}/ap/v1/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        // Load the instance actor's signing key (the host's IKeyStore + IKeyProvider).
        var actorIri = new Iri($"{Base}/ap/v1/u/{Alice}");
        var keyId = new Iri($"{actorIri}#key-1");
        keyStore.PutKey(KeyPairGenerator.GenerateRsa(keyId));
        keyProvider.RegisterKey(actorIri, keyId);

        // After the key is loaded: ready.
        var after = await http.GetAsync($"{Base}/ap/v1/ready");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Ready_CustomGateAlwaysReady_Returns200OnKeylessInstance()
    {
        // A host that loads keys asynchronously can bind a readiness gate that knows when its load
        // completes; a gate that reports ready yields 200 even before the default key is registered.
        var (server, _, _) = StartWithGate(ready: true);
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        HttpResponseMessage response = await http.GetAsync($"{Base}/ap/v1/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Ready_CustomGateNeverReady_Returns503OnKeyedInstance()
    {
        // A never-ready gate (e.g. a deployment that has not finished loading) reports not-ready even on a
        // fully-keyed instance — the gate, not the default key check, controls the report.
        var (server, keyStore, keyProvider) = StartWithGate(ready: false);
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        // Load a real key (the default gate would say ready); the custom gate must still say not-ready.
        var actorIri = new Iri($"{Base}/ap/v1/u/{Alice}");
        var keyId = new Iri($"{actorIri}#key-1");
        keyStore.PutKey(KeyPairGenerator.GenerateRsa(keyId));
        keyProvider.RegisterKey(actorIri, keyId);

        HttpResponseMessage response = await http.GetAsync($"{Base}/ap/v1/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Ready_ResponseCarriesVersionHeader()
    {
        var (server, _, _) = StartKeyless();
        using var http = new HttpClient(server.CreateHandler(), disposeHandler: false);

        HttpResponseMessage response = await http.GetAsync($"{Base}/ap/v1/ready");

        Assert.True(response.Headers.TryGetValues("Iris-Version", out var values));
        Assert.Contains("1", values!);
    }

    /// <summary>
    /// Starts a keyless host (empty IKeyStore, nothing registered with the IKeyProvider) and returns its
    /// server + key seams so a test can flip the readiness by loading the key.
    /// </summary>
    private static (TestServer Server, InMemoryKeyStore KeyStore, IKeyProvider KeyProvider) StartKeyless()
    {
        var persistence = new InMemoryPersistenceProvider();
        var keyStore = new InMemoryKeyStore();
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
            },
        });

        return (server, keyStore, keyProvider);
    }

    /// <summary>
    /// Starts a host bound to a fixed <see cref="IReadinessGate"/> (stands in for a deployment that loads
    /// keys asynchronously and controls its own readiness).
    /// </summary>
    private static (TestServer Server, InMemoryKeyStore KeyStore, IKeyProvider KeyProvider) StartWithGate(bool ready)
    {
        var persistence = new InMemoryPersistenceProvider();
        var keyStore = new InMemoryKeyStore();
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = persistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
                s.AddSingleton<IReadinessGate>(_ => new ConstantReadinessGate(ready));
            },
        });

        return (server, keyStore, keyProvider);
    }

    /// <summary>
    /// A host-bound <see cref="IReadinessGate"/> that always reports a fixed readiness.
    /// </summary>
    private sealed class ConstantReadinessGate(bool ready) : IReadinessGate
    {
        public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(ready);
    }
}
