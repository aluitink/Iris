using System.Text;
using Iris.Client;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Client.Extensions.Tests;

/// <summary>
/// Phase 11 Slice 11.5 end-to-end test (gap J-6 — the client's "post a note" API): a real
/// <see cref="TestServer"/> runs the Iris ActivityPub server. A local actor authenticates (Basic auth →
/// PEM private key), then — using the client's one-call <see cref="IActivityPubClient.PostNoteAsync"/>
/// — posts a note. The request is signed through the full pipeline and accepted by the server's inbox
/// (signature-validated and stored). This proves the post step — the headline write-path dead-end — is
/// reachable through the client as a user would drive it.
/// </summary>
public sealed class PostNoteIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Author = "alice";
    private const string Password = "correct-horse-battery";
    private const string AuthorIri = $"https://{Host}/ap/v1/u/{Author}";
    private const string AuthorKeyIri = $"{AuthorIri}#key-1";

    private readonly TestServer _server;
    private readonly InMemoryPersistenceProvider _persistence;

    public PostNoteIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        // The author gets a real signing key (embedded as a JWK in the actor doc) so the server's
        // SignatureValidationMiddleware can verify the signed Create.
        var (authorKey, _, _) = TestSeeder.SeedPersonWithKey(_persistence, Host, Author);

        // The server's inbound key resolver must fetch the author's actor doc to verify the post's
        // signature. In a single-instance test that doc lives on THIS server, so the fetcher is wired
        // to reach the in-process TestServer. The TestServer is created by ActivityPubHostFactory.Create
        // (below), which is the very call that wires the fetcher — a chicken-and-egg. The LazyHandler
        // therefore captures a Func<TestServer> (deferred to first use) rather than a server reference,
        // because _server is still null while the object initializer that assigns it is running.
        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Author,
            Persistence = _persistence,
            CredentialValidator = new BasicAuthCredentialValidator((_, username, password) =>
            {
                var valid = username == Author &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
                return new ValueTask<bool>(valid);
            }),
            Fetcher = BuildSelfFetcher(authorKey, () => _server!),
        });
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Session_Login_ThenPostNoteAsync_SignedCreateIsAccepted()
    {
        // Authenticate as the author (Basic auth → owner-only doc + PEM key).
        var authenticator = new BasicAuthClientAuthenticator(
            _server.CreateClient(), new Iri(AuthorIri), Author, Password);

        var options = new IrisClientOptions
        {
            ServerBaseUri = new Uri($"https://{Host}"),
            UseProxyFallback = false,
            // The in-process TestServer transport does not clone the request between sends, so a
            // retried post (RetryHandler) would re-send the same HttpRequestMessage and be rejected.
            // Real deployments use a socket transport (which clones internally); disable retry here to
            // keep the single-attempt post on the in-process wire.
            EnableRetry = false,
        };
        using var bundle = IrisClientBuilder.Create(options)
            .WithAuthenticator(authenticator)
            .Build();

        var actor = await bundle.Session.LoginAsync(new Iri(AuthorIri));
        Assert.NotNull(actor);
        Assert.True(bundle.Session.KeyStore.TryGetKey(new Iri(AuthorKeyIri), out _),
            "the authenticated key should be in the session key store");

        // Build a signed client routed to the in-process server and post a note. The client builds the
        // Create (with the embedded Note) and delivers it to the author's own inbox, signed as the author.
        using var client = bundle.CreateClient(new Iri(AuthorIri), _server.CreateHandler());
        var status = await client.PostNoteAsync(new Iri(AuthorIri), "hello from the client");

        // The signed Create is accepted by the server's inbox (202) — signature validated, stored.
        Assert.Equal(202, status);
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// Builds the server's <see cref="IActorDocumentFetcher"/> so it fetches actor documents through
    /// the in-process <see cref="TestServer"/> (the same host that owns the actors), signed with the
    /// author's key. The <see cref="LazyHandler"/> defers the transport to the server's
    /// <see cref="TestServer.CreateHandler()"/> until first use, so the fetcher can be built before the
    /// <see cref="TestServer"/> exists. The server is captured by a <see cref="Func{TResult}"/> (rather
    /// than a reference) because the fetcher is wired by the very <c>ActivityPubHostFactory.Create</c>
    /// call that assigns the field the server reference.
    /// </summary>
    private static IActorDocumentFetcher BuildSelfFetcher(KeyPair authorKey, Func<TestServer> server)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(authorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(new Iri(AuthorIri), authorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = new Iri(AuthorIri), EnableRetry = false },
            new LazyHandler(server));

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

}
