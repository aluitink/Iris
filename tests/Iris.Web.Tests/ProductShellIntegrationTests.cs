using System.Net;
using System.Net.Http;
using System.Text;
using Iris.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 32.4a — the signed-in <em>product shell</em> + home timeline. They boot
/// the real app (in-memory persistence, the default) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and assert the shell's auth-gating + navigation surface:
/// </summary>
/// <list type="bullet">
/// <item>A signed-out request to the <c>[Authorize]</c>-gated <c>/home</c> is redirected to
/// <c>/login</c> by the authorization middleware (the cookie scheme's <c>LoginPath</c>).</item>
/// <item>A signed-in request to <c>/home</c> returns 200 with the home-timeline card's static markup
/// (the feed items themselves are loaded by the interactive circuit, not the static prerender).</item>
/// <item>The signed-in app's navigation shows the product links (Home, …, Log out) when authenticated
/// and the login/register links when not.</item>
/// </list>
/// <remarks>
/// The interactive circuit's client-side navigation (e.g. the <c>/</c> dispatcher's
/// <c>NavigationManager.NavigateTo</c>) is not observable over <see cref="TestServer"/> HTTP (it is a
/// circuit navigation, not an HTTP 302) — the full click-through is verified live via the MCP
/// Playwright server (per <c>docs/plans/production-app-feature-set.md</c> §1: the app's screens are
/// verified functionally + visually in a real browser, not by an automated UI test suite). These
/// integration tests lock the HTTP-observable contract: the <c>[Authorize]</c> gating and the
/// auth-aware navigation shell.
/// </remarks>
public sealed class ProductShellIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public ProductShellIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
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
                // The production pipeline — the same one LocalAuthIntegrationTests boots, so the
                // [Authorize] gating + auth-aware nav are exercised exactly as in production.
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
                    WebAppFactory.MapAuthEndpoints(endpoints);
                    endpoints.MapActivityPubEndpoints();
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // ------------------------------------------------------------------
    // The [Authorize]-gated home timeline.
    // ------------------------------------------------------------------

    [Fact]
    public async Task HomePage_SignedOut_RedirectsToLogin()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/home");
        // The [Authorize] attribute + the cookie scheme's LoginPath (/login) make the authorization
        // middleware issue an HTTP 302 to /login for an unauthenticated request. The Location is an
        // absolute URL (TestServer's base) with a ReturnUrl back to /home so the user lands on the
        // page they wanted after signing in.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("/login", location.PathAndQuery.Split('?', 2)[0]);
        Assert.Contains("ReturnUrl=%2Fhome", location.Query);
    }

    [Fact]
    public async Task HomePage_SignedIn_Returns200WithFeedCard()
    {
        var (client, authCookie) = await SignInAsync("homeuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/home", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The home-timeline card's static markup is in the prerendered page (the feed items themselves
        // are loaded by the interactive circuit and are not in the static HTML).
        Assert.Contains("Home timeline", html);
        Assert.Contains("paged-collection", html);
    }

    [Fact]
    public async Task HomePage_SignedIn_NavShowsProductLinks()
    {
        var (client, authCookie) = await SignInAsync("navuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/home", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The signed-in navigation (the AuthorizeView's Authorized branch) is in the prerendered shell.
        Assert.Contains("Log out", html);
        Assert.Contains("href=\"/home\"", html);
    }

    [Fact]
    public async Task LoginPage_SignedOut_NavShowsLoginLinks()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The signed-out navigation (the AuthorizeView's NotAuthorized branch).
        Assert.Contains("Log in", html);
        Assert.Contains("Register", html);
        // And it does NOT show the signed-in product links.
        Assert.DoesNotContain("Log out", html);
    }

    // ------------------------------------------------------------------
    // Helpers: sign in over the real HTTP surface, then make an
    // authenticated request with the returned iris.auth cookie.
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a local account via the <c>POST /register</c> endpoint (the real HTTP surface) and
    /// returns the client plus the <c>iris.auth</c> cookie the sign-in set (the TestServer client does
    /// not persist cookies, so the cookie is captured from <c>Set-Cookie</c> and re-sent manually).
    /// </summary>
    private async Task<(HttpClient Client, string AuthCookie)> SignInAsync(string handle, string password)
    {
        var client = _server.CreateClient();
        var pageResponse = await client.GetAsync("/register");
        var page = await pageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(page);
        var antiforgeryCookie = ExtractAntiforgeryCookie(pageResponse);

        var form = new StringContent(
            $"handle={Uri.EscapeDataString(handle)}&password={Uri.EscapeDataString(password)}" +
            $"&__RequestVerificationToken={Uri.EscapeDataString(token)}",
            Encoding.UTF8, "application/x-www-form-urlencoded");

        var request = new HttpRequestMessage(HttpMethod.Post, "/register")
        {
            Content = form,
        };
        if (!string.IsNullOrEmpty(antiforgeryCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        }

        var signIn = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Found, signIn.StatusCode);

        var authCookie = signIn.Headers.GetValues("Set-Cookie")!
            .Where(c => c.StartsWith("iris.auth", StringComparison.Ordinal))
            .Select(c => c.Split(';', 2)[0].Trim())
            .Single();
        return (client, authCookie);
    }

    private static async Task<HttpResponseMessage> GetWithAuthAsync(
        HttpClient client, string url, string authCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", authCookie);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start != -1, "no antiforgery token in the rendered form");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private static string? ExtractAntiforgeryCookie(HttpResponseMessage pageResponse)
    {
        foreach (var cookie in pageResponse.Headers.GetValues("Set-Cookie"))
        {
            if (cookie.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            {
                return cookie.Split(';', 2)[0].Trim();
            }
        }

        return null;
    }
}
