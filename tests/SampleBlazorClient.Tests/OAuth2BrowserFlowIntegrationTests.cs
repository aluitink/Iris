using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 15.2 integration tests: the OAuth2 browser flow end-to-end against a live in-process
/// <see cref="Iris.Server"/> (TestServer). Proves the full authorization-code path: the
/// <see cref="OAuth2BrowserFlow"/> builds the authorize URL, the server 302s back with a one-time
/// <c>code</c> + <c>state</c>, the flow exchanges the code for a Bearer token, the
/// <see cref="ExplorerSession.LogOnWithOAuth2Async"/> logs on with that token (Bearer-auth actor
/// document + private key), and the resulting signed client performs a real signed write
/// (<c>PostNoteAsync</c>). Also covers the pure helper (authorize URL shape, callback parsing) and
/// the error path (a bad code fails the exchange).
/// </summary>
public sealed class OAuth2BrowserFlowIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly InMemoryOAuthTokenStore _tokenStore;
    private readonly Uri _dialBase;

    public OAuth2BrowserFlowIntegrationTests()
    {
        _tokenStore = new InMemoryOAuthTokenStore();
        _dialBase = new Uri($"http://{Host}");
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

                // Bearer-token credential validator: the /ap/v1/oauth2/token exchange stores the Bearer
                // token in the IOAuthTokenStore keyed by the actor IRI; the owner-only actor-document
                // path resolves the token back to the actor via the same store.
                s.AddSingleton<IActorCredentialValidator>(new BearerTokenCredentialValidator(
                    (iri, token) =>
                    {
                        var resolved = _tokenStore.ResolveTokenAsync(token).GetAwaiter().GetResult();
                        return new ValueTask<string?>(resolved.HasValue && resolved.Value == iri ? Handle : null);
                    }));
                s.AddSingleton<IKeyStore>(persistence.Keys);
                // The inbound key resolver resolves the signing key in-process (no network) by reading
                // the actor's publicKey from the in-process persistence, so the signed outbox write's
                // signature is verified against the seeded key.
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves an actor's document directly from the
    /// in-process persistence (no network), so the inbound key resolver verifies the signature by
    /// reading the actor's <c>publicKey</c>.
    /// </summary>
    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct)
                ? actor
                : null;
    }

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
    /// Runs the OAuth2 authorize endpoint (via the <see cref="OAuth2BrowserFlow"/> URL) and returns the
    /// <c>code</c> + <c>state</c> the server's 302 <c>Location</c> carried.
    /// </summary>
    private async Task<(string Code, string State)> AuthorizeAsync(string state)
    {
        var redirectUri = OAuth2BrowserFlow.BuildRedirectUri(_dialBase, "/callback");
        var authorizeUrl = OAuth2BrowserFlow.BuildAuthorizeUrl(_dialBase, Handle, redirectUri, state);

        // Dial the authorize endpoint by its path on the TestServer (the URL's host is the advertised
        // IRI host; the TestServer serves it in-process).
        var pathAndQuery = authorizeUrl.PathAndQuery;
        var resp = await _client.GetAsync(pathAndQuery);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);

        var location = resp.Headers.Location!.ToString();
        var (code, returnedState) = OAuth2BrowserFlow.ParseCallback(new Uri(location));
        Assert.False(string.IsNullOrWhiteSpace(code));
        return (code!, state);
    }

    [Fact]
    public async Task AuthorizeUrl_HasExpectedShape()
    {
        var redirectUri = OAuth2BrowserFlow.BuildRedirectUri(_dialBase, "/callback");
        var authorizeUrl = OAuth2BrowserFlow.BuildAuthorizeUrl(_dialBase, Handle, redirectUri, "st-1");

        var s = authorizeUrl.ToString();
        Assert.Contains("/ap/v1/oauth2/authorize", s);
        Assert.Contains($"client_id={Handle}", s);
        Assert.Contains("redirect_uri=", s);
        Assert.Contains("state=st-1", s);
    }

    [Fact]
    public void ParseCallback_ReadsCodeAndState()
    {
        var (code, state) = OAuth2BrowserFlow.ParseCallback(new Uri("http://x/callback?code=abc%3D%3D&state=st-1"));
        Assert.Equal("abc==", code);
        Assert.Equal("st-1", state);
    }

    [Fact]
    public void NewState_IsUniqueAndUrlSafe()
    {
        var a = OAuth2BrowserFlow.NewState();
        var b = OAuth2BrowserFlow.NewState();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Fact]
    public async Task FullFlow_AuthorizeTokenLogOnSignedWrite()
    {
        // 1. Authorize: the server 302s back with a one-time code + the echoed state.
        var (code, _) = await AuthorizeAsync("st-flow");

        // 2. Exchange the code for a Bearer token (the code is one-time).
        var token = await OAuth2BrowserFlow.ExchangeCodeAsync(_client, _dialBase, code);
        Assert.False(string.IsNullOrWhiteSpace(token), "the code exchange must return a Bearer token");

        // 3. Log on with the token (Bearer-auth actor document + private key load).
        var session = new ExplorerSession(() => _server.CreateHandler());
        var loggedOn = await session.LogOnWithOAuth2Async(Handle, token!, _dialBase);
        Assert.True(loggedOn, "the OAuth2 logon must succeed with a valid Bearer token");
        Assert.True(session.IsLoggedIn);

        // 4. The signed client performs a real signed write (PostNoteAsync) — proof the private key
        //    was loaded and the client signs as the actor.
        var actorIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var result = await session.GetClient().PostNoteAsync(actorIri, "hello via oauth2");
        Assert.True(result.IsSuccess, $"the signed PostNoteAsync must be accepted (got HTTP {result.StatusCode})");
    }

    [Fact]
    public async Task ExchangeCode_WithBadCode_ReturnsNull()
    {
        var token = await OAuth2BrowserFlow.ExchangeCodeAsync(_client, _dialBase, "not-a-real-code");
        Assert.Null(token);
    }

    [Fact]
    public async Task ExchangeCode_IsOneTime()
    {
        var (code, _) = await AuthorizeAsync("st-once");
        var first = await OAuth2BrowserFlow.ExchangeCodeAsync(_client, _dialBase, code);
        Assert.False(string.IsNullOrWhiteSpace(first));

        // A second exchange with the same (redeemed) code must fail.
        var second = await OAuth2BrowserFlow.ExchangeCodeAsync(_client, _dialBase, code);
        Assert.Null(second);
    }

    [Fact]
    public async Task LogOnWithOAuth2_WithInvalidToken_Fails()
    {
        var session = new ExplorerSession(() => _server.CreateHandler());
        var loggedOn = await session.LogOnWithOAuth2Async(Handle, "bogus-token", _dialBase);
        Assert.False(loggedOn, "an invalid Bearer token must not log on");
        Assert.False(session.IsLoggedIn);
    }
}
