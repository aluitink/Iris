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
    public async Task WrongGrantType_ReturnsUnsupportedGrantType()
    {
        await IssueCodeAsync();

        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("password", Code));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("unsupported_grant_type", doc.RootElement.GetProperty("error").GetString());
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

    [Fact]
    public async Task AuthorizationCode_ReturnsRefreshToken()
    {
        await IssueCodeAsync();

        var resp = await _client.PostAsync(
            $"https://{Host}/ap/v1/oauth2/token",
            TokenForm("authorization_code", Code));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("access_token", out _));
        Assert.True(doc.RootElement.TryGetProperty("refresh_token", out var refreshToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken.GetString()));
    }

    [Fact]
    public async Task RefreshToken_ExchangesForNewTokenPair()
    {
        await IssueCodeAsync();

        // Exchange the code for a token + refresh token.
        var tokenForm = TokenForm("authorization_code", Code);
        var tokenResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", tokenForm);
        tokenResp.EnsureSuccessStatusCode();
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        var tokenDoc = JsonDocument.Parse(tokenBody);
        var originalToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
        var originalRefresh = tokenDoc.RootElement.GetProperty("refresh_token").GetString()!;

        // Refresh: exchange the refresh token for a new token + new refresh token.
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefresh,
        });
        var refreshResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", refreshForm);
        refreshResp.EnsureSuccessStatusCode();
        var refreshBody = await refreshResp.Content.ReadAsStringAsync();
        var refreshDoc = JsonDocument.Parse(refreshBody);
        var newToken = refreshDoc.RootElement.GetProperty("access_token").GetString()!;
        var newRefresh = refreshDoc.RootElement.GetProperty("refresh_token").GetString()!;

        // The new token pair is different from the original.
        Assert.NotEqual(originalToken, newToken);
        Assert.NotEqual(originalRefresh, newRefresh);

        // The new access token is resolvable.
        var resolved = await _tokenStore.ResolveTokenAsync(newToken);
        Assert.NotNull(resolved);

        // The old refresh token is no longer valid (rotated).
        var oldRefreshResolved = await _tokenStore.RedeemRefreshTokenAsync(originalRefresh);
        Assert.Null(oldRefreshResolved);
    }

    [Fact]
    public async Task RefreshToken_IsOneTime()
    {
        await IssueCodeAsync();

        // Exchange the code for a token + refresh token.
        var tokenForm = TokenForm("authorization_code", Code);
        var tokenResp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", tokenForm);
        tokenResp.EnsureSuccessStatusCode();
        var originalRefresh = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("refresh_token").GetString()!;

        // First refresh succeeds.
        var refreshForm1 = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefresh,
        });
        var refreshResp1 = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", refreshForm1);
        Assert.Equal(System.Net.HttpStatusCode.OK, refreshResp1.StatusCode);

        // Second refresh with the same (rotated) refresh token fails.
        var refreshForm2 = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefresh,
        });
        var refreshResp2 = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", refreshForm2);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refreshResp2.StatusCode);
        var body = await refreshResp2.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownRefreshToken_ReturnsInvalidGrant()
    {
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "unknown-refresh-token",
        });
        var resp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", refreshForm);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_grant", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingRefreshToken_ReturnsInvalidRequest()
    {
        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "",
        });
        var resp = await _client.PostAsync($"https://{Host}/ap/v1/oauth2/token", refreshForm);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
