using Iris.Core.Identity;
using Iris.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// S28 regression: the no-auth <c>GET /local/v1/session/public</c> endpoint must expose the instance's
/// effective advertised base (<c>serverBaseUri</c>) — the FQDN the server serves its ActivityPub
/// endpoints and <c>iris:</c> extension namespace under (its <c>ActivityPubServerOptions.BaseUri</c>).
///
/// The WASM client derives its <c>iris:</c> namespace from this value (rather than its own baked-in
/// appsettings.json <c>AdvertiseBase</c>) so it reads the <c>likedCount</c>/<c>sharedCount</c> counters
/// under the SAME namespace the server stamped onto object documents. When one WASM build is deployed
/// to multiple instances (each with its own FQDN), the baked-in AdvertiseBase diverges from the
/// per-deployment FQDN, which otherwise makes the client read the counters under the wrong namespace,
/// skip the fast path, and render a Boost count of 0.
/// </summary>
public sealed class SessionServerBaseUriEndpointTests
{
    [Fact]
    public async Task SessionPublic_ReturnsServerBaseUriFromAdvertiseBase()
    {
        // The server's effective base is the per-deployment AdvertiseBase FQDN.
        using var host = BuildHost(new Iri("https://dev2-iris-a.luit.ink"));
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/session/public");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // The effective advertised base is exposed so the client matches the server's namespace.
        Assert.Contains("https://dev2-iris-a.luit.ink", body);
        // The public feed IRI is still derived from the same base.
        Assert.Contains("https://dev2-iris-a.luit.ink/ap/v1/public/feed", body);
    }

    [Fact]
    public async Task SessionPublic_TrimsTrailingSlashFromServerBaseUri()
    {
        using var host = BuildHost(new Iri("https://qa-iris.luit.ink/"));
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/session/public");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // Trailing slash trimmed — the value must be a clean base (the client appends /ns#).
        Assert.DoesNotContain("https://qa-iris.luit.ink//ns", body);
        Assert.Contains("https://qa-iris.luit.ink", body);
    }

    [Fact]
    public async Task SessionPublic_FallsBackToLocalhost_WhenBaseUriUnset()
    {
        // No AdvertiseBase configured (e.g. a bare dev run): the server base falls back to localhost.
        using var host = BuildHost(null);
        await host.StartAsync();

        var response = await host.GetTestClient().GetAsync("/local/v1/session/public");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("http://localhost", body);
    }

    private static IHost BuildHost(Iri? baseUri) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer();
                builder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IOptions<ActivityPubServerOptions>>(
                        Options.Create(new ActivityPubServerOptions { BaseUri = baseUri }));
                });
                builder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        WebAppFactory.MapSessionEndpoints(
                            endpoints,
                            endpoints.ServiceProvider.GetRequiredService<IOptions<ActivityPubServerOptions>>());
                    });
                });
            })
            .Build();
}
