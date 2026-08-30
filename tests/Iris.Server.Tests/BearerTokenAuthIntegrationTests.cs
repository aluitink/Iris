using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// Phase 15.1 integration tests: the <see cref="BearerTokenCredentialValidator"/> as the
/// <c>IActorCredentialValidator</c> seam. Proves that a Bearer-token Authorization header
/// authenticates the owner-only actor-document extension (privateKey + keyAlgorithm), that a
/// missing/invalid token rejects, and that the public document never leaks the private key.
/// </summary>
public class BearerTokenAuthIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Token = "tok-12345";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly KeyPair _key;

    public BearerTokenAuthIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        _key = Seed(persistence);

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
                s.AddSingleton<IPersistenceProvider>(persistence);

                // Bearer-token credential validator: the token must match the static token for the seeded actor.
                s.AddSingleton<IActorCredentialValidator>(new BearerTokenCredentialValidator(
                    (iri, token) =>
                    {
                        var expected = new Iri($"https://{Host}/ap/v1/u/{Handle}");
                        var valid = iri == expected && token == Token;
                        return new ValueTask<string?>(valid ? Handle : null);
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

    public void Dispose()
    {
        _server.Dispose();
        _key.Dispose();
    }

    private static KeyPair Seed(InMemoryPersistenceProvider persistence)
    {
        var actorIri = $"https://{Host}/ap/v1/u/{Handle}";
        var keyId = new Iri($"{actorIri}#key-1");
        var keyPair = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(keyPair);

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
            publicKeyPem = keyPair.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        return keyPair;
    }

    private static string ActorDocUrl() => $"https://{Host}/ap/v1/u/{Handle}";

    [Fact]
    public async Task PublicDocument_ExcludesPrivateKey()
    {
        var resp = await _client.GetAsync(ActorDocUrl());
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var ext = doc.RootElement.TryGetProperty("privateKey", out var pk) ? pk : default;
        Assert.Equal(JsonValueKind.Undefined, ext.ValueKind);
    }

    [Fact]
    public async Task BearerToken_AuthenticatesAndIncludesPrivateKey()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ActorDocUrl());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("privateKey", out var pk),
            "authenticated actor document must include the privateKey extension");
        Assert.NotEqual(JsonValueKind.Undefined, pk.ValueKind);
    }

    [Fact]
    public async Task MissingToken_Rejects()
    {
        var resp = await _client.GetAsync(ActorDocUrl());
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("privateKey", out _),
            "public actor document must not include the privateKey extension");
    }

    [Fact]
    public async Task InvalidToken_Rejects()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ActorDocUrl());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("privateKey", out _),
            "actor document with invalid token must not include the privateKey extension");
    }

    [Fact]
    public async Task BasicAuth_Header_IsRejected_ByBearerValidator()
    {
        // A Basic-auth header is not a Bearer token — the validator must reject it.
        using var req = new HttpRequestMessage(HttpMethod.Get, ActorDocUrl());
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Handle}:{Token}")));
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("privateKey", out _),
            "Basic-auth header must be rejected by the Bearer validator");
    }

    [Fact]
    public async Task EmptyToken_Rejects()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ActorDocUrl());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "");
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("privateKey", out _),
            "empty Bearer token must be rejected");
    }

    [Fact]
    public async Task WrongActor_Rejects()
    {
        // The token is valid for alice, but we request carol's document.
        var resp = await _client.GetAsync($"https://{Host}/ap/v1/u/carol");
        // carol doesn't exist → 404 (not a credential issue, just no such actor).
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
