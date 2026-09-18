using Iris.Server;
using Iris.Server.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// Phase 136.1: the <c>GET /local/v1/federation-trace</c> endpoint returns the federation trace
/// collector's snapshot as JSON (count + entries). Verifies the operator-facing read path.
/// </summary>
public sealed class FederationTraceEndpointTests
{
    [Fact]
    public async Task FederationTraceEndpoint_ReturnsCollectorSnapshotAsJson()
    {
        var collector = new InMemoryFederationTraceCollector();
        collector.Record(new FederationTraceEntry(
            DateTimeOffset.UtcNow,
            FederationDirection.Outbound,
            "POST",
            "https://peer.example/ap/v1/u/alice/inbox",
            202,
            "https://peer.example/ap/v1/u/alice",
            "https://me.example/ap/v1/u/me",
            "Create"));

        using var host = BuildHost(collector);
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/federation-trace");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"count\":1", body);
        Assert.Contains("Outbound", body);
        Assert.Contains("https://peer.example/ap/v1/u/alice/inbox", body);
        Assert.Contains("\"status\":202", body);
        Assert.Contains("\"activityType\":\"Create\"", body);
    }

    [Fact]
    public async Task FederationTraceEndpoint_EmptyCollector_ReturnsZeroCount()
    {
        using var host = BuildHost(new InMemoryFederationTraceCollector());
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/federation-trace");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"count\":0", body);
    }

    private static IHost BuildHost(IFederationTraceCollector collector) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IFederationTraceCollector>(collector);
                    services.AddSingleton<IOptions<ActivityPubServerOptions>>(
                        Options.Create(new ActivityPubServerOptions()));
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        WebAppFactory.MapFederationTraceEndpoint(endpoints);
                    });
                });
            })
            .Build();
}
