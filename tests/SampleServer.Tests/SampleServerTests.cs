using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Phase 7 integration tests: hosts the <see cref="SampleServer"/> (via its
/// <see cref="SampleServer.CreateWebHostBuilder"/> + an in-process <see cref="TestServer"/>) and
/// asserts the seeded actor, community, and WebFinger endpoints respond as the sample intends.
/// </summary>
/// <remarks>
/// The sample is a real, runnable ASP.NET Core host; these tests drive it in-process (no real port is
/// bound, because the <see cref="TestServer"/> overrides the Kestrel server). They cover: the public
/// actor document (no private key), the authenticated actor document (Basic auth unlocks the owner-only
/// <c>privateKey</c> + <c>keyAlgorithm</c> extensions), the no-leak guarantee for a wrong password, the
/// community document, its members, its feed, WebFinger resolution, and the 404 for an unknown actor.
/// </remarks>
public sealed class SampleServerTests : IDisposable
{
    private const string Host = "localhost";
    private const int Port = 5000;
    private const string Handle = "alice";
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _client;

    public SampleServerTests()
    {
        var builder = SampleServer.CreateWebHostBuilder();
        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static string BaseUri => $"http://{Host}:{Port}";

    private static string BasicAuth(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    // --- Public actor document -------------------------------------------------

    [Fact]
    public async Task ActorDoc_Public_ReturnsActorWithoutPrivateKey()
    {
        var response = await _client.GetAsync($"/ap/v1/u/{Handle}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/activity+json", response.Content.Headers.ContentType!.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        Assert.Equal($"{BaseUri}/ap/v1/u/{Handle}", doc!.Id);
        Assert.Equal(Handle, doc.PreferredUsername);

        Assert.False(
            doc.ExtensionData is { } ext && ext.ContainsKey("privateKey"),
            "public actor document must not include the privateKey extension");
        Assert.False(
            doc.ExtensionData is { } ext2 && ext2.ContainsKey("keyAlgorithm"),
            "public actor document must not include the keyAlgorithm extension");

        Assert.NotNull(doc.Inbox);
        Assert.NotNull(doc.Outbox);
    }

    // --- Authenticated actor document (owner-only extension) -------------------

    [Fact]
    public async Task ActorDoc_Authenticated_IncludesPrivateKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{Handle}")
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:{SampleServer.Password}"))),
            },
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);

        var ext = doc!.ExtensionData;
        Assert.NotNull(ext);
        Assert.True(ext!.ContainsKey("privateKey"),
            "authenticated actor document must include the privateKey extension");
        Assert.True(ext.ContainsKey("keyAlgorithm"),
            "authenticated actor document must include the keyAlgorithm extension");

        var privateKeyPem = ext["privateKey"].GetString();
        Assert.NotNull(privateKeyPem);
        Assert.Contains("-----BEGIN PRIVATE KEY-----", privateKeyPem);
        Assert.Equal("rsa", ext["keyAlgorithm"].GetString());
    }

    [Fact]
    public async Task ActorDoc_Public_ServesRsaPublicKeyPem()
    {
        var response = await _client.GetAsync($"/ap/v1/u/{Handle}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var publicKey = doc.RootElement.GetProperty("publicKey");

        // The sample now seeds an RSA-2048 key and serves its public half as PEM (publicKeyPem).
        var pem = publicKey.GetProperty("publicKeyPem").GetString();
        Assert.NotNull(pem);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem);
    }

    [Fact]
    public async Task ActorDoc_WrongPassword_ReturnsPublicDoc()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{Handle}")
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:wrong-password"))),
            },
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        Assert.False(
            doc!.ExtensionData is { } ext && ext.ContainsKey("privateKey"),
            "a wrong password must not unlock the privateKey extension");
    }

    // --- Community -------------------------------------------------------------

    [Fact]
    public async Task CommunityDoc_ReturnsGroup()
    {
        var response = await _client.GetAsync($"/ap/v1/c/{Community}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/activity+json", response.Content.Headers.ContentType!.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        Assert.Equal($"{BaseUri}/ap/v1/c/{Community}", doc!.Id);

        var ext = doc.ExtensionData;
        Assert.NotNull(ext);
        Assert.True(ext!.ContainsKey("members"), "community document must carry a members endpoint");
        Assert.True(ext.ContainsKey("feed"), "community document must carry a feed endpoint");
    }

    [Fact]
    public async Task CommunityMembers_ReturnsBothSeededActors()
    {
        var response = await _client.GetAsync($"/ap/v1/c/{Community}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("OrderedCollection", root.GetProperty("type").GetString());

        var items = root.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 2,
            "the community must have at least two seeded members");
    }

    [Fact]
    public async Task CommunityFeed_ReturnsSeededPosts()
    {
        var response = await _client.GetAsync($"/ap/v1/c/{Community}/feed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("OrderedCollection", root.GetProperty("type").GetString());

        var items = root.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 2,
            "the community feed must contain at least the two seeded posts");
    }

    // --- WebFinger -------------------------------------------------------------

    [Fact]
    public async Task WebFinger_ResolvesActor()
    {
        var response = await _client.GetAsync($"/.well-known/webfinger?resource=acct:{Handle}@{Host}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal($"acct:{Handle}@{Host}", root.GetProperty("subject").GetString());

        var links = root.GetProperty("links");
        var link = links[0];
        Assert.Equal("self", link.GetProperty("rel").GetString());
        Assert.Equal($"{BaseUri}/ap/v1/u/{Handle}", link.GetProperty("href").GetString());
    }

    // --- 404s ------------------------------------------------------------------

    [Fact]
    public async Task ActorDoc_UnknownHandle_Returns404()
    {
        var response = await _client.GetAsync("/ap/v1/u/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CommunityDoc_UnknownName_Returns404()
    {
        var response = await _client.GetAsync("/ap/v1/c/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
