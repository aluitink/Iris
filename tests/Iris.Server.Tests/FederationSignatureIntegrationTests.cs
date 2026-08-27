using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 integration tests: the first true <strong>instance-to-instance federation</strong> test.
/// Two live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> instances (A and B) are
/// wired together over a genuine HTTP stack:
/// </summary>
/// <list type="bullet">
/// <item>Instance A (a.domain.local) hosts actor <c>alice</c> (key <c>keyA</c>).</item>
/// <item>Instance B (b.domain.local) hosts actor <c>bob</c> (key <c>keyB</c>).</item>
/// </list>
/// <para>
/// alice follows bob: a client signed as alice POSTs a <c>Follow</c> activity to B's inbox. B's
/// <see cref="SignatureValidationMiddleware"/> validates the HTTP signature by resolving alice's public
/// key — fetching A's actor document over the wire (B's <see cref="IActorDocumentFetcher"/> is wired to
/// A's <c>TestServer</c>) — and checking it cryptographically. The inbox handler then stores the
/// validated activity.
/// </para>
/// <remarks>
/// This proves the full inbound validation path end-to-end: signature parsing, remote key resolution
/// via actor-document fetch, key reconstruction from a JWK, and cryptographic verification — all over
/// real HTTP between two independent Iris instances.
/// </remarks>
public sealed class FederationSignatureIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;

    private readonly Iri AliceActorIri;
    private readonly Iri AliceKeyId;
    private readonly Iri BobActorIri;
    private readonly Iri BobInboxIri;

    public FederationSignatureIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = Seed(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;
        AliceKeyId = aSeeded.KeyId;

        var bSeeded = Seed(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        BobActorIri = bSeeded.ActorIri;
        BobInboxIri = BobActorIri.InboxOf();

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, targetServer: _a));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- The happy path: alice follows bob over the wire ----------------------

    [Fact]
    public async Task Follow_SignedByAlice_IsValidatedAndAcceptedAtBobInbox()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // A client signed as alice, whose transport routes to B's TestServer.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);

        Assert.Equal(202, statusCode);

        // B validated the signature (by fetching A's actor doc to resolve alice's key) and stored
        // the activity under its IRI.
        var stored = await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var activity);
        Assert.True(stored);
        Assert.NotNull(activity);
        Assert.Equal(follow.Id, activity!.Id);

        // A's actor document endpoint was hit by B's key-resolution fetch (the federation round-trip).
        // (The middleware on A does not validate GETs, so this is a plain public document fetch.)
    }

    // --- Key resolution: B resolves alice's key by fetching A's actor doc --------

    [Fact]
    public async Task Resolver_ResolvesRemoteKey_ByFetchingActorDocumentOverWire()
    {
        var resolver = _b.Services.GetRequiredService<IInboundKeyResolver>();
        var key = await resolver.ResolveAsync(AliceKeyId);
        Assert.True(key is not null,
            "B's IInboundKeyResolver should resolve alice's key by fetching A's actor doc over the wire");
    }

    // --- Negative: an unsigned inbox POST is rejected with 401 ------------------

    [Fact]
    public async Task Follow_UntouchedBySignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // A plain (unsigned) POST to B's inbox: no Signature header → 401.
        using var http = new HttpClient(_b.CreateHandler());
        using var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        var response = await http.PostAsync(
            $"https://{BHost}/ap/v1/u/{Bob}/inbox", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Negative: a tampered signature is rejected with 401 -------------------

    [Fact]
    public async Task Follow_TamperedSignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // Deliver to a capture handler that records the signed request, so we can replay it tampered.
        using var capture = new CaptureHandler(_b.CreateHandler());
        using var captureClient = BuildDeliveryClient(AliceActorIri, _aliceKey, capture);
        // (captureClient's transport is the CaptureHandler, which forwards to B's TestServer.)
        _ = await captureClient.DeliverAsync(BobInboxIri, follow);
        var captured = Assert.Single(capture.Captured);

        // Tamper with the body: the original Digest header no longer matches the (changed) body →
        // validation fails → 401. Reuse the signed request's Date + Signature message headers and
        // Digest + Content-Type content headers; only the body changes.
        var tampered = new HttpRequestMessage(HttpMethod.Post, captured.RequestUri!)
        {
            // The body is tampered (a trailing space changes the bytes → the digest mismatches).
            Content = new StringContent(json + " "),
        };

        // Copy the signature-relevant message headers (Date + Signature). Skip Host: HttpClient sets it.
        foreach (var key in new[] { "Date", "Signature" })
        {
            if (captured.Headers.TryGetValue(key, out var values))
            {
                tampered.Headers.TryAddWithoutValidation(key, values);
            }
        }

        // Copy the content headers (Digest + Content-Type) verbatim.
        tampered.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        if (captured.ContentHeaders.TryGetValue("Digest", out var digests))
        {
            tampered.Content.Headers.TryAddWithoutValidation("Digest", digests);
        }

        using var http = new HttpClient(_b.CreateHandler());
        var response = await http.SendAsync(tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Seeds a persistence provider with a single actor (Person) + a real EC key.
    /// The actor's <c>publicKey</c> extension carries the real JWK (so a remote resolver can verify).
    /// Returns the key, the actor IRI (string + <see cref="Iri"/>), and the key IRI.
    /// </summary>
    private static (KeyPair Key, string ActorIriString, Iri ActorIri, Iri KeyId) Seed(
        InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIriString = $"https://{host}/ap/v1/u/{handle}";
        var actorIri = new Iri(actorIriString);
        var keyId = new Iri($"{actorIriString}#key-1");

        var key = KeyPairGenerator.GenerateEcP256(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIriString,
            // The real JWK, so a remote instance can reconstruct the public key and verify signatures.
            kty = "EC",
            crv = "P-256",
            x = ExtractJwkComponent(key, "x"),
            y = ExtractJwkComponent(key, "y"),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        return (key, actorIriString, actorIri, keyId);
    }

    private static string ExtractJwkComponent(KeyPair key, string name)
    {
        // The JWK is the canonical serialization; pull the component out of it.
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }


    /// <summary>
    /// Builds a delivery <see cref="IActivityPubClient"/> signed with the given key (as the given
    /// actor), whose transport is the given <paramref name="handler"/>.
    /// </summary>
    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as the given
    /// <paramref name="handle"/>) routes to <paramref name="targetServer"/> — i.e. B's fetcher
    /// reaches A's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair bobKey, TestServer targetServer)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var bobActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(bobActorIri, bobKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = bobActorIri, EnableRetry = false },
            targetServer.CreateHandler());

        return new IrisActorDocumentFetcher(client);
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, optionally
    /// overriding the <see cref="IActorDocumentFetcher"/> (for the federation wiring).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null)
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
                    // The instance actor is the federation signing identity (outbound fetches).
                    opts.InstanceActorId = new Iri($"https://{host}/ap/v1/u/{handle}");
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);

                if (fetcher is not null)
                {
                    // Override the default fetcher (which would use a real HttpClientHandler and fail
                    // to resolve the in-process host) with one wired to the other TestServer.
                    s.AddSingleton<IActorDocumentFetcher>(fetcher);
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

    private static Follow BuildFollow(Iri actorIri, Iri targetIri)
    {
        // Multi-valued Actor/Object: set via an object initializer of Links (Rule 2 — never a
        // positional constructor; Rule 3 — read multi-valued as IEnumerable).
        var follow = new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
        return follow;
    }

    /// <summary>
    /// A handler that records the signed request (headers + URI) and forwards it to the inner handler.
    /// Forwarding goes through an <see cref="HttpClient"/> over the inner handler (the handler's
    /// <c>SendAsync</c> is protected and cannot be invoked through a base-typed reference).
    /// </summary>
    private sealed class CaptureHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpClient _forward = new(inner, disposeHandler: true);

        /// <summary>
        /// The captured (signed) requests, in order.
        /// </summary>
        public List<CapturedRequest> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest
            {
                RequestUri = request.RequestUri,
                Headers = new Dictionary<string, IList<string>>(),
                ContentHeaders = new Dictionary<string, IList<string>>(),
            };
            foreach (var header in request.Headers)
            {
                captured.Headers[header.Key] = header.Value.ToList();
            }

            if (request.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    captured.ContentHeaders[header.Key] = header.Value.ToList();
                }
            }

            Captured.Add(captured);

            // Forward a clone: the outer HttpClient has already marked the original request as
            // sent, so re-sending the same instance through _forward would throw. A clone carries
            // the same method/URI/headers/content (and the signing is already applied upstream).
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } sourceContent)
            {
                var body = await sourceContent.ReadAsByteArrayAsync(cancellationToken);
                var clonedContent = new ByteArrayContent(body);
                foreach (var header in sourceContent.Headers)
                {
                    clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = clonedContent;
            }

            return await _forward.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _forward.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A captured HTTP request (URI + headers) for replaying with a tampered body.
    /// </summary>
    private sealed class CapturedRequest
    {
        public Uri? RequestUri { get; init; }

        public Dictionary<string, IList<string>> Headers { get; init; } = new();

        public Dictionary<string, IList<string>> ContentHeaders { get; init; } = new();
    }
}
