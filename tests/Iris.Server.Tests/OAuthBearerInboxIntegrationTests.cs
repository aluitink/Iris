using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 59.2 (F-20) integration tests: the OAuth2 Bearer path for inbox delivery. Proves that:
/// (1) a valid Bearer token authenticates an unsigned inbox POST (no <c>Signature</c> header);
/// (2) an unknown Bearer token is rejected with 401;
/// (3) the actor document advertises <c>endpoints.oauthAuthorizationEndpoint</c> and
/// <c>endpoints.oauthTokenEndpoint</c> (the discovery surface a real client uses to walk the OAuth2 flow).
/// </summary>
public sealed class OAuthBearerInboxIntegrationTests : IDisposable
{
    private const string Host = "oauth.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly InMemoryOAuthTokenStore _tokenStore;

    public OAuthBearerInboxIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _tokenStore = new InMemoryOAuthTokenStore();
        TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = _persistence,
            RegisterLocalKey = false,
            Fetcher = new NullActorDocumentFetcher(),
            ExtraServices = s => s.AddSingleton<IOAuthTokenStore>(_tokenStore),
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{Host}"),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A valid Bearer token authenticates an unsigned inbox POST -------------------------------

    [Fact]
    public async Task Inbox_AcceptsValidBearerToken_WithoutSignature()
    {
        // Issue a Bearer token for the local actor (the result of the /ap/v1/oauth2/token exchange).
        const string token = "test-bearer-token-abc";
        await _tokenStore.StoreTokenAsync(token, ActorIri);

        // Build a Follow activity (a remote actor following the local actor).
        var followJson = """
        {
            "type": "Follow",
            "id": "https://remote.example.org/ap/v1/activities/follow-1",
            "actor": "https://remote.example.org/ap/v1/u/bob",
            "object": "https://oauth.domain.local/ap/v1/u/alice"
        }
        """;

        // POST to the inbox with Authorization: Bearer (no Signature header).
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new StringContent(followJson, System.Text.Encoding.UTF8, "application/activity+json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(request);

        // 202 Accepted: the Bearer token authenticated the request and the Follow was processed.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // --- An unknown Bearer token is rejected with 401 -------------------------------------------

    [Fact]
    public async Task Inbox_RejectsUnknownBearerToken_With401()
    {
        const string token = "unknown-bearer-token";

        var followJson = """
        {
            "type": "Follow",
            "id": "https://remote.example.org/ap/v1/activities/follow-2",
            "actor": "https://remote.example.org/ap/v1/u/bob",
            "object": "https://oauth.domain.local/ap/v1/u/alice"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new StringContent(followJson, System.Text.Encoding.UTF8, "application/activity+json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- A revoked Bearer token is rejected with 401 --------------------------------------------

    [Fact]
    public async Task Inbox_RejectsRevokedBearerToken_With401()
    {
        const string token = "to-be-revoked-token";
        await _tokenStore.StoreTokenAsync(token, ActorIri);
        await _tokenStore.RevokeTokenAsync(token);

        var followJson = """
        {
            "type": "Follow",
            "id": "https://remote.example.org/ap/v1/activities/follow-3",
            "actor": "https://remote.example.org/ap/v1/u/bob",
            "object": "https://oauth.domain.local/ap/v1/u/alice"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new StringContent(followJson, System.Text.Encoding.UTF8, "application/activity+json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- An unsigned POST with no Authorization header is rejected with 401 -----------------------

    [Fact]
    public async Task Inbox_RejectsUnsignedRequest_With401()
    {
        var followJson = """
        {
            "type": "Follow",
            "id": "https://remote.example.org/ap/v1/activities/follow-4",
            "actor": "https://remote.example.org/ap/v1/u/bob",
            "object": "https://oauth.domain.local/ap/v1/u/alice"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new StringContent(followJson, System.Text.Encoding.UTF8, "application/activity+json"),
        };
        // No Authorization header, no Signature header.

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The actor document advertises the OAuth2 endpoints --------------------------------------

    [Fact]
    public async Task ActorDocument_AdvertisesOauthEndpoints()
    {
        var response = await _http.GetAsync($"/ap/v1/u/{Handle}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = doc.RootElement.GetProperty("endpoints");

        Assert.True(endpoints.TryGetProperty("oauthAuthorizationEndpoint", out var authEndpoint));
        Assert.Equal($"https://{Host}/ap/v1/oauth2/authorize", authEndpoint.GetString());

        Assert.True(endpoints.TryGetProperty("oauthTokenEndpoint", out var tokenEndpoint));
        Assert.Equal($"https://{Host}/ap/v1/oauth2/token", tokenEndpoint.GetString());
    }

    // --- A Bearer-authenticated inbox POST records the follow edge -------------------------------

    [Fact]
    public async Task Inbox_BearerAuthenticated_FollowRecordsEdge()
    {
        const string token = "follow-bearer-token";
        await _tokenStore.StoreTokenAsync(token, ActorIri);

        var remoteActorIri = new Iri("https://remote.example.org/ap/v1/u/bob");
        var followJson = """
        {
            "type": "Follow",
            "id": "https://remote.example.org/ap/v1/activities/follow-5",
            "actor": "https://remote.example.org/ap/v1/u/bob",
            "object": "https://oauth.domain.local/ap/v1/u/alice"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/ap/v1/u/{Handle}/inbox")
        {
            Content = new StringContent(followJson, System.Text.Encoding.UTF8, "application/activity+json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The Follow edge is recorded: remote actor → local actor.
        var isFollowing = await _persistence.Follows.IsFollowingAsync(remoteActorIri, ActorIri);
        Assert.True(isFollowing);
    }
}

/// <summary>
/// A no-op <see cref="IActorDocumentFetcher"/> that always returns null. Used in tests where the
/// server's outbound delivery (e.g. the <c>Accept</c> a <see cref="FollowActivityHandler"/> schedules)
/// would otherwise attempt a real HTTP fetch of a nonexistent remote actor. Returning null makes
/// <see cref="Iris.Server.Delivery.DeliveryService"/> fall back to the per-actor inbox convention
/// (<c>{actorIri}/inbox</c>) and enqueue the delivery without any network call.
/// </summary>
internal sealed class NullActorDocumentFetcher : IActorDocumentFetcher
{
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        => Task.FromResult<Actor?>(null);
}
