using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using Iris.Web;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 41.1 — notification read-state + unread badge. Boots the real app
/// (in-memory persistence) in a <see cref="TestServer"/> and exercises the
/// <c>POST /local/v1/notifications/read</c> and <c>GET /local/v1/notifications/unread-count</c>
/// endpoints, plus the in-process <c>NotificationService</c>.
/// </summary>
public sealed class NotificationReadStateIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public NotificationReadStateIntegrationTests()
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
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
                    WebAppFactory.MapAuthEndpoints(endpoints);
                    WebAppFactory.MapNotificationEndpoints(endpoints);
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
    // Unauthenticated access.
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnreadCount_Unauthenticated_RedirectsToLogin()
    {
        var client = _server.CreateClient();
        var response = await client.GetAsync("/local/v1/notifications/unread-count");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.PathAndQuery.Split('?', 2)[0]);
    }

    [Fact]
    public async Task MarkRead_Unauthenticated_RedirectsToLogin()
    {
        var client = _server.CreateClient();
        var response = await client.PostAsync("/local/v1/notifications/read", new StringContent(""));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.PathAndQuery.Split('?', 2)[0]);
    }

    // ------------------------------------------------------------------
    // Unread count (no notifications yet).
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnreadCount_NoInboxItems_ReturnsZero()
    {
        var (client, authCookie) = await SignInAsync("notifuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/local/v1/notifications/unread-count", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("unread").GetInt32());
    }

    // ------------------------------------------------------------------
    // Unread count (with inbox items).
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnreadCount_WithInboxItems_ReturnsCount()
    {
        var (client, authCookie) = await SignInAsync("notifuser2", "s3cret-pw");

        // Seed two inbox items (activities with Published timestamps).
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("notifuser2", CancellationToken.None);
        Assert.NotNull(account);

        var activity1 = CreateFollowActivity("https://remote.example/bob", account!.ActorId, DateTime.UtcNow - TimeSpan.FromHours(2));
        var activity2 = CreateFollowActivity("https://remote.example/charlie", account.ActorId, DateTime.UtcNow - TimeSpan.FromHours(1));

        await persistence.Activities.AddToInboxAsync(account.ActorId, activity1, CancellationToken.None);
        await persistence.Activities.AddToInboxAsync(account.ActorId, activity2, CancellationToken.None);

        var response = await GetWithAuthAsync(client, "/local/v1/notifications/unread-count", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("unread").GetInt32());
    }

    // ------------------------------------------------------------------
    // Mark all as read.
    // ------------------------------------------------------------------

    [Fact]
    public async Task MarkRead_SetsCursorAndResetsCount()
    {
        var (client, authCookie) = await SignInAsync("notifuser3", "s3cret-pw");

        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("notifuser3", CancellationToken.None);
        Assert.NotNull(account);

        var activity = CreateFollowActivity("https://remote.example/dave", account!.ActorId, DateTime.UtcNow - TimeSpan.FromHours(1));
        await persistence.Activities.AddToInboxAsync(account.ActorId, activity, CancellationToken.None);

        // Verify unread count is 1 before marking read.
        var beforeResponse = await GetWithAuthAsync(client, "/local/v1/notifications/unread-count", authCookie);
        var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
        using var beforeDoc = JsonDocument.Parse(beforeBody);
        Assert.Equal(1, beforeDoc.RootElement.GetProperty("unread").GetInt32());

        // Mark all as read.
        var markResponse = await PostWithAuthAsync(client, "/local/v1/notifications/read", authCookie);
        Assert.Equal(HttpStatusCode.OK, markResponse.StatusCode);

        var markBody = await markResponse.Content.ReadAsStringAsync();
        using var markDoc = JsonDocument.Parse(markBody);
        Assert.Equal(0, markDoc.RootElement.GetProperty("unread").GetInt32());

        // Verify the cursor was advanced.
        var updatedAccount = await accounts.FindByUsernameAsync("notifuser3", CancellationToken.None);
        Assert.NotNull(updatedAccount);
        Assert.NotNull(updatedAccount!.NotificationsReadAt);

        // Verify unread count is now 0.
        var afterResponse = await GetWithAuthAsync(client, "/local/v1/notifications/unread-count", authCookie);
        var afterBody = await afterResponse.Content.ReadAsStringAsync();
        using var afterDoc = JsonDocument.Parse(afterBody);
        Assert.Equal(0, afterDoc.RootElement.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task MarkRead_OnlyCountsItemsAfterCursor()
    {
        var (client, authCookie) = await SignInAsync("notifuser4", "s3cret-pw");

        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("notifuser4", CancellationToken.None);
        Assert.NotNull(account);

        // Add an old item (2h ago).
        var oldActivity = CreateFollowActivity("https://remote.example/old", account!.ActorId, DateTime.UtcNow - TimeSpan.FromHours(2));
        await persistence.Activities.AddToInboxAsync(account.ActorId, oldActivity, CancellationToken.None);

        // Mark read (sets cursor to now).
        await PostWithAuthAsync(client, "/local/v1/notifications/read", authCookie);

        // Add a new item (just now, after the cursor).
        var newActivity = CreateFollowActivity("https://remote.example/new", account.ActorId, DateTime.UtcNow);
        await persistence.Activities.AddToInboxAsync(account.ActorId, newActivity, CancellationToken.None);

        // Unread count should be 1 (only the new item).
        var response = await GetWithAuthAsync(client, "/local/v1/notifications/unread-count", authCookie);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("unread").GetInt32());
    }

    // ------------------------------------------------------------------
    // In-process NotificationService.
    // ------------------------------------------------------------------

    [Fact]
    public async Task NotificationService_GetUnreadCount_WorksInProcess()
    {
        var registration = _services.GetRequiredService<Iris.Web.Accounts.RegistrationService>();
        var result = await registration.RegisterAsync("svcuser", "s3cret-pw", null);
        Assert.True(result.Succeeded);

        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var account = result.Account!;

        var activity = CreateFollowActivity("https://remote.example/eve", account.ActorId, DateTime.UtcNow - TimeSpan.FromMinutes(5));
        await persistence.Activities.AddToInboxAsync(account.ActorId, activity, CancellationToken.None);

        var service = _services.GetRequiredService<NotificationService>();
        var count = await service.GetUnreadCountAsync(account.Id, CancellationToken.None);
        Assert.Equal(1, count);

        await service.MarkAllReadAsync(account.Id, CancellationToken.None);
        count = await service.GetUnreadCountAsync(account.Id, CancellationToken.None);
        Assert.Equal(0, count);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static Follow CreateFollowActivity(string actorIri, Iri recipientIri, DateTime published)
    {
        return new Follow
        {
            Id = $"https://remote.example/act/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri) }],
            Object = [new Link { Href = recipientIri.Uri }],
            Published = published,
        };
    }

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

    private static async Task<HttpResponseMessage> GetWithAuthAsync(HttpClient client, string url, string authCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", authCookie);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task<HttpResponseMessage> PostWithAuthAsync(HttpClient client, string url, string authCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(""),
        };
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
