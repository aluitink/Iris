using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Web;
using Iris.Web.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 32.3 — <em>local auth</em> (username/password registration + login,
/// provisioning a local actor per account). They boot the real app (in-memory persistence, the
/// default) in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and exercise the
/// account + actor services directly (the same service graph the <c>/login</c>/<c>/register</c> Razor
/// pages use), plus the HTTP surface of the login/register pages.
/// </summary>
/// <remarks>
/// The host uses a fixed advertised base (<c>https://web.test.local</c>) so provisioned actor IRIs are
/// deterministic. The seeded actor (<c>iris</c>, <see cref="WebAppFactory.SeedHandle"/>) is a system
/// actor, not a local account — local accounts are provisioned by the registration service.
/// </remarks>
public sealed class LocalAuthIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public LocalAuthIntegrationTests()
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
                // The production pipeline (routing, antiforgery, signature validation, cookie auth,
                // the Blazor Web App, the /register|/login|/logout auth endpoints, and the
                // ActivityPub endpoints) — exercised in-process via TestServer. (Static-asset
                // middleware is omitted: the tests exercise the auth endpoints + services, not the
                // Blazor framework JS.)
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
    // Handle rules (pure validation, exercised through the registration path).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("a")]                       // too short
    [InlineData("-ab")]                     // leading hyphen
    [InlineData("ab-")]                     // trailing hyphen
    [InlineData("alice_bob")]               // underscore not allowed
    [InlineData("admin")]                   // reserved
    [InlineData("nodeinfo")]                // reserved
    [InlineData("api")]                     // reserved
    public void HandleRules_RejectInvalidHandles(string handle)
    {
        Assert.NotNull(HandleRules.Validate(handle));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("bob-2")]
    [InlineData("a1-b2")]
    public void HandleRules_AcceptValidHandles(string handle)
    {
        Assert.Null(HandleRules.Validate(handle));
    }

    // ------------------------------------------------------------------
    // Registration.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Register_CreatesAccountAndProvisionsActor()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();

        var result = await registration.RegisterAsync("newuser", "s3cret-pw", "New User");

        Assert.True(result.Succeeded, $"registration failed: {result.Error}");
        Assert.NotNull(result.Account);
        Assert.Equal("newuser", result.Account!.Username);
        Assert.Equal(UserRole.User, result.Account.Role);
        // The linked actor is a local actor under the /ap/v1/u/ namespace.
        Assert.StartsWith($"{Base}/ap/v1/u/newuser", result.Account.ActorId.Value);

        // The actor exists in the persistence store and carries its public key.
        var actorStore = _services.GetRequiredService<Iris.Server.Stores.IPersistenceProvider>().Actors;
        var found = await actorStore.TryGetActorAsync(
            new Iris.Core.Identity.Iri(result.Account.ActorId.Value), out var actor, CancellationToken.None);
        Assert.True(found, "the provisioned actor should be retrievable");
        Assert.NotNull(actor);
        Assert.Equal("newuser", actor!.PreferredUsername);

        // The account is retrievable by username and its password hash verifies.
        var stored = await accounts.FindByUsernameAsync("newuser", CancellationToken.None);
        Assert.NotNull(stored);
        var hasher = _services.GetRequiredService<PasswordHasher>();
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            hasher.Verify(stored!.PasswordHash, "s3cret-pw"));
    }

    [Fact]
    public async Task Register_RejectsDuplicateUsername_CaseInsensitive()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        await registration.RegisterAsync("dupuser", "s3cret-pw", null);

        // Same username, different case — still taken.
        var result = await registration.RegisterAsync("DupUser", "other-pw-1", null);
        Assert.False(result.Succeeded);
        Assert.Equal("That username is already taken.", result.Error);
    }

    [Fact]
    public async Task Register_RejectsShortPassword()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var result = await registration.RegisterAsync("shortpw", "123", null);
        Assert.False(result.Succeeded);
        Assert.Equal($"The password must be at least {RegistrationService.MinPasswordLength} characters.", result.Error);
    }

    // ------------------------------------------------------------------
    // Login.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Login_SucceedsWithCorrectPassword()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var login = _services.GetRequiredService<LoginService>();
        await registration.RegisterAsync("logintest", "s3cret-pw", null);

        var result = await login.LoginAsync("logintest", "s3cret-pw", "10.0.0.1");

        Assert.True(result.Succeeded, $"login failed: {result.Error}");
        Assert.NotNull(result.Account);
        Assert.Equal("logintest", result.Account!.Username);
    }

    [Fact]
    public async Task Login_FailsWithWrongPassword_GenericMessage()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var login = _services.GetRequiredService<LoginService>();
        await registration.RegisterAsync("wrongpw", "s3cret-pw", null);

        var result = await login.LoginAsync("wrongpw", "not-the-password", "10.0.0.1");

        Assert.False(result.Succeeded);
        // The message is the same generic one (it never reveals which field was wrong).
        Assert.Equal(LoginService.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task Login_FailsWithUnknownUsername_SameGenericMessage()
    {
        var login = _services.GetRequiredService<LoginService>();
        var result = await login.LoginAsync("nobody-here", "s3cret-pw", "10.0.0.1");

        Assert.False(result.Succeeded);
        Assert.Equal(LoginService.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task Login_RateLimitsAfterRepeatedFailures()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var login = _services.GetRequiredService<LoginService>();
        await registration.RegisterAsync("ratelimit", "s3cret-pw", null);

        // The limiter allows 5 failures in the window; the 6th is blocked.
        for (var i = 0; i < 5; i++)
        {
            var r = await login.LoginAsync("ratelimit", "bad", "10.0.0.9");
            Assert.Equal(LoginService.InvalidCredentials, r.Error);
        }

        // The 6th attempt is rate-limited (a distinct message), even with the correct password.
        var blocked = await login.LoginAsync("ratelimit", "s3cret-pw", "10.0.0.9");
        Assert.False(blocked.Succeeded);
        Assert.Equal(LoginService.RateLimited, blocked.Error);
    }

    [Fact]
    public async Task Login_RateLimitKeyedByUsernamePlusIp()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var login = _services.GetRequiredService<LoginService>();
        await registration.RegisterAsync("ipkey", "s3cret-pw", null);

        // Exhaust the budget from one IP.
        for (var i = 0; i < 5; i++)
        {
            await login.LoginAsync("ipkey", "bad", "10.0.0.1");
        }

        // A different IP for the same username is NOT blocked (the key includes the IP).
        var otherIp = await login.LoginAsync("ipkey", "s3cret-pw", "10.0.0.2");
        Assert.True(otherIp.Succeeded);
    }

    // ------------------------------------------------------------------
    // Password hasher.
    // ------------------------------------------------------------------

    [Fact]
    public void PasswordHasher_HashAndVerify()
    {
        var hasher = _services.GetRequiredService<PasswordHasher>();
        var hash = hasher.Hash("s3cret-pw");
        Assert.NotEqual("s3cret-pw", hash);
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success, hasher.Verify(hash, "s3cret-pw"));
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, hasher.Verify(hash, "wrong"));
    }

    // ------------------------------------------------------------------
    // Admin bootstrapper (idempotent).
    // ------------------------------------------------------------------

    [Fact]
    public async Task AdminBootstrapper_CreatesAdminWhenConfigured()
    {
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var provisioner = _services.GetRequiredService<ActorProvisioner>();
        var hasher = _services.GetRequiredService<PasswordHasher>();
        var config = _services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var logger = _services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminBootstrapper>>();

        // No admin yet, and no App:Admin config → the bootstrapper is a no-op.
        Assert.False(await accounts.AnyAdminExistsAsync(CancellationToken.None));

        // Configure an admin in memory and run the bootstrapper.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:Admin:Username"] = "rootadmin",
                ["App:Admin:Password"] = "admin-pw-123",
            })
            .Build();
        var bootstrapper = new AdminBootstrapper(_services, cfg, new Iris.Core.Identity.Iri(Base), logger);
        await bootstrapper.StartAsync(CancellationToken.None);

        Assert.True(await accounts.AnyAdminExistsAsync(CancellationToken.None));
        var admin = await accounts.FindByUsernameAsync("rootadmin", CancellationToken.None);
        Assert.NotNull(admin);
        Assert.Equal(UserRole.Admin, admin!.Role);
        Assert.StartsWith($"{Base}/ap/v1/u/rootadmin", admin.ActorId.Value);

        // Idempotent: a second run does not create a second admin.
        await bootstrapper.StartAsync(CancellationToken.None);
        var adminAfter = await accounts.FindByUsernameAsync("rootadmin", CancellationToken.None);
        Assert.Equal(admin!.Id, adminAfter!.Id);
    }

    // ------------------------------------------------------------------
    // Claims factory (the cookie-identity schema).
    // ------------------------------------------------------------------

    [Fact]
    public async Task ClaimsFactory_CarriesUserIdUsernameActorAndRole()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var result = await registration.RegisterAsync("claimsuser", "s3cret-pw", null);
        Assert.NotNull(result.Account);
        var identity = ClaimsFactory.CreateIdentity(result.Account!);

        Assert.Equal(result.Account!.Id.ToString(), identity.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("claimsuser", identity.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal(result.Account.ActorId.Value, identity.FindFirst(ActorClaims.ActorIri)?.Value);
        Assert.Equal(UserRole.User.ToString(), identity.FindFirst(ClaimTypes.Role)?.Value);
    }

    // ------------------------------------------------------------------
    // HTTP surface: the /login and /register pages are served (200) and
    // redirect appropriately when unauthenticated / authenticated.
    // ------------------------------------------------------------------

    [Fact]
    public async Task LoginPage_IsServed()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign in", html);
    }

    [Fact]
    public async Task RegisterPage_IsServed()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Create your account", html);
    }

    // ------------------------------------------------------------------
    // Auth ENDPOINTS (the plain-HTTP sign-in/out surface): POST /register,
    // POST /login, GET /logout. These are the only way a local account signs
    // in (the interactive Blazor circuit cannot set cookies).
    // ------------------------------------------------------------------

    /// <summary>
    /// A TestServer client (default behavior: follows redirects). The auth endpoints 302-redirect to
    /// <c>/</c> (success) or <c>/login?error=…</c>/<c>/register?error=…</c> (failure); the tests assert
    /// on the final <see cref="System.Net.Http.HttpResponseMessage.RequestMessage"/> URL after the
    /// redirect is followed.
    /// </summary>
    private System.Net.Http.HttpClient CreateClient() => _server.CreateClient();

    /// <summary>
    /// Extracts the antiforgery <em>cookie</em> name=value pair set by a GET of an auth page. The cookie
    /// is named <c>.AspNetCore.Antiforgery.&lt;app&gt;</c> (httponly). The default
    /// <c>TestServer.CreateClient()</c> does not persist cookies, so the cookie is captured from the
    /// <c>Set-Cookie</c> header and re-sent manually on the POST (browser-like) so the token validates
    /// against the same session.
    /// </summary>
    private static string? ExtractAntiforgeryCookie(System.Net.Http.HttpResponseMessage pageResponse)
    {
        var setCookies = pageResponse.Headers.GetValues("Set-Cookie");
        foreach (var cookie in setCookies)
        {
            // The antiforgery cookie name always starts with ".AspNetCore.Antiforgery." (httponly).
            // Take its name=value (up to the first ';').
            if (cookie.StartsWith(".AspNetCore.Antiforgery.", System.StringComparison.Ordinal))
            {
                return cookie.Split(';', 2)[0].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Registers a local account via the <c>POST /register</c> endpoint (the real HTTP surface the
    /// Razor form posts to). A single <see cref="System.Net.Http.HttpClient"/> GETs the form (capturing
    /// the antiforgery cookie + token, as a browser's cookie jar would) then POSTs both.
    /// </summary>
    private async Task<System.Net.Http.HttpResponseMessage> PostRegisterAsync(
        string handle, string password, string? displayName = null)
    {
        var client = CreateClient();
        var pageResponse = await client.GetAsync("/register");
        var page = await pageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(page);
        var cookie = ExtractAntiforgeryCookie(pageResponse);
        var form = new System.Net.Http.StringContent(
            $"handle={Uri.EscapeDataString(handle)}&password={Uri.EscapeDataString(password)}" +
            (displayName is null ? string.Empty : $"&displayName={Uri.EscapeDataString(displayName)}") +
            $"&__RequestVerificationToken={Uri.EscapeDataString(token)}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        return await PostWithAntiforgeryAsync(client, "/register", form, cookie);
    }

    private async Task<System.Net.Http.HttpResponseMessage> PostLoginAsync(string handle, string password)
    {
        var client = CreateClient();
        var pageResponse = await client.GetAsync("/login");
        var page = await pageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(page);
        var cookie = ExtractAntiforgeryCookie(pageResponse);
        var form = new System.Net.Http.StringContent(
            $"handle={Uri.EscapeDataString(handle)}&password={Uri.EscapeDataString(password)}" +
            $"&__RequestVerificationToken={Uri.EscapeDataString(token)}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        return await PostWithAntiforgeryAsync(client, "/login", form, cookie);
    }

    private static async Task<System.Net.Http.HttpResponseMessage> PostWithAntiforgeryAsync(
        System.Net.Http.HttpClient client, string url, System.Net.Http.HttpContent form, string? antiforgeryCookie)
    {
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
        {
            Content = form,
        };
        if (!string.IsNullOrEmpty(antiforgeryCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        }
        // The client follows the 302 redirect; the final response's RequestMessage.RequestUri is the
        // landing page (e.g. "/" on success, "/login?error=…" on failure).
        return await client.SendAsync(request);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        // The rendered form contains <input type="hidden" name="__RequestVerificationToken" value="…">.
        var marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(start != -1, "no antiforgery token in the rendered form");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    [Fact]
    public async Task RegisterEndpoint_CreatesAccount_SignsIn_RedirectsHome()
    {
        var response = await PostRegisterAsync("endpointuser", "s3cret-pw", "Endpoint User");
        // Success: 302 to the home page.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);

        // The account + actor were actually created.
        var store = _services.GetRequiredService<IUserAccountStore>();
        var account = await store.FindByUsernameAsync("endpointuser");
        Assert.NotNull(account);
        Assert.Equal(new Iri($"{Base}/ap/v1/u/endpointuser"), account!.ActorId);
    }

    [Fact]
    public async Task RegisterEndpoint_DuplicateHandle_RedirectsBackWithError()
    {
        await PostRegisterAsync("dupendpoint", "s3cret-pw");
        var response = await PostRegisterAsync("dupendpoint", "other-pw-1");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/register?error=", location);
        Assert.Contains("already taken", Uri.UnescapeDataString(location));
    }

    [Fact]
    public async Task RegisterEndpoint_ShortPassword_RedirectsBackWithError()
    {
        var response = await PostRegisterAsync("shortpwendpoint", "123");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/register?error=", location);
        Assert.Contains("at least 8 characters", Uri.UnescapeDataString(location));
    }

    [Fact]
    public async Task LoginEndpoint_ValidCredentials_SignsIn_RedirectsHome()
    {
        await PostRegisterAsync("loginendpoint", "s3cret-pw");
        var response = await PostLoginAsync("loginendpoint", "s3cret-pw");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LoginEndpoint_WrongPassword_RedirectsBackWithError()
    {
        await PostRegisterAsync("wrongpwendpoint", "s3cret-pw");
        var response = await PostLoginAsync("wrongpwendpoint", "not-the-password");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/login?error=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LoginEndpoint_UnknownHandle_RedirectsBackWithError()
    {
        var response = await PostLoginAsync("nobody-here-endpoint", "s3cret-pw");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/login?error=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LogoutEndpoint_SignsOut_RedirectsLogin()
    {
        // Sign in first (so there is a session to end), capturing the auth cookie the sign-in sets.
        await PostRegisterAsync("logoutendpoint", "s3cret-pw");
        var client = CreateClient();
        var pageResponse = await client.GetAsync("/login");
        var page = await pageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(page);
        var antiforgeryCookie = ExtractAntiforgeryCookie(pageResponse);
        var form = new System.Net.Http.StringContent(
            $"handle=logoutendpoint&password={Uri.EscapeDataString("s3cret-pw")}" +
            $"&__RequestVerificationToken={Uri.EscapeDataString(token)}",
            System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var signIn = await PostWithAntiforgeryAsync(client, "/login", form, antiforgeryCookie);
        Assert.Equal(HttpStatusCode.Found, signIn.StatusCode);

        var authCookies = signIn.Headers.GetValues("Set-Cookie")!
            .Where(c => c.StartsWith("iris.auth", System.StringComparison.Ordinal))
            .Select(c => c.Split(';', 2)[0].Trim())
            .ToList();
        Assert.True(authCookies.Count > 0, "sign-in did not set an auth cookie");

        // GET /logout with the auth cookie signs out and 302-redirects to /login.
        var logoutRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/logout");
        logoutRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", authCookies));
        var logout = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location!.OriginalString);
    }
}
