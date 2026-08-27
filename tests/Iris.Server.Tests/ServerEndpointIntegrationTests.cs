using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 3 integration tests: the live <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> built by
/// <see cref="ActivityPubServerExtensions.AddActivityPubServer(IServiceCollection)"/> +
/// <see cref="InMemoryPersistenceExtensions.AddInMemoryPersistence(IServiceCollection)"/> +
/// <see cref="ActivityPubServerExtensions.MapActivityPubEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)"/>.
/// </summary>
/// <remarks>
/// These prove the server's end-to-end behavior over a genuine HTTP stack: the versioned route prefix
/// (<c>/ap/v1</c>) + <c>Iris-Version</c> meta header, the public actor document, the authenticated actor
/// document (owner-only <c>privateKey</c> PEM + <c>keyAlgorithm</c> extension), WebFinger resolution, and
/// NodeInfo. The private key is only ever included when the request is authenticated (Basic auth).
/// </remarks>
public class ServerEndpointIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Password = "s3cret!";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly HttpClient _client;

    public ServerEndpointIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);

        var builder = new WebHostBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.None);
            })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{Host}");
                    opts.InstanceName = "test-iris";
                });
                s.AddInMemoryPersistence();

                // Replace the auto-registered in-memory provider with our seeded one.
                s.AddSingleton<IPersistenceProvider>(_persistence);

                // Replace the default (no-op) credential validator with a Basic-auth one for the seeded actor.
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (iri, username, password) =>
                    {
                        var expected = new Iri($"https://{Host}/ap/v1/u/{Handle}");
                        var valid = iri == expected &&
                            username == Handle &&
                            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var actorIri = $"https://{Host}/ap/v1/u/{Handle}";
        var keyId = new Iri($"{actorIri}#key-1");

        // A Person actor with a publicKey extension (the library carries publicKey in ExtensionData).
        var actor = new Person
        {
            Id = actorIri,
            PreferredUsername = Handle,
            Name = [Handle],
        };

        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIri,
            publicKeyPem = "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A\n-----END PUBLIC KEY-----",
        });

        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        // A signing key for the actor (the private key that gets revealed on auth).
        var keyPair = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(keyPair);
    }

    private static string BasicAuth(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    // --- Version header + route prefix -----------------------------------------

    [Fact]
    public async Task ActorDoc_CarriesIrisVersionHeader()
    {
        var response = await _client.GetAsync($"/ap/v1/u/{Handle}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("Iris-Version").Single());
    }

    // --- Public actor document -------------------------------------------------

    [Fact]
    public async Task ActorDoc_Public_ReturnsActorWithoutPrivateKey()
    {
        var response = await _client.GetAsync($"/ap/v1/u/{Handle}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Content type is activity+json.
        Assert.Equal("application/activity+json", response.Content.Headers.ContentType!.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        Assert.Equal($"https://{Host}/ap/v1/u/{Handle}", doc!.Id);

        // The public document must NOT carry the owner-only privateKey extension.
        Assert.False(
            doc.ExtensionData is { } ext && ext.ContainsKey("privateKey"),
            "public actor document must not include the privateKey extension");
        Assert.False(
            doc.ExtensionData is { } ext2 && ext2.ContainsKey("keyAlgorithm"),
            "public actor document must not include the keyAlgorithm extension");

        // It must carry the standard collection endpoints.
        Assert.NotNull(doc.Inbox);
        Assert.NotNull(doc.Outbox);
    }

    [Fact]
    public async Task ActorDoc_UnknownHandle_Returns404()
    {
        var response = await _client.GetAsync("/ap/v1/u/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Authenticated actor document (owner-only extension) -------------------

    [Fact]
    public async Task ActorDoc_Authenticated_IncludesPrivateKeyPem()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{Handle}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:{Password}")));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);

        // The authenticated document includes the owner-only privateKey + keyAlgorithm extensions.
        var ext = doc!.ExtensionData;
        Assert.NotNull(ext);
        Assert.True(ext!.ContainsKey("privateKey"),
            "authenticated actor document must include the privateKey extension");
        Assert.True(ext.ContainsKey("keyAlgorithm"),
            "authenticated actor document must include the keyAlgorithm extension");

        var privateKeyPem = ext["privateKey"].GetString();
        Assert.NotNull(privateKeyPem);
        Assert.Contains("-----BEGIN PRIVATE KEY-----", privateKeyPem);

        // The keyAlgorithm label matches the EC key we seeded.
        Assert.Equal("ecdsa-p256", ext["keyAlgorithm"].GetString());
    }

    [Fact]
    public async Task ActorDoc_WrongPassword_ReturnsPublicDoc()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ap/v1/u/{Handle}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:wrong-password")));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = ActivityJson.Deserialize<Actor>(json);
        Assert.NotNull(doc);
        // Wrong password → not authenticated → no privateKey.
        Assert.False(doc!.ExtensionData is { } ext && ext.ContainsKey("privateKey"),
            "unauthenticated actor document must not include the privateKey extension");
    }

    // --- WebFinger -------------------------------------------------------------

    [Fact]
    public async Task WebFinger_ResolvesHandleToActorIri()
    {
        var response = await _client.GetAsync(
            $"/ap/v1/.well-known/webfinger?resource=acct:{Handle}@{Host}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal($"acct:{Handle}@{Host}", doc.RootElement.GetProperty("subject").GetString());

        var link = doc.RootElement.GetProperty("links")[0];
        Assert.Equal("self", link.GetProperty("rel").GetString());
        Assert.Equal($"https://{Host}/ap/v1/u/{Handle}", link.GetProperty("href").GetString());
    }

    [Fact]
    public async Task WebFinger_UnknownHandle_Returns404()
    {
        var response = await _client.GetAsync(
            $"/ap/v1/.well-known/webfinger?resource=acct:nobody@{Host}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- NodeInfo --------------------------------------------------------------

    [Fact]
    public async Task NodeInfo_ReturnsInstanceMetadata()
    {
        var response = await _client.GetAsync("/ap/v1/nodeinfo/2.0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal("iris", doc.RootElement.GetProperty("software").GetProperty("name").GetString());
    }

    [Fact]
    public async Task NodeInfoWellKnown_ReturnsDiscoveryLink()
    {
        var response = await _client.GetAsync("/ap/v1/.well-known/nodeinfo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var link = doc.RootElement.GetProperty("links")[0];
        Assert.Contains("nodeinfo/2.0", link.GetProperty("href").GetString());
    }
}
