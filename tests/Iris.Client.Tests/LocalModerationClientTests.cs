using System.Net;
using System.Text;
using Iris.Core;
using Iris.Client.Pipeline;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.LocalModerationClient"/>: the local, non-federated moderation
/// surface (a mute, F-07, and a relay subscription, F-06). Each call is a body-less, Basic-authenticated
/// POST to the acting actor's own instance (<c>{actorId}/mutes/{targetId}</c> or
/// <c>{actorId}/relays/{targetId}</c>), never a signed inbox delivery — so these tests assert the POST
/// path, the Basic-auth header, the absence of a body, and the propagated status code, without a server.
/// </summary>
public sealed class LocalModerationClientTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";
    private const string TargetIri = "https://b.domain.local/u/carol";
    private const string RelayIri = "https://relay.example/";

    private static HttpResponseMessage NoContent()
        => new(HttpStatusCode.NoContent);

    private static HttpResponseMessage Unauthorized()
        => new(HttpStatusCode.Unauthorized);

    private static FakeHttpHandler Ok()
        => new(NoContent());

    private static LocalModerationClient BuildClient(ProxyCredentials? credentials, FakeHttpHandler inner)
    {
        // The way the factory builds the client: a LocalAuthHandler over the transport when credentials
        // are configured, otherwise a handler-less client (only the explicit-credential overloads work).
        var localAuth = credentials is { } creds ? new LocalAuthHandler(creds, inner) : null;
        return new LocalModerationClient(localAuth);
    }

    [Fact]
    public async Task MuteAsync_PostsToMutesRoute_BasicAuth_NoBody()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.MuteAsync(new Iri(ActorIri), new Iri(TargetIri));

        Assert.Equal(204, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.NotNull(inner.LastRequest);
        Assert.Equal(HttpMethod.Post, inner.LastRequest!.Method);
        Assert.Equal($"{ActorIri}/mutes/{TargetIri}", inner.LastUri!.ToString());
        Assert.Equal("Basic", inner.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:bob-password")),
            inner.LastRequest.Headers.Authorization.Parameter);
        Assert.Empty(inner.LastBody);
    }

    [Fact]
    public async Task UnmuteAsync_PostsToMutesRoute_WithUnmuteQuery()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.UnmuteAsync(new Iri(ActorIri), new Iri(TargetIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{ActorIri}/mutes/{TargetIri}?unmute=true", inner.LastUri!.ToString());
    }

    [Fact]
    public async Task SubscribeRelayAsync_PostsToRelaysRoute_BasicAuth()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.SubscribeRelayAsync(new Iri(ActorIri), new Iri(RelayIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{ActorIri}/relays/{RelayIri}", inner.LastUri!.ToString());
        Assert.Equal("Basic", inner.LastRequest!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task UnsubscribeRelayAsync_PostsToRelaysRoute_WithUnsubscribeQuery()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.UnsubscribeRelayAsync(new Iri(ActorIri), new Iri(RelayIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{ActorIri}/relays/{RelayIri}?unsubscribe=true", inner.LastUri!.ToString());
    }

    [Fact]
    public async Task ExplicitCredentials_WithConfigured_DefaultWrapsSharedTransport()
    {
        // With a configured default handler, passing explicit credentials wraps that same (shared)
        // transport — so the fake still observes the request (no fresh network transport is built).
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.MuteAsync(
            new Iri(ActorIri), new Iri(TargetIri), new ProxyCredentials("bob", "bob-password"));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{ActorIri}/mutes/{TargetIri}", inner.LastUri!.ToString());
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:bob-password")),
            inner.LastRequest!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void NoCredentials_NoExplicitCredentials_Throws()
    {
        var client = BuildClient(credentials: null, inner: Ok());

        Assert.Throws<InvalidOperationException>(
            () => client.MuteAsync(new Iri(ActorIri), new Iri(TargetIri)).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task NonSuccessStatusCode_PropagatesFailure()
    {
        var inner = new FakeHttpHandler(Unauthorized());
        var client = BuildClient(new ProxyCredentials("bob", "wrong"), inner);

        var result = await client.MuteAsync(new Iri(ActorIri), new Iri(TargetIri));

        Assert.Equal(401, result.StatusCode);
        Assert.False(result.IsSuccess);
    }
}
