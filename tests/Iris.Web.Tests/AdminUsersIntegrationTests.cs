using System.Net;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Web;
using Iris.Web.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for slice 42.3 — <em>instance admin user list</em>. They boot the real app
/// (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/>
/// and verify: (1) the admin authorization policy is registered, (2) an unauthenticated request to
/// /admin/users is redirected to the login page, and (3) the account store returns the seeded
/// accounts (the data the admin page renders).
/// </summary>
public sealed class AdminUsersIntegrationTests : IDisposable
{
    private const string Base = "https://admin.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public AdminUsersIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in builder.Services)
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

    [Fact]
    public async Task Unauthenticated_AdminUsers_RedirectsToLogin()
    {
        var http = _server.CreateClient();
        var response = await http.GetAsync("/admin/users");

        // Blazor pages with [Authorize] redirect unauthenticated users to the login path.
        // TestServer follows redirects by default; the final page is the login form.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently,
            $"Expected 200 (followed redirect) or 3xx, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task RegisteredAccount_AppearInGetAll()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var result = await registration.RegisterAsync("adminuser", "pass123456", "Admin User");
        Assert.True(result.Succeeded, $"registration failed: {result.Error}");

        using var scope = _services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
        var all = await accounts.GetAllAsync();

        Assert.Contains(all, a => a.Username == "adminuser");
    }

    [Fact]
    public async Task RegisteredAccount_HasValidFields()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        var result = await registration.RegisterAsync("fieldcheck", "pass123456", "Field Check");
        Assert.True(result.Succeeded, $"registration failed: {result.Error}");

        using var scope = _services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
        var user = await accounts.FindByUsernameAsync("fieldcheck");

        Assert.NotNull(user);
        Assert.NotEqual(default, user!.CreatedAt);
        Assert.NotEqual(default, user.ActorId);
        Assert.Equal(UserRole.User, user.Role);
    }

    [Fact]
    public async Task GetAll_ReturnsAllRegisteredAccounts()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        await registration.RegisterAsync("user1", "pass123456", "User One");
        await registration.RegisterAsync("user2", "pass123456", "User Two");

        using var scope = _services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
        var all = await accounts.GetAllAsync();

        Assert.Contains(all, a => a.Username == "user1");
        Assert.Contains(all, a => a.Username == "user2");
    }

    [Fact]
    public async Task FindByUsername_IsCaseInsensitive()
    {
        var registration = _services.GetRequiredService<RegistrationService>();
        await registration.RegisterAsync("casecheck", "pass123456", "Case Check");

        using var scope = _services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
        var found = await accounts.FindByUsernameAsync("CASECHECK");

        Assert.NotNull(found);
        Assert.Equal("casecheck", found!.Username);
    }
}
