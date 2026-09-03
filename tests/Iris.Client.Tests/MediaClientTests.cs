using System.Net;
using System.Text;
using Iris.Client.Pipeline;
using Iris.Core;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.MediaClient"/>: the local, non-federated media-upload surface
/// (Phase 20.4 (a)). Each call is a Basic-authenticated <c>multipart/form-data</c> POST of the file's
/// bytes to the acting actor's own instance on the dedicated <c>/local/v1</c> tree
/// (<c>{host}/local/v1/{actor-segment}/media</c>), never a signed inbox delivery and never on the
/// <c>/ap/v1</c> AP tree (the file is not an ActivityStreams activity). These tests assert the POST path
/// (under <c>/local/v1</c>), the Basic-auth header, a multipart body carrying the file, the parsed
/// <see cref="Iris.Client.MediaUploadResult"/> (the same-origin media IRI from the 201 body), the
/// explicit-credential overload, and the no-credentials / non-success failures — without a server.
/// </summary>
public sealed class MediaClientTests
{
    private const string ActorIri = "https://b.domain.local/u/bob";
    private const string LocalActorBase = "https://b.domain.local/local/v1/u/bob";
    private const string MediaIriValue = "https://b.domain.local/ap/v1/media/0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static HttpResponseMessage Created()
        => new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"id":"{{MediaIriValue}}","type":"image/png","name":"cat.png"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static FakeHttpHandler Ok()
        => new(Created());

    private static MediaClient BuildClient(ProxyCredentials? credentials, FakeHttpHandler inner)
    {
        // The way the factory builds the client: a LocalAuthHandler over the transport when credentials
        // are configured, otherwise a handler-less client (only the explicit-credential overloads work).
        var localAuth = credentials is { } creds ? new LocalAuthHandler(creds, inner) : null;
        return new MediaClient(localAuth);
    }

    [Fact]
    public async Task UploadAsync_PostsToMediaRoute_BasicAuth_MultipartBody()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);
        byte[] bytes = [1, 2, 3, 4, 5];

        var result = await client.UploadAsync(new Iri(ActorIri), bytes, "image/png", "cat.png");

        Assert.Equal(new Iri(MediaIriValue), result.MediaIri);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("cat.png", result.FileName);

        Assert.NotNull(inner.LastRequest);
        Assert.Equal(HttpMethod.Post, inner.LastRequest!.Method);
        Assert.Equal($"{LocalActorBase}/media", inner.LastUri!.ToString());
        Assert.Equal("Basic", inner.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:bob-password")),
            inner.LastRequest.Headers.Authorization.Parameter);

        // The body is a multipart/form-data part carrying the file's bytes (content-type + file name).
        Assert.StartsWith("multipart/form-data", inner.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(bytes, inner.LastBody);
    }

    [Fact]
    public async Task UploadAsync_ParsesThe201BodyIntoTheResult()
    {
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.UploadAsync(new Iri(ActorIri), [1, 2], "image/png", "cat.png");

        // The media IRI is the same-origin /ap/v1/media/{id} the server minted (read from the 201 body).
        Assert.Equal(MediaIriValue, result.MediaIri.Value);
        Assert.EndsWith("/ap/v1/media/0a1b2c3d4e5f60718293a4b5c6d7e8f9", result.MediaIri.Value);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("cat.png", result.FileName);
    }

    [Fact]
    public async Task UploadAsync_ExplicitCredentials_WithConfigured_DefaultWrapsSharedTransport()
    {
        // With a configured default handler, passing explicit credentials wraps that same (shared)
        // transport — so the fake still observes the request (no fresh network transport is built).
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);

        var result = await client.UploadAsync(
            new Iri(ActorIri), [9, 9], "image/png", "cat.png", new ProxyCredentials("bob", "bob-password"));

        Assert.Equal(new Iri(MediaIriValue), result.MediaIri);
        Assert.Equal($"{LocalActorBase}/media", inner.LastUri!.ToString());
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:bob-password")),
            inner.LastRequest!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void NoCredentials_NoExplicitCredentials_Throws()
    {
        var client = BuildClient(credentials: null, inner: Ok());

        Assert.Throws<InvalidOperationException>(() =>
            client.UploadAsync(new Iri(ActorIri), [1], "image/png", "cat.png").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task NonSuccessStatusCode_ThrowsWithStatus()
    {
        var inner = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = BuildClient(new ProxyCredentials("bob", "wrong"), inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UploadAsync(new Iri(ActorIri), [1], "image/png", "cat.png"));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task ActorIriWithApPrefix_TargetsLocalTree_NotApTree()
    {
        // The real-world actor IRI carries the /ap/v1 route prefix (e.g. https://host/ap/v1/u/bob). The
        // media upload must target the /local/v1 tree on the same host (the actor's /u/bob segment
        // reused), never the /ap/v1 tree (the file is not an ActivityStreams activity).
        var inner = Ok();
        var client = BuildClient(new ProxyCredentials("bob", "bob-password"), inner);
        var apActorIri = new Iri("https://b.domain.local/ap/v1/u/bob");

        var result = await client.UploadAsync(apActorIri, [1], "image/png", "cat.png");

        Assert.Equal(new Iri(MediaIriValue), result.MediaIri);
        Assert.Equal("https://b.domain.local/local/v1/u/bob/media", inner.LastUri!.ToString());
        Assert.DoesNotContain("/ap/v1/u/bob/media", inner.LastUri!.ToString());
    }
}
