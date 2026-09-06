using System.Net;
using System.Net.Http;
using System.Text;
using Iris.Client;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 32.4b — the signed-in <em>compose</em> + <em>profile</em> product
/// screens. They boot the real app (in-memory persistence, the default) in-process via
/// <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and assert:
/// </summary>
/// <list type="bullet">
/// <item>Both <c>[Authorize]</c>-gated screens (<c>/compose</c> + <c>/profile</c>) redirect a signed-out
/// request to <c>/login</c> and return 200 with their static markup when signed in.</item>
/// <item>The signed-in <c>/profile</c> page renders the profile-header card + the outbox card's static
/// markup (the actor document + outbox items themselves are loaded by the interactive circuit).</item>
/// <item>The compose write path — posting a note through the session's <see cref="IActivityPubClient"/>
/// (the exact client the circuit uses, resolved from DI the same way
/// <see cref="Iris.Web.Accounts.ActorSessionAccessor"/> does) — lands the note in the actor's outbox,
/// readable over the ActivityPub HTTP surface: the vertical contract the profile page's "Your posts"
/// card renders against.</item>
/// </list>
/// <remarks>
/// The compose post itself is a Blazor circuit action (an <c>@onclick</c> that calls
/// <c>PostNoteAsync</c>), not an HTTP endpoint, so it is not directly drivable over
/// <see cref="TestServer"/> (per <c>docs/plans/production-app-feature-set.md</c> §1 — the click-through
/// is verified live via the MCP Playwright server). These integration tests lock the HTTP-observable
/// contract: the <c>[Authorize]</c> gating + static markup, and the compose→outbox data flow the
/// profile page depends on (the same <c>PostNoteAsync</c> call the compose page makes, asserted against
/// the same outbox the profile page reads).
/// </remarks>
public sealed class ComposeProfileIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Uri _dialBase;

    public ComposeProfileIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);

        // A holder for the TestServer's in-process handler, populated in the ctor once the TestServer
        // is constructed. The in-process actor-document fetcher dials through it so the inbound
        // signature validator resolves the local actor's key by fetching the actor document in-process
        // — the same same-origin path production takes when a local actor posts to its own outbox
        // (a real TCP dial to web.test.local:443 would fail in the test host).
        var handlerHolder = new TestServerHandlerHolder();

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in builder.Services)
                {
                    s.Add(descriptor);
                }

                // Override the remote actor-document fetcher (AddActivityPubServer registers it with
                // TryAddSingleton, so this later registration wins) with one whose client dials the
                // TestServer in-process via the held handler.
                s.AddSingleton<IActorDocumentFetcher>(sp =>
                {
                    var factory = sp.GetRequiredService<IActivityPubClientFactory>();
                    var instanceActor = sp.GetRequiredService<IOptions<ActivityPubServerOptions>>().Value.InstanceActorId
                        ?? new Iri($"{Base}/ap/v1/u/{WebAppFactory.SeedHandle}");
                    var client = factory.Create(
                        new ActivityPubClientOptions { ActorId = instanceActor, EnableRetry = false },
                        handlerHolder.Handler);
                    return new IrisActorDocumentFetcher(client, sp.GetRequiredService<RemoteActorCache>());
                });
            })
            .Configure(webApp =>
            {
                // The production pipeline — the same one ProductShellIntegrationTests boots, so the
                // [Authorize] gating + the ActivityPub endpoints are exercised exactly as in production.
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
        handlerHolder.Set(_server.CreateHandler());
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;
        _dialBase = new Uri(Base);
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // ------------------------------------------------------------------
    // The [Authorize]-gated compose + profile screens.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ComposePage_SignedOut_RedirectsToLogin()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/compose");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.PathAndQuery.Split('?', 2)[0]);
        Assert.Contains("ReturnUrl=%2Fcompose", response.Headers.Location.Query);
    }

    [Fact]
    public async Task ProfilePage_SignedOut_RedirectsToLogin()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.PathAndQuery.Split('?', 2)[0]);
        Assert.Contains("ReturnUrl=%2Fprofile", response.Headers.Location.Query);
    }

    [Fact]
    public async Task ComposePage_SignedIn_Returns200WithComposer()
    {
        var (client, authCookie) = await SignInAsync("composeuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/compose", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The composer's static markup (the card + content field + post button) is in the prerendered
        // page; the post itself is a circuit action and is not in the static HTML.
        Assert.Contains("Compose", html);
        Assert.Contains("compose-content", html);
        Assert.Contains("Post note", html);
    }

    [Fact]
    public async Task ProfilePage_SignedIn_Returns200WithProfileAndOutboxCards()
    {
        var (client, authCookie) = await SignInAsync("profileuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/profile", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The profile page's static markup: the page title, the outbox card's title + paged-collection
        // shell. The actor document (the ActorProfile header) and the outbox items are loaded by the
        // interactive circuit and are not in the static HTML.
        Assert.Contains("Your profile", html);
        Assert.Contains("Your posts", html);
        Assert.Contains("paged-collection", html);
    }

    // ------------------------------------------------------------------
    // The compose → outbox data flow (the contract the profile page reads against).
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostNoteViaSessionClient_LandsInActorOutbox_ReadableOverHttp()
    {
        // Register a real local account over the real HTTP surface and capture its auth cookie.
        var (client, authCookie) = await SignInAsync("poster", "s3cret-pw");

        // Resolve the session's ActivityPub client exactly the way ActorSessionAccessor does (the
        // factory signs with the shared key store, which holds the account's provisioned key); dial the
        // TestServer base. This is the same client object the interactive circuit uses to post.
        var accountStore = _services.GetRequiredService<Iris.Server.Data.Accounts.IUserAccountStore>();
        var account = await accountStore.FindByUsernameAsync("poster");
        Assert.NotNull(account);
        var actorId = account!.ActorId;
        // Dial the TestServer in-process: the TestServer handler routes the client's requests through
        // the in-process host (a fresh HttpClientHandler would attempt a real TCP connection to
        // web.test.local:443, which does not exist). This is the same in-process dial the circuit's
        // client uses when the app runs in a browser against the same origin.
        var factory = _services.GetRequiredService<IActivityPubClientFactory>();
        var sessionClient = factory.Create(
            new ActivityPubClientOptions { ActorId = actorId, DialBaseUri = _dialBase },
            _server.CreateHandler());

        // Post a note as the account (the same PostNoteAsync call the compose page makes, addressed to
        // the public collection so it lands in the outbox).
        const string noteContent = "hello from the 32.4b compose integration test";
        var result = await sessionClient.PostNoteAsync(actorId, noteContent);
        Assert.True(result.IsSuccess, $"the posted note should succeed; got HTTP {result.StatusCode}: {result.Body}");
        Assert.NotNull(result.MintedId);

        // Read the actor's outbox over the ActivityPub HTTP surface (the same collection the profile
        // page's "Your posts" card renders) and assert the posted note's content is present (the
        // outbox's items are full Create activities with the embedded note).
        var outboxRequest = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{account.Username}/outbox");
        outboxRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/activity+json"));
        var outboxResponse = await client.SendAsync(outboxRequest);
        Assert.Equal(HttpStatusCode.OK, outboxResponse.StatusCode);
        var outboxHtml = await outboxResponse.Content.ReadAsStringAsync();
        Assert.Contains(noteContent, outboxHtml);
    }

    // ------------------------------------------------------------------
    // Helpers: sign in over the real HTTP surface, then make an authenticated
    // request with the returned iris.auth cookie.
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

    /// <summary>
    /// Holds the TestServer's in-process <see cref="HttpMessageHandler"/> (available only once the
    /// TestServer is constructed) for the lazily-resolved actor-document fetcher to dial in-process.
    /// The raw handler is passed straight to <c>ActivityPubClientFactory.Create</c> (no wrapper), so the
    /// client pipeline sends each request exactly once through the in-process host.
    /// </summary>
    private sealed class TestServerHandlerHolder
    {
        private HttpMessageHandler? _handler;

        public void Set(HttpMessageHandler handler) => _handler = handler;

        public HttpMessageHandler Handler => _handler
            ?? throw new InvalidOperationException("The TestServer handler was not wired before the actor-document fetcher was resolved.");
    }
}
