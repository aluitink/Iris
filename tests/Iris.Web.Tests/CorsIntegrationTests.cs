using System.Net;
using System.Net.Http;
using Iris.Server;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the production host's <em>CORS same-origin default + opt-in</em> (slice 33.5).
/// The app is same-origin-only by default (no CORS middleware → a cross-origin request gets no
/// <c>Access-Control-Allow-Origin</c> header and the browser blocks it). An operator opts a
/// third-party API consumer in by setting <c>Iris:Cors:Origins</c> (env <c>IRIS_CORS_ORIGINS</c>), which
/// registers the <see cref="WebAppFactory.CorsPolicyName"/> policy (an allow-list of the listed origins,
/// never <c>AllowAnyOrigin</c>) and applies <c>UseCors</c>. These tests boot the real service graph
/// (via <see cref="WebAppFactory.ConfigureServices"/>, which registers the policy only when origins are
/// set) on a <c>TestServer</c> and assert the <c>Access-Control-Allow-Origin</c> header behavior for a
/// cross-origin request: absent by default, present for a registered origin, absent for an unregistered
/// origin, and present on a preflight (OPTIONS) from a registered origin.
/// </summary>
public sealed class CorsIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";
    private const string OriginHeader = "Origin";
    private const string AllowOriginHeader = "Access-Control-Allow-Origin";
    private const string AllowMethodsHeader = "Access-Control-Allow-Methods";
    private const string PreflightMethodHeader = "Access-Control-Request-Method";
    private const string RegisteredOrigin = "https://app.example.org";

    private readonly TestServer _defaultServer;
    private readonly TestServer _optedInServer;
    private readonly HttpClient _defaultClient;
    private readonly HttpClient _optedInClient;

    public CorsIntegrationTests()
    {
        // Two hosts: the default (no origins configured → same-origin-only) and the opted-in one
        // (IRIS_CORS_ORIGINS set → the CorsPolicyName policy is registered + applied). Both reuse the
        // real service graph from the production composition root, and both apply the CORS middleware
        // exactly the way WebAppFactory.ConfigurePipeline does (only when the policy is registered).
        _defaultServer = StartServer(orchestratesOrigins: null);
        _optedInServer = StartServer(orchestratesOrigins: RegisteredOrigin);
        _defaultClient = _defaultServer.CreateClient();
        _optedInClient = _optedInServer.CreateClient();
    }

    public void Dispose()
    {
        _defaultClient.Dispose();
        _optedInClient.Dispose();
        _defaultServer.Dispose();
        _optedInServer.Dispose();
    }

    [Fact]
    public async Task Default_NoCrossOriginAccess_AllowOriginHeaderAbsent()
    {
        // A cross-origin GET (Origin header set to a foreign origin) on the default host must NOT
        // receive an Access-Control-Allow-Origin header — the app is same-origin-only by default.
        var request = new HttpRequestMessage(HttpMethod.Get, "/ap/v1/u/" + WebAppFactory.SeedHandle);
        request.Headers.Add(OriginHeader, RegisteredOrigin);

        var response = await _defaultClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(AllowOriginHeader));
    }

    [Fact]
    public async Task OptedIn_RegisteredOrigin_AllowOriginHeaderPresent()
    {
        // A cross-origin GET from a registered origin on the opted-in host MUST receive
        // Access-Control-Allow-Origin echoing that origin (and the credentials marker, since the policy
        // allows credentials).
        var request = new HttpRequestMessage(HttpMethod.Get, "/ap/v1/u/" + WebAppFactory.SeedHandle);
        request.Headers.Add(OriginHeader, RegisteredOrigin);

        var response = await _optedInClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(AllowOriginHeader));
        Assert.Equal(RegisteredOrigin, response.Headers.GetValues(AllowOriginHeader).Single());
    }

    [Fact]
    public async Task OptedIn_UnregisteredOrigin_AllowOriginHeaderAbsent()
    {
        // A cross-origin GET from an origin NOT in the allow-list must NOT receive the header — the
        // policy is a strict allow-list, never AllowAnyOrigin.
        var request = new HttpRequestMessage(HttpMethod.Get, "/ap/v1/u/" + WebAppFactory.SeedHandle);
        request.Headers.Add(OriginHeader, "https://evil.example.com");

        var response = await _optedInClient.SendAsync(request);

        Assert.False(response.Headers.Contains(AllowOriginHeader));
    }

    [Fact]
    public async Task OptedIn_RegisteredOrigin_Preflight_AllowsTheMethod()
    {
        // A cross-origin preflight (OPTIONS with Origin + Access-Control-Request-Method) from a
        // registered origin must be answered with the CORS headers (allow-origin + allow-methods) — the
        // preflight short-circuits before the signature/cookie gate (UseCors runs before auth).
        var request = new HttpRequestMessage(HttpMethod.Options, "/ap/v1/u/" + WebAppFactory.SeedHandle + "/inbox");
        request.Headers.Add(OriginHeader, RegisteredOrigin);
        request.Headers.Add(PreflightMethodHeader, "POST");

        var response = await _optedInClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(RegisteredOrigin, response.Headers.GetValues(AllowOriginHeader).Single());
        // Access-Control-Allow-Methods is a single comma-joined value (e.g. "GET,POST,PUT,DELETE,OPTIONS"),
        // not one entry per method — assert the POST method is in the comma-separated list.
        var allowMethods = string.Join(",", response.Headers.GetValues(AllowMethodsHeader));
        Assert.Contains("POST", allowMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Boots a <c>TestServer</c> with the real production service graph (via
    /// <see cref="WebAppFactory.ConfigureServices"/>), optionally setting <c>Iris:Cors:Origins</c> to the
    /// given origin. The pipeline mirrors <see cref="WebAppFactory.ConfigurePipeline"/>'s CORS handling:
    /// <c>UseCors(CorsPolicyName)</c> is applied only when the policy is registered (i.e. only when
    /// origins were configured), which is exactly the production wiring under test.
    /// </summary>
    private static TestServer StartServer(string? orchestratesOrigins)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (orchestratesOrigins is not null)
        {
            builder.Configuration["Iris:Cors:Origins"] = orchestratesOrigins;
        }
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

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
                // Mirror WebAppFactory.ConfigurePipeline's CORS wiring verbatim: apply UseCors only when
                // the CorsPolicyName policy was registered (ConfigureServices registers it only when
                // Iris:Cors:Origins is set to a non-empty allow-list). IOptions<CorsOptions> is registered
                // by AddCors even when no policy is added, so it is always resolvable; GetPolicy returns
                // null for an unregistered policy.
                var corsOptions = webApp.ApplicationServices
                    .GetRequiredService<IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>()
                    .Value;
                if (corsOptions.GetPolicy(WebAppFactory.CorsPolicyName) is not null)
                {
                    webApp.UseCors(WebAppFactory.CorsPolicyName);
                }
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapActivityPubEndpoints();
                    endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
                });
            });

        var server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(server.Services, builder.Configuration, Base);
        return server;
    }
}
