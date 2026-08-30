using System.Net;
using System.Text;
using System.Text.Json;
using Iris.Client.Auth;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 15.2b integration tests: the <see cref="OAuth2ClientAuthenticator"/> (client-side half of
/// the OAuth2 flow). Proves that a Bearer token (obtained via the code→token exchange) authenticates
/// the actor-document fetch + private-key extraction, that a missing/invalid token rejects, and that
/// the token→handle resolution goes through the <see cref="IOAuthTokenStore"/>.
/// </summary>
public class OAuth2ClientAuthenticatorIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Code = "auth-code-12345";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly InMemoryOAuthTokenStore _tokenStore;
    private readonly KeyPair _key;

    public OAuth2ClientAuthenticatorIntegrationTests()
    {
        _tokenStore = new InMemoryOAuthTokenStore();
        var persistence = new InMemoryPersistenceProvider();
        _key = Seed(persistence);

        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");

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
                s.AddSingleton<IOAuthTokenStore>(_tokenStore);

                // Bearer-token credential validator: resolves the token via the IOAuthTokenStore.
                s.AddSingleton<IActorCredentialValidator>(new BearerTokenCredentialValidator(
                    (iri, token) =>
                    {
                        var resolved = _tokenStore.ResolveTokenAsync(token).GetAwaiter().GetResult();
                        return new ValueTask<string?>(resolved.HasValue && resolved.Value == iri ? Handle : null);
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

    private async Task<string?> ExchangeCodeForTokenAsync()
    {
        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        await _tokenStore.StoreAuthorizationCodeAsync(Code, actorIri);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = Code,
        });
        var resp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", form);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString();
    }

    [Fact]
    public async Task ValidToken_AuthenticatesAndLoadsKey()
    {
        var token = await ExchangeCodeForTokenAsync();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var authenticator = new OAuth2ClientAuthenticator(
            _client,
            _ => new ValueTask<string?>(token));

        var actorId = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var result = await authenticator.AuthenticateAsync(actorId);

        Assert.NotNull(result);
        Assert.NotNull(result!.Actor);
        Assert.NotNull(result.Key);
        Assert.Equal(Handle, result.Actor.PreferredUsername);
    }

    [Fact]
    public async Task MissingToken_ReturnsNull()
    {
        var authenticator = new OAuth2ClientAuthenticator(
            _client,
            _ => new ValueTask<string?>((string?)null));

        var actorId = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var result = await authenticator.AuthenticateAsync(actorId);

        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidToken_ReturnsNull()
    {
        var authenticator = new OAuth2ClientAuthenticator(
            _client,
            _ => new ValueTask<string?>("invalid-token"));

        var actorId = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var result = await authenticator.AuthenticateAsync(actorId);

        Assert.Null(result);
    }

    [Fact]
    public async Task RevokedToken_ReturnsNull()
    {
        var token = await ExchangeCodeForTokenAsync();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Revoke the token.
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token!,
        });
        await _client.PostAsync($"https://{Host}/ap/v1/oauth2/revoke", revokeForm);

        var authenticator = new OAuth2ClientAuthenticator(
            _client,
            _ => new ValueTask<string?>(token));

        var actorId = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var result = await authenticator.AuthenticateAsync(actorId);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenForWrongActor_ReturnsNull()
    {
        var token = await ExchangeCodeForTokenAsync();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Use the token (valid for alice) to fetch carol's document.
        var authenticator = new OAuth2ClientAuthenticator(
            _client,
            _ => new ValueTask<string?>(token));

        var carolId = new Iri($"https://{Host}/ap/v1/u/carol");
        var result = await authenticator.AuthenticateAsync(carolId);

        // carol doesn't exist → 404 → null.
        Assert.Null(result);
    }
}
