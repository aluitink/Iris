using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 S8 tests: the explorer's **raw JSON inspector** and **proxy-fallback** paths, exercised
/// in-process against a live <see cref="Iris.Server"/> ActivityPub pipeline (the two mechanisms the
/// S8 screen is built on; the screen's UI is a thin wrapper over them, covered by the in-process
/// assertions here the way the S3–S7 screens are).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Raw JSON inspector.</strong> The inspector is the primary tool for finding interop bugs: it
/// shows the exact signed request (the <c>Signature</c> header, the content-type) and the raw response.
/// Its library surface is <see cref="IActivityPubClient.SendAsync"/> — a raw request sent through the
/// client's full signed pipeline, returning the (unconsumed) response the caller inspects. The first
/// test proves <c>SendAsync</c> signs (the server sees a <c>Signature</c> header) and returns the raw
/// response body.
/// </para>
/// <para>
/// <strong>Proxy fallback.</strong> When a browser cannot reach a remote instance directly (CORS, and
/// the browser cannot produce an ActivityPub HTTP signature), the client's <see cref="Iris.Client.
/// Pipeline.ProxyFallbackHandler"/> retries the request through the home instance's proxy endpoint
/// (<c>POST {proxyBase}/ap/v1/proxy/{target}</c>), which the home server signs with the acting actor's
/// key. The second test drives the <em>full client pipeline</em> (retry → JSON-LD → signing, wrapped by
/// the proxy-fallback stage) against a real home server (A) whose proxy relays to a real remote server
/// (B): a direct GET to B's actor document (rejected 401 — A's signature is not resolvable by B) falls
/// back through A's proxy, which re-signs with alice's key, so B validates and the client gets the
/// document.
/// </para>
/// </remarks>
public sealed class S8InspectorAndProxyTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// Hosts a single in-process instance (A, host <c>localhost</c>, actor <c>alice</c>) with a
    /// Basic-auth credential validator (so the proxy endpoint can identify the acting actor) and a
    /// persistence-backed actor-document fetcher (so inbound signature validation reads the actor's
    /// <c>publicKey</c>). Returns the server, alice's dial-base actor IRI, and alice's signing key.
    /// </summary>
    private static (TestServer Server, Iri ActorIri, KeyPair Key) StartSingleHost(string handle, string password)
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();
        var actorIri = new Iri($"{dialBase}/ap/v1/u/{handle}");
        var keyId = new Iri($"{actorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(key);

        var person = new KristofferStrube.ActivityStreams.Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        person.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = keyId.Value,
                owner = actorIri.Value,
                publicKeyPem = key.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(person).GetAwaiter().GetResult();

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
                    opts.BaseUri = new Iri(dialBase);
                    opts.InstanceName = $"iris-{handle}";
                    opts.InstanceActorId = actorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (_, username, pass) =>
                    {
                        var valid = username == handle
                            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(pass),
                                System.Text.Encoding.UTF8.GetBytes(password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return (new TestServer(builder), actorIri, key);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that serves an actor's document directly from the
    /// in-process persistence, so inbound signature validation reads the actor's <c>publicKey</c>.
    /// </summary>
    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct)
                ? actor
                : null;
    }

    // --- Raw JSON inspector (SendAsync) ------------------------------------------

    [Fact]
    public async Task RawInspector_SendAsync_SignsAndReturnsRawResponse()
    {
        var (server, actorIri, key) = StartSingleHost("alice", "iris-sample");
        using var _ = server;

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new Iris.Client.Auth.InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        using var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            server.CreateHandler());

        // The raw inspector sends a request through the client's signed pipeline and inspects the
        // (unconsumed) response. A GET of the actor's own document, with an Accept that forces a
        // response body.
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, actorIri.Value);
        request.Headers.Accept.ParseAdd("application/activity+json");
        using var response = await client.SendAsync(request);

        // The request reached the server carrying a real Signature header (signed by the pipeline) —
        // the inspector's "exact signed request" half.
        Assert.True(response.IsSuccessStatusCode, $"the raw GET must succeed (got {(int)response.StatusCode})");

        // The response is returned unconsumed: the caller reads the raw body (the inspector's "raw
        // response" half). It is the actor document (its `id` is the actor IRI).
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.Contains(actorIri.Value, rawBody, StringComparison.Ordinal);
    }

    // --- Proxy fallback (full client pipeline: direct 401 -> proxy -> remote) -----

    [Fact]
    public async Task ProxyFallback_Direct401_RetriesThroughHomeProxyAndSucceeds()
    {
        const string AHost = "a.example";
        const string BHost = "b.example";
        const string password = "iris-sample";

        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var (aliceKey, aliceActorIri, _) = TestSeeder.SeedPersonWithKey(aPersistence, AHost, "alice");
        var (bobKey, bobActorIri, _) = TestSeeder.SeedPersonWithKey(bPersistence, BHost, "bob");

        // The two instances' cross-wiring is deferred (a LazyHandler) because each instance's
        // TestServer does not yet exist while it is being constructed: A's proxy relays to B, and B's
        // signature-validation fetcher resolves a signing key by fetching the actor's document from A.
        TestServer? aServer = null;
        TestServer? bServer = null;
        var bHandler = new LazyHandler(() => bServer!.CreateHandler());
        var aHandler = new LazyHandler(() => aServer!.CreateHandler());

        // B (the remote target): hosts bob; its signature validation resolves a signing actor's key by
        // fetching the actor's document from A (so it can resolve alice's key when A's proxy forwards a
        // re-signed GET).
        bServer = StartHost(
            BHost, bobActorIri, bPersistence,
            fetcher: new RemoteDocumentFetcher(aHandler, BHost),
            credentialValidator: null);

        // A (the home/proxy origin): hosts alice; its proxy's outbound transport is routed to B (so the
        // proxied GET reaches B in-process), and its credential validator is Basic auth (the proxy
        // identifies the acting actor). A's BaseUri is https://a.example (so the proxy relays to the
        // absolute target IRI), distinct from B's.
        aServer = StartHost(
            AHost, aliceActorIri, aPersistence,
            fetcher: new PersistenceActorFetcher(aPersistence),
            credentialValidator: BasicAuth("alice", password),
            deliveryTransport: () => bHandler);
        using var _ = aServer;
        using var _b = bServer;

        // The browser's client: signed as alice (A's key), with proxy fallback to A's home instance
        // (https://a.example) using alice's Basic-auth credentials. Its transport dials B's TestServer
        // (the remote target) — the direct attempt reaches B, whose signature validation rejects it
        // (A's signature is not resolvable by B), so the client falls back through A's proxy.
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(aliceKey);
        var keyProvider = new Iris.Client.Auth.InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(aliceActorIri, aliceKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        using var client = factory.Create(new ActivityPubClientOptions
        {
            ActorId = aliceActorIri,
            EnableRetry = false,
            ProxyBaseUrl = new Iri($"https://{AHost}"),
            ProxyCredentials = new ProxyCredentials("alice", password),
        }, bServer.CreateHandler());

        // The browser GETs bob's actor document. The direct attempt is rejected 401 by B (A's
        // signature is not resolvable cross-origin); the ProxyFallbackHandler retries through A's
        // proxy (POST https://a.example/ap/v1/proxy/https://b.example/ap/v1/u/bob), which A signs
        // with alice's key; B validates (resolving alice's key via A) and returns bob's document.
        var doc = await client.GetObjectAsync(bobActorIri);
        Assert.NotNull(doc);
        Assert.Equal(bobActorIri.Value, doc!.Id);
    }

    /// <summary>
    /// Builds a Basic-auth credential validator keyed on a username + password (the proxy endpoint
    /// identifies the acting actor from the request's Basic auth).
    /// </summary>
    private static IActorCredentialValidator BasicAuth(string username, string password)
        => new BasicAuthCredentialValidator(
            (_, user, pass) =>
            {
                var valid = user == username
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(pass),
                        System.Text.Encoding.UTF8.GetBytes(password));
                return new ValueTask<bool>(valid);
            });

    /// <summary>
    /// Hosts a single in-process instance with the given base-URI host, seeding nothing (the actor is
    /// pre-seeded into <paramref name="persistence"/> by the caller) and wiring an optional
    /// actor-document fetcher (inbound signature validation), an optional Basic-auth credential
    /// validator (the proxy + owner-only document paths), and an optional outbound transport (the
    /// proxy's relay + the delivery worker).
    /// </summary>
    private static TestServer StartHost(
        string host, Iri actorIri, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        IActorCredentialValidator? credentialValidator = null,
        Func<System.Net.Http.HttpMessageHandler>? deliveryTransport = null)
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
                    opts.BaseUri = new Iri($"https://{host}");
                    opts.InstanceName = $"iris-{host}";
                    opts.InstanceActorId = actorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                if (fetcher is not null)
                {
                    s.AddSingleton<IActorDocumentFetcher>(fetcher);
                }

                if (deliveryTransport is not null)
                {
                    s.AddSingleton<Func<System.Net.Http.HttpMessageHandler>>(() => deliveryTransport());
                }

                if (credentialValidator is not null)
                {
                    s.AddSingleton<IActorCredentialValidator>(credentialValidator);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that resolves an actor document by fetching the actor
    /// from an in-process <see cref="LazyHandler"/> (the source instance that hosts the actor), so a
    /// remote instance's signature validation can resolve the proxy origin's signing key over the wire.
    /// The handler is a deferred <see cref="LazyHandler"/> so an instance's fetcher can reach its
    /// (not-yet-constructed) peer.
    /// </summary>
    private sealed class RemoteDocumentFetcher(LazyHandler handler, string host) : IActorDocumentFetcher
    {
        private readonly LazyHandler _handler = handler;
        private readonly string _host = host;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            var uri = new Uri(actorIri.Value);
            using var http = new System.Net.Http.HttpClient(_handler, disposeHandler: false);
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/activity+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return Iris.Core.ActivityJson.Deserialize<Actor>(body);
        }
    }
}
