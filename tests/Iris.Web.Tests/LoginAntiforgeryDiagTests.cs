using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Iris.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Diagnostic test for the live login 400 (Phase 71 Paused Question). Boots the real production
/// service graph + pipeline on a TestServer and walks the exact anti-forgery flow the browser login
/// form performs: (1) GET /local/v1/antiforgery to obtain a fresh token pair (the response sets the
/// HandlerToken cookie), (2) POST /login carrying the RequestToken in the __RequestVerificationToken
/// form field plus the HandlerToken cookie. Asserts the POST is accepted (302, not 400) and surfaces
/// the exact AntiforgeryException reason when it is rejected, so the root cause is visible.
/// </summary>
public sealed class LoginAntiforgeryDiagTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly HttpClient _client;

    public LoginAntiforgeryDiagTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://localhost");
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
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/local/v1/antiforgery", (Microsoft.AspNetCore.Antiforgery.IAntiforgery af, HttpContext ctx) =>
                    {
                        var token = af.GetAndStoreTokens(ctx);
                        return Results.Json(new { token.RequestToken });
                    });
                    endpoints.MapPost("/login", (HttpContext ctx) => Results.Redirect("/"));
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task Login_Post_WithFreshAntiforgeryToken_IsAccepted_Not400()
    {
        // Step 1: obtain a fresh token pair (mirrors the WASM login page's OnInitializedAsync fetch).
        var afRes = await _client.GetAsync("/local/v1/antiforgery");
        Assert.Equal(HttpStatusCode.OK, afRes.StatusCode);
        var afJson = await afRes.Content.ReadAsStringAsync();
        // Extract the request token + the HandlerToken cookie the server set.
        var requestToken = ExtractJsonString(afJson, "requestToken");
        Assert.False(string.IsNullOrEmpty(requestToken), "antiforgery endpoint did not return a request token. body=" + afJson);
        var handlerCookie = afRes.Headers.GetValues("Set-Cookie").FirstOrDefault(c => c!.Contains(".AspNetCore.Antiforgery."));
        Assert.False(string.IsNullOrEmpty(handlerCookie), "antiforgery endpoint did not set the HandlerToken cookie.");

        // Step 2: POST /login with the RequestToken form field + the HandlerToken cookie.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = requestToken,
            ["handle"] = "andrew",
            ["password"] = "Password1",
        });
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = form };
        var handlerName = handlerCookie!.Split('=')[0].Trim();
        var handlerValue = handlerCookie.Substring(handlerCookie.IndexOf('=') + 1).Split(';')[0].Trim();
        loginReq.Headers.TryAddWithoutValidation("Cookie", $"{handlerName}={handlerValue}");

        try
        {
            var loginRes = await _client.SendAsync(loginReq);
            var body = await loginRes.Content.ReadAsStringAsync();
            // The point of this diagnostic: a valid token must NOT be rejected 400 by anti-forgery.
            Assert.NotEqual(HttpStatusCode.BadRequest, loginRes.StatusCode);
        }
        catch (Exception ex)
        {
            // Surface the exact anti-forgery reason (TestServer surfaces the inner 400 as an exception).
            var inner = ex.InnerException ?? ex;
            Assert.Fail("login POST threw: " + ex.GetType().Name + ": " + ex.Message + "\ninner=" + inner.GetType().Name + ": " + inner.Message);
        }
    }

    private static string ExtractJsonString(string json, string key)
    {
        var marker = "\"" + key + "\":";
        var idx = json.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var start = idx + marker.Length;
        while (start < json.Length && json[start] == ' ') start++;
        if (start >= json.Length || json[start] != '"') return string.Empty;
        start++;
        var end = json.IndexOf('"', start);
        return end < 0 ? string.Empty : json[start..end];
    }
}
