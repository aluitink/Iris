using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Client.Extensions.Tests;

/// <summary>
/// Phase 7 end-to-end test: a real <see cref="TestServer"/> runs the Iris ActivityPub server
/// (Basic-auth actor endpoint with the owner-only <c>privateKey</c> extension + signature
/// validation). The <see cref="IrisSession"/> authenticates (Basic auth → PEM private key) and
/// stores the key in memory; the <see cref="IrisClientFactory"/> builds a pre-configured signed
/// client from that key. The client then performs a signed GET of the actor's public document
/// through the full HTTP stack — proving the session → key store → client pipeline is coherent
/// end to end.
/// </summary>
public sealed class EndToEndSessionIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Password = "correct-horse-battery";
    private const string ActorIri = $"https://{Host}/ap/v1/u/{Handle}";
    private const string KeyIri = $"{ActorIri}#key-1";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;

    public EndToEndSessionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedPersonWithKey(_persistence, Host, Handle);
        _server = StartServer(_persistence);
    }

    public void Dispose() => _server.Dispose();

    // --- The full session → key store → signed client pipeline ------------------------

    [Fact]
    public async Task Session_Login_BuildsClient_SignedGetSucceeds()
    {
        // The authenticator talks to the real server's Basic-auth actor endpoint (GET /ap/v1/u/alice
        // with the Authorization header), fetching the owner-only document + PEM private key.
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(ActorIri), Handle, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false, // no proxy in this single-instance test
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        // 1. Login: fetch the owner-only doc + key, store the key, register the identity.
        var actor = await bundle.Session.LoginAsync(new Iri(ActorIri));
        Assert.NotNull(actor);
        Assert.True(bundle.Session.IsAuthenticated);
        Assert.True(bundle.Session.KeyStore.TryGetKey(new Iri(KeyIri), out _),
            "the authenticated key should be in the session key store");

        // 2. Build a pre-configured signed client, routed to the in-process server.
        using var client = bundle.CreateClient(new Iri(ActorIri), _server.CreateHandler());

        // 3. A signed GET of the actor's public document. The server's SignatureValidationMiddleware
        //    resolves the signer's public key (from the actor doc's publicKey JWK) and verifies; a
        //    200 proves the session's key signed the request and the server accepted the signature.
        var fetched = await client.GetActorAsync(new Iri(ActorIri));
        Assert.NotNull(fetched);
        Assert.Equal(ActorIri, fetched!.Id);
        Assert.Equal(Handle, fetched.PreferredUsername);
    }

    // --- A wrong password yields no key: the session stays unauthenticated -------------

    [Fact]
    public async Task Session_WrongPassword_LoginFails_NoKeyStored()
    {
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(ActorIri), Handle, "wrong-password");

        using var bundle = IrisClientBuilder.Create(new IrisClientOptions { UseProxyFallback = false })
            .WithAuthenticator(authenticator)
            .Build();

        // A wrong password yields the *public* document (no privateKey extension) → the
        // authenticator returns null → the session stays unauthenticated and stores no key.
        var actor = await bundle.Session.LoginAsync(new Iri(ActorIri));
        Assert.Null(actor);
        Assert.False(bundle.Session.IsAuthenticated);
        Assert.False(bundle.Session.KeyStore.TryGetKey(new Iri(KeyIri), out _));
    }

    // --- The authenticated (owner-only) document is never served publicly ---------------

    [Fact]
    public async Task PublicActorDoc_DoesNotLeakPrivateKey()
    {
        // An unauthenticated GET returns the public document without the privateKey extension.
        var http = _server.CreateClient();
        using var response = await http.GetAsync(ActorIri);
        Assert.True(response.IsSuccessStatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("privateKey", out _),
            "the unauthenticated actor document must not carry the privateKey extension");
    }

    // --- Helpers ----------------------------------------------------------------------

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
    {
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
                    opts.InstanceName = $"iris-{Host}";
                    opts.InstanceActorId = new Iri(ActorIri);
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);

                // Basic-auth credential validator for the seeded actor (owner-only doc gate).
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (actorIri, username, password) =>
                    {
                        var valid = username == Handle &&
                            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }
}
