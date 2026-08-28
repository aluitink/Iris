using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Core;
using Xunit;

namespace Iris.Client.Tests;

/// <summary>
/// A <see cref="HttpMessageHandler"/> double that returns a scripted sequence of
/// <see cref="HttpResponseMessage"/> (or throws a scripted exception) per request, recording each
/// call. Used to drive <see cref="Iris.Client.RetryHandler"/> and <see cref="Iris.Client.JsonLdHandler"/>
/// deterministically.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _script;
    private readonly List<HttpRequestMessage> _requests = [];

    public ScriptedHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] steps)
    {
        _script = new(steps);
    }

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public int CallCount => _requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        if (_script.Count == 0)
        {
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }

        return _script.Dequeue()(request);
    }

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Ok(string json)
        => _ => Task.FromResult(Response(HttpStatusCode.OK, json));

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Status(HttpStatusCode code, string json = "{}")
        => _ => Task.FromResult(Response(code, json));

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> StatusWithRetryAfter(HttpStatusCode code, int seconds, string json = "{}")
    {
        var response = Response(code, json);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return _ => Task.FromResult(response);
    }

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> NetworkFailure()
        => _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));

    private static HttpResponseMessage Response(HttpStatusCode code, string json)
        => new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
        };
}

public class JsonLdHandlerTests
{
    private static readonly string ActorJson = """
        {"id":"https://a.domain.local/users/alice","type":"Person","name":"Alice"}
        """;

    [Fact]
    public async Task Get_SetsAcceptWithBothMediaTypes()
    {
        var inner = new ScriptedHandler(ScriptedHandler.Ok(ActorJson));
        using var client = new HttpClient(new JsonLdHandler(inner));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), CancellationToken.None);

        var accept = string.Join(",", inner.Requests[0].Headers.Accept.Select(a => a.MediaType));
        Assert.Equal("application/activity+json,application/ld+json", accept);
    }

    [Fact]
    public async Task Post_WithoutContentType_SetsActivityJson()
    {
        var inner = new ScriptedHandler(ScriptedHandler.Ok("{}"));
        using var client = new HttpClient(new JsonLdHandler(inner));

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://remote.example/inbox"))
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("application/activity+json", inner.Requests[0].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Post_WithExplicitContentType_PreservesIt()
    {
        var inner = new ScriptedHandler(ScriptedHandler.Ok("{}"));
        using var client = new HttpClient(new JsonLdHandler(inner));

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://remote.example/inbox"))
        {
            Content = content,
        };
        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("application/ld+json", inner.Requests[0].Content!.Headers.ContentType!.MediaType);
    }
}

public class RetryHandlerTests
{
    private static readonly string ActorJson = """
        {"id":"https://a.domain.local/users/alice","type":"Person","name":"Alice"}
        """;

    private static CancellationToken Ct => CancellationToken.None;

    private static HttpClient Client(RetryHandler handler) => new(handler);

    [Fact]
    public async Task Get_RetriesOn503_ThenSucceeds()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Ok(ActorJson));
        using var client = Client(new RetryHandler(3, inner, (d, t) => Task.CompletedTask, new Random(1)));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Get_RetriesOn429_HonorsRetryAfter()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.StatusWithRetryAfter(HttpStatusCode.TooManyRequests, 7),
            ScriptedHandler.Ok(ActorJson));
        TimeSpan? observedDelay = null;
        using var client = Client(new RetryHandler(3, inner, (delay, token) =>
        {
            observedDelay = delay;
            return Task.CompletedTask;
        }, new Random(1)));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), Ct);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(7), observedDelay);
    }

    [Fact]
    public async Task Get_NotRetriedOn404()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.NotFound),
            ScriptedHandler.Ok(ActorJson));
        using var client = Client(new RetryHandler(3, inner, (d, t) => Task.CompletedTask, new Random(1)));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/missing")), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Post_NeverRetried_EvenOn503()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Ok("{}"));
        using var client = Client(new RetryHandler(3, inner, (d, t) => Task.CompletedTask, new Random(1)));

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://remote.example/inbox"))
        {
            Content = new ByteArrayContent([1]),
        };
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Get_GivesUpAfterMaxAttempts_ReturnsLastResponse()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.InternalServerError),
            ScriptedHandler.Status(HttpStatusCode.InternalServerError),
            ScriptedHandler.Status(HttpStatusCode.InternalServerError),
            ScriptedHandler.Ok(ActorJson));
        using var client = Client(new RetryHandler(3, inner, (d, t) => Task.CompletedTask, new Random(1)));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, inner.CallCount);
    }

    [Fact]
    public async Task Get_RetriesOnNetworkFailure_ThenSucceeds()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.NetworkFailure(),
            ScriptedHandler.Ok(ActorJson));
        using var client = Client(new RetryHandler(3, inner, (d, t) => Task.CompletedTask, new Random(1)));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Get_BackoffIsExponentialWithJitter()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Ok(ActorJson));
        var delays = new List<TimeSpan>();
        using var client = Client(new RetryHandler(3, inner, (delay, token) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }, new Random(42)));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("https://remote.example/actors/alice")), Ct);

        Assert.Equal(2, delays.Count);
        // base = 250ms; attempt 1 -> [250, 500); attempt 2 -> [500, 1000).
        Assert.InRange(delays[0], TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
        Assert.InRange(delays[1], TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task Factory_RetryEnabledByDefault_RetriesTransientGet()
    {
        var keyStore = new InMemoryKeyStore();
        using var keyPair = KeyPairGenerator.GenerateRsa(new Iri("https://a.domain.local/users/retry#key-1"));
        keyStore.PutKey(keyPair);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var actorIri = new Iri("https://a.domain.local/users/retry");
        keyProvider.RegisterKey(actorIri, keyPair.KeyId);

        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Ok(ActorJson));
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var options = new ActivityPubClientOptions { ActorId = actorIri };
        using var client = factory.Create(options, inner);

        var @object = await client.GetObjectAsync(new Iri("https://remote.example/actors/alice"), Ct);

        Assert.NotNull(@object);
        // 503 then 200 -> two transport calls.
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Factory_RetryDisabled_DoesNotRetry()
    {
        var keyStore = new InMemoryKeyStore();
        using var keyPair = KeyPairGenerator.GenerateRsa(new Iri("https://a.domain.local/users/noretry#key-1"));
        keyStore.PutKey(keyPair);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var actorIri = new Iri("https://a.domain.local/users/noretry");
        keyProvider.RegisterKey(actorIri, keyPair.KeyId);

        var inner = new ScriptedHandler(
            ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedHandler.Ok(ActorJson));
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var options = new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false };
        using var client = factory.Create(options, inner);

        // Without retry, the 503 surfaces immediately: GetObjectAsync returns null after a single
        // transport call (no second attempt).
        var @object = await client.GetObjectAsync(new Iri("https://remote.example/actors/alice"), Ct);

        Assert.Null(@object);
        Assert.Equal(1, inner.CallCount);
    }
}
