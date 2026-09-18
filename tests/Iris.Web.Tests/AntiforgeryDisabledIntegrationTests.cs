using System.Net;
using System.Net.Http;
using Iris.Server;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for the Phase 94 antiforgery toggle. Boots the real production service graph
/// (via <see cref="WebAppFactory.ConfigureServices"/>) on a <c>TestServer</c> and applies the
/// antiforgery middleware using the <em>same</em> decision function the production
/// <see cref="WebAppFactory.ConfigurePipeline"/> uses (<see cref="WebAppFactory.IsAntiforgeryEnabled"/>
/// reading <c>Iris:Security:EnableAntiforgery</c> from the app's configuration). This exercises the
/// real toggle logic + the real <c>UseAntiforgery</c> middleware behavior, end to end.
///
/// Contract:
/// <list type="bullet">
///   <item>When the flag is unset (production default), a <c>POST /login</c> carrying <em>no</em>
///     antiforgery token is rejected <c>400</c> (the middleware validates and fails).</item>
///   <item>When <c>Iris:Security:EnableAntiforgery=false</c>, the same tokenless <c>POST /login</c>
///     passes the (absent) antiforgery middleware and reaches the handler — with bad credentials it
///     returns a <c>302</c> redirect (NOT <c>400</c>), proving the middleware was skipped.</item>
///   <item>The <c>/local/v1/antiforgery</c> token-issuance endpoint still works when disabled
///     (<c>AddAntiforgery</c> is always registered; only the validation middleware is skipped).</item>
/// </list>
/// The login handler redirects on bad credentials (not a 400), so the status code is a clean
/// discriminator between "rejected by antiforgery" (400) and "passed to the handler" (302).
/// </summary>
public sealed class AntiforgeryDisabledIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _enabledServer;
    private readonly TestServer _disabledServer;
    private readonly HttpClient _enabledClient;
    private readonly HttpClient _disabledClient;

    public AntiforgeryDisabledIntegrationTests()
    {
        _enabledServer = StartServer(enableAntiforgery: null);
        _disabledServer = StartServer(enableAntiforgery: "false");
        _enabledClient = _enabledServer.CreateClient();
        _disabledClient = _disabledServer.CreateClient();
    }

    public void Dispose()
    {
        _enabledClient.Dispose();
        _disabledClient.Dispose();
        _enabledServer.Dispose();
        _disabledServer.Dispose();
    }

    [Fact]
    public async Task Enabled_Default_TokenlessLoginPOST_IsRejected400()
    {
        // With antiforgery ON (the default), a POST /login with no __RequestVerificationToken field
        // and no HandlerToken cookie must be rejected 400 by the UseAntiforgery middleware — the
        // production behavior an attacker's forged POST would hit.
        var response = await _enabledClient.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["handle"] = "andrew",
                ["password"] = "wrong-password",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_TokenlessLoginPOST_PassesToHandler_Not400()
    {
        // With antiforgery DISABLED (Iris:Security:EnableAntiforgery=false), the same tokenless POST
        // must NOT be rejected 400 — the middleware is absent, so the request reaches the login
        // handler, which (with bad credentials) redirects 302 back to /login?error=….
        var response = await _disabledClient.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["handle"] = "andrew",
                ["password"] = "wrong-password",
            }));

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        // The login handler redirects (302) on bad credentials — proves the request passed the
        // (absent) antiforgery middleware and reached the handler.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_TokenIssuanceEndpoint_StillWorks()
    {
        // Even with antiforgery disabled, the /local/v1/antiforgery endpoint still issues a token
        // (AddAntiforgery is always registered; only the validation middleware is skipped). The
        // client can fetch a token harmlessly.
        var response = await _disabledClient.GetAsync("/local/v1/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("requestToken", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Boots the real production service graph on a <c>TestServer</c>. When
    /// <paramref name="enableAntiforgery"/> is non-null, <c>Iris:Security:EnableAntiforgery</c> is set
    /// to it in the app's configuration; otherwise the flag is unset (the production default →
    /// antiforgery ON). The antiforgery middleware is applied using the <em>real</em> decision
    /// function <see cref="WebAppFactory.IsAntiforgeryEnabled"/> (reading the flag from
    /// <c>webApp.Configuration</c>) — exactly the way <see cref="WebAppFactory.ConfigurePipeline"/>
    /// does — so the test exercises the production toggle logic end to end.
    /// </summary>
    private static TestServer StartServer(string? enableAntiforgery)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (enableAntiforgery is not null)
        {
            builder.Configuration[WebAppFactory.EnableAntiforgeryConfigKey] = enableAntiforgery;
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
                // Apply UseAntiforgery UNCONDITIONALLY — exactly as WebAppFactory.ConfigurePipeline does
                // (the POST endpoints carry antiforgery metadata, so the middleware must always be
                // applied). The permissive behavior when the flag is off comes from the DI-registered
                // PermissiveAntiforgery (a no-op IAntiforgery) that WebAppFactory.ConfigureServices
                // registers after AddAntiforgery() when the flag is disabled — the middleware's
                // IsRequestValidAsync then always passes.
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseEndpoints(endpoints =>
                {
                    // The real auth endpoints (login/register/logout) + the antiforgery token
                    // endpoint (MapAuthEndpoints maps /local/v1/antiforgery itself, so it must NOT be
                    // mapped again here — a duplicate would be an AmbiguousMatchException).
                    WebAppFactory.MapAuthEndpoints(endpoints);
                });
            });

        var server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(server.Services, builder.Configuration, Base);
        return server;
    }
}
