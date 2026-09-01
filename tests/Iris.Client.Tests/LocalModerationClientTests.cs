using System.Net;
using System.Text;
using Iris.Core;
using Iris.Client.Pipeline;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.LocalModerationClient"/>: the local, non-federated moderation
/// surface (a mute, F-07, and a relay subscription, F-06). Each call is a body-less, Basic-authenticated
/// POST to the acting actor's own instance on the dedicated <c>/local/v1</c> tree
/// (<c>{host}/local/v1/{actor-segment}/mutes/{targetId}</c> or <c>.../relays/{targetId}</c>), never a
/// signed inbox delivery and never on the <c>/ap/v1</c> AP tree — so these tests assert the POST path
/// (under <c>/local/v1</c>), the Basic-auth header, the absence of a body, and the propagated status
/// code, without a server.
/// </summary>
public sealed class LocalModerationClientTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";
    private const string TargetIri = "https://b.domain.local/u/carol";
    private const string RelayIri = "https://relay.example/";

    // The local-moderation write for this actor: the host (b.domain.local) + /local/v1 tree + the
    // actor's /u/bob segment (derived from the actor IRI's path, replacing its route prefix with the
    // local tree).
    private const string LocalActorBase = "https://b.domain.local/local/v1/u/bob";

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
        Assert.Equal($"{LocalActorBase}/mutes/{TargetIri}", inner.LastUri!.ToString());
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
        Assert.Equal($"{LocalActorBase}/mutes/{TargetIri}?unmute=true", inner.LastUri!.ToString());
    }

    [Fact]
    public async Task SubscribeRelayAsync_PostsToRelaysRoute_BasicAuth()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.SubscribeRelayAsync(new Iri(ActorIri), new Iri(RelayIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{LocalActorBase}/relays/{RelayIri}", inner.LastUri!.ToString());
        Assert.Equal("Basic", inner.LastRequest!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task UnsubscribeRelayAsync_PostsToRelaysRoute_WithUnsubscribeQuery()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.UnsubscribeRelayAsync(new Iri(ActorIri), new Iri(RelayIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal($"{LocalActorBase}/relays/{RelayIri}?unsubscribe=true", inner.LastUri!.ToString());
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
        Assert.Equal($"{LocalActorBase}/mutes/{TargetIri}", inner.LastUri!.ToString());
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

    [Fact]
    public async Task ActorIriWithApPrefix_TargetsLocalTree_NotApTree()
    {
        // The real-world actor IRI carries the /ap/v1 route prefix (e.g. https://host/ap/v1/u/bob). The
        // local-moderation write must target the /local/v1 tree on the same host (the actor's /u/bob
        // segment reused), never the /ap/v1 tree. This is the AP-native rework: mute/relay are non-AP
        // local capabilities, so the write is off the AP route tree.
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);
        var apActorIri = new Iri("https://b.domain.local/ap/v1/u/bob");

        var result = await client.MuteAsync(apActorIri, new Iri(TargetIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal("https://b.domain.local/local/v1/u/bob/mutes/" + TargetIri, inner.LastUri!.ToString());
        Assert.DoesNotContain("/ap/v1/u/bob/mutes", inner.LastUri!.ToString());
    }

    [Fact]
    public async Task CommunityIriWithApPrefix_TargetsLocalTreeCommunityMute()
    {
        // A community actor IRI (https://host/ap/v1/c/iris) maps to /local/v1/c/iris for the community
        // mute write (the /c/ segment reused under the local tree). MuteAsync is generic over the actor
        // IRI (person or community); the server routes on the /c/{name} path.
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("iris", "iris-password"), inner);
        var communityIri = new Iri("https://b.domain.local/ap/v1/c/iris");

        var result = await client.MuteAsync(communityIri, new Iri(TargetIri));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal("https://b.domain.local/local/v1/c/iris/mutes/" + TargetIri, inner.LastUri!.ToString());
        Assert.DoesNotContain("/ap/v1/c/iris/mutes", inner.LastUri!.ToString());
    }
}
