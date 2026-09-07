using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
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
/// Integration tests for slice 41.3 — <em>community membership requests (admin UI)</em>. They boot
/// the real app (in-memory persistence) in-process via <see cref="WebAppFactory"/> in a
/// <see cref="TestServer"/> and exercise the community join-request endpoints (list, accept, reject)
/// with Basic-auth creator verification.
/// </summary>
public sealed class CommunityJoinRequestIntegrationTests : IDisposable
{
    private const string Base = "https://web.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public CommunityJoinRequestIntegrationTests()
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
    public async Task ListJoinRequests_Returns404_WhenCommunityNotFound()
    {
        var client = _server.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/local/v1/c/nonexistent/requests");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListJoinRequests_Returns403_WhenNotCreator()
    {
        await SeedCommunityWithCreatorAsync("testcomm", "alice");

        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/local/v1/c/testcomm/requests");
        request.Headers.Authorization = BasicAuth("other", "otherpass123");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListJoinRequests_ReturnsEmptyArray_WhenNoRequests()
    {
        await SeedCommunityWithCreatorAsync("testcomm", "alice");

        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/local/v1/c/testcomm/requests");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", body);
    }

    [Fact]
    public async Task ListJoinRequests_ReturnsPendingRequests()
    {
        var communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");
        await AddJoinRequestAsync(communityIri, actorIri);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/local/v1/c/testcomm/requests");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(actorIri.Value, body);
    }

    [Fact]
    public async Task AcceptJoinRequest_AddsMemberAndRemovesRequest()
    {
        var communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");
        await AddJoinRequestAsync(communityIri, actorIri);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/accept/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var persistence = GetPersistence();
        Assert.True(await persistence.Communities.IsMemberAsync(communityIri, actorIri));
        Assert.False(await persistence.Communities.HasJoinRequestAsync(communityIri, actorIri));
    }

    [Fact]
    public async Task AcceptJoinRequest_Returns404_WhenNoRequest()
    {
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/accept/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectJoinRequest_RemovesRequestWithoutAddingMember()
    {
        var communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");
        await AddJoinRequestAsync(communityIri, actorIri);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/reject/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var persistence = GetPersistence();
        Assert.False(await persistence.Communities.IsMemberAsync(communityIri, actorIri));
        Assert.False(await persistence.Communities.HasJoinRequestAsync(communityIri, actorIri));
    }

    [Fact]
    public async Task RejectJoinRequest_Returns404_WhenNoRequest()
    {
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/reject/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("alice", "alice");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AcceptJoinRequest_Returns403_WhenNotCreator()
    {
        var communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");
        await AddJoinRequestAsync(communityIri, actorIri);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/accept/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("other", "otherpass123");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectJoinRequest_Returns403_WhenNotCreator()
    {
        var communityIri = new Iri($"{Base}/ap/v1/c/testcomm");
        var actorIri = new Iri($"{Base}/ap/v1/u/joiner");

        await SeedCommunityWithCreatorAsync("testcomm", "alice");
        await AddJoinRequestAsync(communityIri, actorIri);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/local/v1/c/testcomm/requests/reject/{actorIri.Value.TrimStart('/')}");
        request.Headers.Authorization = BasicAuth("other", "otherpass123");

        var response = await _server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private static AuthenticationHeaderValue BasicAuth(string user, string pass)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private async Task SeedCommunityWithCreatorAsync(string name, string creatorHandle)
    {
        var persistence = GetPersistence();
        var creatorIri = new Iri($"{Base}/ap/v1/u/{creatorHandle}");
        var communityIri = new Iri($"{Base}/ap/v1/c/{name}");

        var community = new KristofferStrube.ActivityStreams.Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [name],
            AttributedTo = [new KristofferStrube.ActivityStreams.Link { Href = creatorIri.Uri }],
        };

        await persistence.Communities.PutCommunityAsync(community);
    }

    private async Task AddJoinRequestAsync(Iri communityIri, Iri actorIri)
    {
        var persistence = GetPersistence();
        await persistence.Communities.AddJoinRequestAsync(communityIri, actorIri);
    }
}
