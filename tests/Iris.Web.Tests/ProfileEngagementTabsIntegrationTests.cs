using System.Net;
using System.Net.Http;
using System.Text;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using Iris.Web;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 41.2 — Profile engagement tabs (Replies + Likes). Boots the real app
/// (in-memory persistence) in a <see cref="TestServer"/> and asserts:
/// </summary>
/// <list type="bullet">
/// <item>The signed-in <c>/profile</c> page renders the tab bar (Your posts / Replies / Likes).</item>
/// <item>The inbox-filtering logic (the same predicates the Profile page's <c>ItemFilter</c> uses)
/// correctly classifies <c>Create</c>-with-<c>InReplyTo</c> as a reply and <c>Like</c> as a like,
/// while excluding other activity types.</item>
/// </list>
public sealed class ProfileEngagementTabsIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public ProfileEngagementTabsIntegrationTests()
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
    // Tab bar markup.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProfilePage_SignedIn_ShowsTabBar()
    {
        var (client, authCookie) = await SignInAsync("tabuser", "s3cret-pw");
        var response = await GetWithAuthAsync(client, "/profile", authCookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The tab bar is server-rendered (static prerender); the tab content is loaded by the circuit.
        Assert.Contains("tab-bar", html);
        Assert.Contains("Your posts", html);
        Assert.Contains("Replies", html);
        Assert.Contains("Likes", html);
    }

    // ------------------------------------------------------------------
    // Inbox filtering logic (the same predicates Profile.razor's ItemFilter uses).
    // ------------------------------------------------------------------

    [Fact]
    public async Task InboxFilter_Reply_DetectsCreateWithInReplyTo()
    {
        var (client, authCookie) = await SignInAsync("replyuser", "s3cret-pw");
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("replyuser", CancellationToken.None);
        Assert.NotNull(account);

        // Seed: a reply (Create with InReplyTo), a like, a follow, a plain Create (no InReplyTo).
        var reply = CreateReplyActivity(account!.ActorId, DateTime.UtcNow - TimeSpan.FromHours(1));
        var like = CreateLikeActivity("https://remote.example/bob", account.ActorId, DateTime.UtcNow - TimeSpan.FromHours(2));
        var follow = CreateFollowActivity("https://remote.example/charlie", account.ActorId, DateTime.UtcNow - TimeSpan.FromHours(3));
        var plainCreate = CreatePlainActivity("https://remote.example/dave", account.ActorId, DateTime.UtcNow - TimeSpan.FromHours(4));

        await persistence.Activities.AddToInboxAsync(account.ActorId, reply, CancellationToken.None);
        await persistence.Activities.AddToInboxAsync(account.ActorId, like, CancellationToken.None);
        await persistence.Activities.AddToInboxAsync(account.ActorId, follow, CancellationToken.None);
        await persistence.Activities.AddToInboxAsync(account.ActorId, plainCreate, CancellationToken.None);

        var inbox = await persistence.Activities.GetInboxAsync(account.ActorId, CancellationToken.None);
        Assert.Equal(4, inbox.Count);

        var replies = inbox.Where(IsReply).ToList();
        var likes = inbox.Where(IsLike).ToList();

        Assert.Single(replies);
        Assert.IsType<Create>(replies[0]);
        Assert.Single(likes);
        Assert.IsType<Like>(likes[0]);
    }

    [Fact]
    public async Task InboxFilter_EmptyInbox_ReturnsEmpty()
    {
        var (client, authCookie) = await SignInAsync("emptyuser", "s3cret-pw");
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("emptyuser", CancellationToken.None);
        Assert.NotNull(account);

        var inbox = await persistence.Activities.GetInboxAsync(account!.ActorId, CancellationToken.None);
        Assert.DoesNotContain(inbox, IsReply);
        Assert.DoesNotContain(inbox, IsLike);
    }

    [Fact]
    public async Task InboxFilter_CreateWithoutInReplyTo_IsNotAReply()
    {
        var (client, authCookie) = await SignInAsync("noiruser", "s3cret-pw");
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("noiruser", CancellationToken.None);
        Assert.NotNull(account);

        // A Create with no InReplyTo (a plain mention/broadcast, not a reply).
        var plainCreate = CreatePlainActivity("https://remote.example/eve", account!.ActorId, DateTime.UtcNow);
        await persistence.Activities.AddToInboxAsync(account.ActorId, plainCreate, CancellationToken.None);

        var inbox = await persistence.Activities.GetInboxAsync(account.ActorId, CancellationToken.None);
        Assert.DoesNotContain(inbox, IsReply);
        // But it IS a Create (would show in the general notifications feed).
        Assert.Contains(inbox, item => item is Create);
    }

    [Fact]
    public async Task InboxFilter_Like_OnLocalObject_IsDetected()
    {
        var (client, authCookie) = await SignInAsync("likeuser", "s3cret-pw");
        var persistence = _services.GetRequiredService<IPersistenceProvider>();
        var accounts = _services.GetRequiredService<IUserAccountStore>();
        var account = await accounts.FindByUsernameAsync("likeuser", CancellationToken.None);
        Assert.NotNull(account);

        // Two likes from different remote actors.
        var like1 = CreateLikeActivity("https://remote.example/alice", account!.ActorId, DateTime.UtcNow - TimeSpan.FromHours(1));
        var like2 = CreateLikeActivity("https://remote.example/bob", account.ActorId, DateTime.UtcNow - TimeSpan.FromHours(2));
        await persistence.Activities.AddToInboxAsync(account.ActorId, like1, CancellationToken.None);
        await persistence.Activities.AddToInboxAsync(account.ActorId, like2, CancellationToken.None);

        var inbox = await persistence.Activities.GetInboxAsync(account.ActorId, CancellationToken.None);
        var likes = inbox.Where(IsLike).ToList();
        Assert.Equal(2, likes.Count);
    }

    // ------------------------------------------------------------------
    // Filter predicates (mirror of Profile.razor's IsReply / IsLike).
    // ------------------------------------------------------------------

    private static bool IsReply(IObjectOrLink item)
    {
        if (item is not Activity activity)
        {
            return false;
        }

        var type = activity.Type?.FirstOrDefault() ?? string.Empty;
        if (type != "Create")
        {
            return false;
        }

        var embedded = activity.Object?.FirstOrDefault() as IObject;
        return embedded?.InReplyTo?.Any() == true;
    }

    private static bool IsLike(IObjectOrLink item)
    {
        if (item is not Activity activity)
        {
            return false;
        }

        return activity.Type?.FirstOrDefault() == "Like";
    }

    // ------------------------------------------------------------------
    // Activity builders.
    // ------------------------------------------------------------------

    private static Create CreateReplyActivity(Iri recipientIri, DateTime published)
    {
        var note = new Note
        {
            Id = $"https://remote.example/notes/{Guid.NewGuid():N}",
            Content = ["a reply to your post"],
            InReplyTo = [new Link { Href = recipientIri.Uri }],
            Published = published,
        };

        return new Create
        {
            Id = $"https://remote.example/act/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri("https://remote.example/replier") }],
            Object = [note],
            Published = published,
        };
    }

    private static Like CreateLikeActivity(string likerIri, Iri targetIri, DateTime published)
    {
        return new Like
        {
            Id = $"https://remote.example/act/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(likerIri) }],
            Object = [new Link { Href = targetIri.Uri }],
            Published = published,
        };
    }

    private static Follow CreateFollowActivity(string actorIri, Iri targetIri, DateTime published)
    {
        return new Follow
        {
            Id = $"https://remote.example/act/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri) }],
            Object = [new Link { Href = targetIri.Uri }],
            Published = published,
        };
    }

    private static Create CreatePlainActivity(string actorIri, Iri recipientIri, DateTime published)
    {
        var note = new Note
        {
            Id = $"https://remote.example/notes/{Guid.NewGuid():N}",
            Content = ["a plain note, not a reply"],
            Published = published,
        };

        return new Create
        {
            Id = $"https://remote.example/act/{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri) }],
            Object = [note],
            Published = published,
        };
    }

    // ------------------------------------------------------------------
    // Auth helpers.
    // ------------------------------------------------------------------

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
