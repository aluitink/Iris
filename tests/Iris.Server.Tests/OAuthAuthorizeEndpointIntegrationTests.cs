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
/// Phase 15.2 integration tests: the OAuth2 authorization endpoint
/// (<c>GET /ap/v1/oauth2/authorize</c>). Proves the browser-redirect half of the authorization-code
/// flow: auto-approve + one-time code issuance + 302 redirect to <c>redirect_uri</c> with
/// <c>code</c> + <c>state</c>, and the error paths (missing parameters, unknown actor). The TestServer
/// client does not auto-follow the redirect (the 302 + <c>Location</c> header are returned as-is), so
/// the tests read the code + state straight off the <c>Location</c>.
/// </summary>
public class OAuthAuthorizeEndpointIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string RedirectUri = "http://localhost:8090/callback";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly InMemoryOAuthTokenStore _tokenStore;

    public OAuthAuthorizeEndpointIntegrationTests()
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

    /// <summary>
    /// Reads the <paramref name="name"/> query parameter from a redirect <c>Location</c> URI.
    /// </summary>
    private static string ExtractQueryParam(string location, string name)
    {
        var query = location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..];
        return Uri.UnescapeDataString(
            query.Split('&').First(p => p.StartsWith(name + "=", StringComparison.Ordinal)).Substring(name.Length + 1));
    }

    private Task<HttpResponseMessage> AuthorizeAsync(string clientId, string redirectUri, string state)
        => _client.GetAsync(
            $"/ap/v1/oauth2/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}");

    [Fact]
    public async Task ValidRequest_RedirectsWithCodeAndState()
    {
        var resp = await AuthorizeAsync(Handle, RedirectUri, "xyz123");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);
        Assert.Equal("xyz123", ExtractQueryParam(location, "state"));

        // The code is stored for the actor and is redeemable (the Phase 15.2a token-exchange input).
        var code = ExtractQueryParam(location, "code");
        Assert.False(string.IsNullOrWhiteSpace(code));
        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var resolved = await _tokenStore.RedeemAuthorizationCodeAsync(code);
        Assert.Equal(actorIri, resolved);
    }

    [Fact]
    public async Task RedirectUriWithExistingQuery_UsesAmpersand()
    {
        var uriWithQuery = "http://localhost:8090/callback?foo=bar";
        var resp = await AuthorizeAsync(Handle, uriWithQuery, "st1");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith(uriWithQuery + "&code=", location);
        Assert.Equal("st1", ExtractQueryParam(location, "state"));
    }

    [Fact]
    public async Task MissingClientId_ReturnsBadRequest()
    {
        var resp = await _client.GetAsync(
            $"/ap/v1/oauth2/authorize?redirect_uri={Uri.EscapeDataString(RedirectUri)}&state=xyz");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingRedirectUri_ReturnsBadRequest()
    {
        var resp = await _client.GetAsync(
            $"/ap/v1/oauth2/authorize?client_id={Handle}&state=xyz");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingState_ReturnsBadRequest()
    {
        var resp = await _client.GetAsync(
            $"/ap/v1/oauth2/authorize?client_id={Handle}&redirect_uri={Uri.EscapeDataString(RedirectUri)}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownActor_ReturnsInvalidClient()
    {
        var resp = await AuthorizeAsync("nobody", RedirectUri, "xyz");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_client", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task IssuedCode_ExchangesForBearerToken()
    {
        // Full flow: authorize (302 + code) → token exchange (Phase 15.2a).
        var resp = await AuthorizeAsync(Handle, RedirectUri, "abc");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var code = ExtractQueryParam(resp.Headers.Location!.ToString(), "code");

        var tokenResp = await _client.PostAsync(
            $"/ap/v1/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
            }));

        tokenResp.EnsureSuccessStatusCode();
        var body = await tokenResp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("access_token", out var token));
        Assert.False(string.IsNullOrWhiteSpace(token.GetString()));
        Assert.Equal("bearer", doc.RootElement.GetProperty("token_type").GetString());
    }

    [Fact]
    public async Task IssuedCode_IsOneTime()
    {
        var resp = await AuthorizeAsync(Handle, RedirectUri, "abc");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var code = ExtractQueryParam(resp.Headers.Location!.ToString(), "code");

        var form1 = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
        });
        var resp1 = await _client.PostAsync($"/ap/v1/oauth2/token", form1);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp1.StatusCode);

        // A second exchange with the same (redeemed) code must fail.
        var form2 = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
        });
        var resp2 = await _client.PostAsync($"/ap/v1/oauth2/token", form2);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp2.StatusCode);
    }
}
