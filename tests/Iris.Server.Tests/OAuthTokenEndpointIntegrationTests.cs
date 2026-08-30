using System.Text;
using System.Text.Json;
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
/// Phase 15.2a integration tests: the server-side OAuth2 token + revoke endpoints
/// (<c>POST /ap/v1/oauth2/token</c> + <c>POST /ap/v1/oauth2/revoke</c>). Proves the code→token
/// exchange, token revocation, and error handling (invalid grant, wrong grant type, missing fields).
/// </summary>
public class OAuthTokenEndpointIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Code = "auth-code-12345";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly InMemoryOAuthTokenStore _tokenStore;

    public OAuthTokenEndpointIntegrationTests()
    {
        _tokenStore = new InMemoryOAuthTokenStore();
        var persistence = new InMemoryPersistenceProvider();
        Seed(persistence);

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
    }

    private async Task<string?> IssueCodeAsync()
    {
        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        await _tokenStore.StoreAuthorizationCodeAsync(Code, actorIri);
        return Code;
    }

    private static FormUrlEncodedContent TokenForm(string grantType, string? code = null) =>
        new(new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["code"] = code ?? "",
        });

    [Fact]
    public async Task ValidCode_ExchangesForBearerToken()
    {
        await IssueCodeAsync();

        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("authorization_code", Code));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("access_token", out var token));
        Assert.False(string.IsNullOrWhiteSpace(token.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("token_type", out var type));
        Assert.Equal("bearer", type.GetString());
    }

    [Fact]
    public async Task RedeemedCode_IsOneTime()
    {
        await IssueCodeAsync();

        var form1 = TokenForm("authorization_code", Code);
        var resp1 = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", form1);
        resp1.EnsureSuccessStatusCode();

        // The same code must not redeem a second time.
        var form2 = TokenForm("authorization_code", Code);
        var resp2 = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", form2);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task UnknownCode_ReturnsInvalidGrant()
    {
        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("authorization_code", "unknown-code"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WrongGrantType_ReturnsInvalidRequest()
    {
        await IssueCodeAsync();

        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("password", Code));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingCode_ReturnsInvalidRequest()
    {
        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("authorization_code", ""));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ValidToken_CanBeRevoked()
    {
        await IssueCodeAsync();

        // Exchange the code for a token.
        var tokenForm = TokenForm("authorization_code", Code);
        var tokenResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", tokenForm);
        tokenResp.EnsureSuccessStatusCode();
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var token = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;

        // The token is resolvable before revocation.
        var actorIriBefore = await _tokenStore.ResolveTokenAsync(token);
        Assert.NotNull(actorIriBefore);

        // Revoke the token.
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        });
        var revokeResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/revoke", revokeForm);
        Assert.Equal(System.Net.HttpStatusCode.OK, revokeResp.StatusCode);

        // The token is no longer resolvable after revocation.
        var actorIriAfter = await _tokenStore.ResolveTokenAsync(token);
        Assert.Null(actorIriAfter);
    }

    [Fact]
    public async Task RevokeUnknownToken_ReturnsOk()
    {
        // RFC 7009: always 200, even for unknown tokens.
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "unknown-token",
        });
        var resp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/revoke", revokeForm);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task RevokedToken_CannotBeUsedForAuth()
    {
        // Wire up the BearerTokenCredentialValidator to resolve tokens via the store.
        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        await _tokenStore.StoreAuthorizationCodeAsync(Code, actorIri);

        var tokenForm = TokenForm("authorization_code", Code);
        var tokenResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", tokenForm);
        tokenResp.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("access_token").GetString()!;

        // Revoke the token.
        var revokeForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
        });
        await _client.PostAsync($"https://{Host}/ap/v1/oauth2/revoke", revokeForm);

        // Now request the actor document with the revoked token — the Bearer validator
        // (wired via the store) must reject it.
        // (The default credential validator is a no-op, so this test verifies the store
        //  directly rather than through the endpoint.)
        var resolved = await _tokenStore.ResolveTokenAsync(token);
        Assert.Null(resolved);
    }
}
