using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.InMemory;
using Iris.Server.Observability;
using Iris.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 30.1 — <strong>Configuration surface</strong>: verifies that <see cref="ActivityPubServerExtensions.AddActivityPubServer(IServiceCollection, IConfiguration)"/>
/// correctly binds <see cref="ActivityPubServerOptions"/> and all delivery/observability options from
/// conventional configuration sections, and that the bound values are observable through the
/// <c>IOptions&lt;T&gt;</c> infrastructure and the live host endpoints.
/// </summary>
public sealed class ConfigurationBindingIntegrationTests : IDisposable
{
    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly HttpClient _http;

    public ConfigurationBindingIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:BaseUri"] = "https://config-test.local",
                ["Iris:InstanceName"] = "Config Test Instance",
                ["Iris:InstanceActorId"] = "https://config-test.local/ap/v1/u/alice",
                ["Iris:SharedInboxIri"] = "https://config-test.local/ap/v1/shared-inbox",
                ["Iris:ProxySettings:MaxRequestsPerMinute"] = "42",
                ["Iris:Delivery:Retry:MaxAttempts"] = "7",
                ["Iris:Delivery:Retry:BaseDelay"] = "00:00:03",
                ["Iris:Delivery:Retry:MaxDelay"] = "00:01:00",
                ["Iris:Delivery:Worker:MaxConcurrentDeliveries"] = "4",
                ["Iris:Delivery:RateLimit:PerPeerMaxRequestsPerMinute"] = "100",
                ["Iris:Delivery:CircuitBreaker:FailureThreshold"] = "10",
                ["Iris:Delivery:CircuitBreaker:OpenDuration"] = "00:02:00",
                ["Iris:Inbound:RateLimit:PerPeerMaxRequestsPerMinute"] = "200",
                ["Iris:Feed:PagesPerActor"] = "3",
                ["Iris:Feed:MaxItems"] = "500",
                ["Iris:Health:DeliveryQueue:WarningPending"] = "50",
                ["Iris:Health:DeliveryQueue:CriticalPending"] = "100",
            })
            .Build();

        var persistence = new InMemoryPersistenceProvider();
        var seeded = TestSeeder.SeedPersonWithKey(persistence, "config-test.local", "alice");
        var key = seeded.Key;
        var actorIri = seeded.ActorIri;

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(services =>
            {
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                services.AddRouting();
                services.AddActivityPubServer(config);
                services.AddInMemoryPersistence();
                services.AddSingleton<IPersistenceProvider>(persistence);

                var keyStore = new InMemoryKeyStore();
                keyStore.PutKey(key);
                var keyProvider = new InMemoryKeyProvider(keyStore);
                keyProvider.RegisterKey(actorIri, key.KeyId);
                var signer = new HttpSignatureSigner(keyStore);

                services.AddSingleton<IKeyStore>(keyStore);
                services.AddSingleton<IKeyProvider>(keyProvider);
                services.AddSingleton<ISignatureSigner>(signer);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        _server = new TestServer(builder);
        _services = _server.Services;
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void ActivityPubServerOptions_BoundFromIrisSection()
    {
        var options = _services.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value;
        Assert.Equal(new Iri("https://config-test.local"), options.BaseUri);
        Assert.Equal("Config Test Instance", options.InstanceName);
        Assert.Equal(new Iri("https://config-test.local/ap/v1/u/alice"), options.InstanceActorId);
        Assert.Equal(new Iri("https://config-test.local/ap/v1/shared-inbox"), options.SharedInboxIri);
        Assert.Equal(42, options.ProxySettings!.MaxRequestsPerMinute);
    }

    [Fact]
    public void DeliveryRetryOptions_BoundFromIrisDeliveryRetrySection()
    {
        var options = _services.GetRequiredService<IOptions<DeliveryRetryOptions>>().Value;
        Assert.Equal(7, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxDelay);
    }

    [Fact]
    public void DeliveryWorkerOptions_BoundFromIrisDeliveryWorkerSection()
    {
        var options = _services.GetRequiredService<IOptions<DeliveryWorkerOptions>>().Value;
        Assert.Equal(4, options.MaxConcurrentDeliveries);
    }

    [Fact]
    public void DeliveryRateLimitOptions_BoundFromIrisDeliveryRateLimitSection()
    {
        var options = _services.GetRequiredService<IOptions<DeliveryRateLimitOptions>>().Value;
        Assert.Equal(100, options.PerPeerMaxRequestsPerMinute);
    }

    [Fact]
    public void DeliveryCircuitBreakerOptions_BoundFromIrisDeliveryCircuitBreakerSection()
    {
        var options = _services.GetRequiredService<IOptions<DeliveryCircuitBreakerOptions>>().Value;
        Assert.Equal(10, options.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(120), options.OpenDuration);
    }

    [Fact]
    public void InboundRateLimitOptions_BoundFromIrisInboundRateLimitSection()
    {
        var options = _services.GetRequiredService<IOptions<InboundRateLimitOptions>>().Value;
        Assert.Equal(200, options.PerPeerMaxRequestsPerMinute);
    }

    [Fact]
    public void FeedOptions_BoundFromIrisFeedSection()
    {
        var options = _services.GetRequiredService<IOptions<FeedOptions>>().Value;
        Assert.Equal(3, options.PagesPerActor);
        Assert.Equal(500, options.MaxItems);
    }

    [Fact]
    public void DeliveryQueueHealthOptions_BoundFromIrisHealthDeliveryQueueSection()
    {
        var options = _services.GetRequiredService<IOptions<DeliveryQueueHealthOptions>>().Value;
        Assert.Equal(50, options.WarningPending);
        Assert.Equal(100, options.CriticalPending);
    }

    [Fact]
    public async Task ActorDocument_ServesConfiguredInstance()
    {
        var response = await _http.GetAsync("https://config-test.local/ap/v1/u/alice");
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("alice", body);
    }
}
