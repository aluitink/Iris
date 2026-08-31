using System.Net.Http.Headers;
using Iris.Core;

namespace Iris.Client.Tests;

/// <summary>
/// Integration tests: the <strong>real</strong> <see cref="Iris.Client.ActivityPubClient"/> (built by the
/// real <see cref="Iris.Client.ActivityPubClientFactory"/>, so its full pipeline —
/// <see cref="Iris.Client.Pipeline.RetryHandler"/> → <see cref="Iris.Client.Pipeline.JsonLdHandler"/> →
/// <see cref="Iris.Client.Pipeline.SigningHandler"/> — is active) exercised against a live in-process
/// <see cref="FakeActivityPubServer"/> over a genuine HTTP stack.
/// </summary>
/// <remarks>
/// These prove the client's end-to-end behavior: real signing (a <c>Signature</c> header is sent and
/// the server sees it), real content negotiation (<c>Accept</c>), WebFinger discovery, multi-page
/// collection enumeration, cache hit, and retry on transient failure. A full-fidelity Iris server
/// (signature validation, persistence) lands in Phase 3/4; this fake server only serves well-formed
/// documents so the client side can be proven now.
/// </remarks>
public class ClientServerIntegrationTests : IDisposable
{
    private const string ServerHost = "b.domain.local";
    private const string ServerActor = "bob";
    private const string LocalHost = "a.domain.local";
    private const string LocalActor = "alice";

    private readonly FakeActivityPubServer _server;

    public ClientServerIntegrationTests()
    {
        _server = FakeActivityPubServer.Start(ServerHost, ServerActor);
    }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Builds the real signed client via the real factory, with the given fake server as the transport.
    /// </summary>
    private IActivityPubClient CreateClient(FakeActivityPubServer server, ActivityPubClientOptions? options = null)
    {
        var keyStore = new InMemoryKeyStore();
        // Not disposed here: the key must live for the whole lifetime of the client (its signer
        // holds it). It is owned by the key store, which the factory retains.
        var keyPair = KeyPairGenerator.GenerateRsa(new Iri($"https://{LocalHost}/u/{LocalActor}#key-1"));
        keyStore.PutKey(keyPair);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var localActorIri = new Iri($"https://{LocalHost}/u/{LocalActor}");
        keyProvider.RegisterKey(localActorIri, keyPair.KeyId);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            options ?? new ActivityPubClientOptions { ActorId = localActorIri },
            server.Handler);
    }

    // --- Signing over the wire -------------------------------------------------

    [Fact]
    public async Task GetActor_SendsSignatureHeader_ServerSeesIt()
    {
        using var client = CreateClient(_server);
        var actor = await client.GetActorAsync(_server.ActorIri);

        Assert.NotNull(actor);
        Assert.Equal($"https://{ServerHost}/u/{ServerActor}", actor!.Id);

        // The GET reached the server carrying a real Signature header (signed by the pipeline).
        var request = Assert.Single(_server.Requests, r => r.Path == $"/u/{ServerActor}");
        Assert.True(request.IsSigned, "expected a Signature header on the actor GET");
        Assert.Equal(HttpMethod.Get.Method, request.Method);
    }

    [Fact]
    public async Task GetActor_AdvertisesBothAcceptMediaTypes()
    {
        using var client = CreateClient(_server);
        await client.GetActorAsync(_server.ActorIri);

        var request = Assert.Single(_server.Requests, r => r.Path == $"/u/{ServerActor}");
        // JsonLdHandler advertises both media types on bodyless requests (activity+json first).
        Assert.StartsWith("application/activity+json", request.Accept!);
        Assert.Contains("application/ld+json", request.Accept!);
    }

    // --- WebFinger discovery ---------------------------------------------------

    [Fact]
    public async Task WebFinger_ResolvesActorIri()
    {
        using var client = CreateClient(_server);
        var webFinger = new WebFingerClient(new HttpClient(_server.Handler));

        // WebFinger for bob@b.domain.local resolves to the actor document IRI.
        var resolved = await webFinger.ResolveActorAsync($"acct:{ServerActor}@{ServerHost}", dialScheme: "https");

        Assert.Equal(_server.ActorIri, resolved);
        // The WebFinger request reached the server.
        Assert.Equal(1, _server.HitsOnPath("/.well-known/webfinger"));
    }

    // --- Paged enumeration -----------------------------------------------------

    [Fact]
    public async Task GetCollection_FollowsPagesEndToEnd()
    {
        using var client = CreateClient(_server);
        var pages = new List<CollectionPage>();
        await foreach (var page in client.GetCollectionAsync(_server.OutboxIri))
        {
            pages.Add(page);
        }

        // Two pages served by the fake server.
        Assert.Equal(2, pages.Count);
        Assert.Equal(2, pages[0].Items.Count);
        Assert.Single(pages[1].Items);
        Assert.Equal(3, pages[0].TotalItems);
        // The collection doc + first page + page2 were each fetched once.
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}/outbox"));
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}/outbox/first"));
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}/outbox/2"));
    }

    // --- Caching ---------------------------------------------------------------

    [Fact]
    public async Task GetActor_ServedFromCacheOnSecondRead()
    {
        var options = new ActivityPubClientOptions
        {
            ActorId = new Iri($"https://{LocalHost}/u/{LocalActor}"),
            Caches = new ClientCaches(Actors: new ActorCache()),
        };
        using var client = CreateClient(_server, options);

        var first = await client.GetActorAsync(_server.ActorIri);
        var second = await client.GetActorAsync(_server.ActorIri);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Second read is served from the actor cache: the server saw exactly one actor GET.
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}"));
    }

    [Fact]
    public async Task GetCollection_PageCacheHitOnSecondEnumeration()
    {
        var options = new ActivityPubClientOptions
        {
            ActorId = new Iri($"https://{LocalHost}/u/{LocalActor}"),
            Caches = new ClientCaches(CollectionPages: new CollectionPageCache()),
        };
        using var client = CreateClient(_server, options);

        int firstCount = 0;
        await foreach (var _ in client.GetCollectionAsync(_server.OutboxIri))
        {
            firstCount++;
        }

        int secondCount = 0;
        await foreach (var _ in client.GetCollectionAsync(_server.OutboxIri))
        {
            secondCount++;
        }

        Assert.Equal(2, firstCount);
        Assert.Equal(2, secondCount);
        // Pages are cached: each page was fetched once across both enumerations.
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}/outbox/first"));
        Assert.Equal(1, _server.HitsOnPath($"/u/{ServerActor}/outbox/2"));
    }

    // --- Retry -----------------------------------------------------------------

    [Fact]
    public async Task GetActor_RetriesTransient503ThenSucceeds()
    {
        // A flaky fake server: first hit on the actor path is a 503, then it serves normally.
        using var flakyServer = FakeActivityPubServer.Start("c.domain.local", ServerActor, flaky: true);
        using var client = CreateClient(flakyServer);

        var actor = await client.GetActorAsync(flakyServer.ActorIri);

        // RetryHandler retried the transient 503 and got the actor on the second attempt.
        Assert.NotNull(actor);
        Assert.Equal($"https://c.domain.local/u/{ServerActor}", actor!.Id);
        Assert.Equal(2, flakyServer.HitsOnPath($"/u/{ServerActor}"));
    }

    // --- 404: not-found is a final answer (no retry, null result) -----------------

    [Fact]
    public async Task GetObject_UnknownObject_404_ReturnsNull()
    {
        using var client = CreateClient(_server);

        // An IRI the fake server does not serve → 404 (the terminal handler's fallback).
        var objectIri = new Iri($"https://{ServerHost}/n/does-not-exist");
        var result = await client.GetObjectAsync(objectIri);

        // The client maps a 404 to null (a not-found is an expected condition, not an error).
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActor_UnknownActor_404_ReturnsNull()
    {
        using var client = CreateClient(_server);

        // A handle the instance does not host → 404.
        var unknownActorIri = new Iri($"https://{ServerHost}/u/{ServerActor}-ghost");
        var result = await client.GetActorAsync(unknownActorIri);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetObject_404_IsNotRetried()
    {
        using var client = CreateClient(_server);

        var objectIri = new Iri($"https://{ServerHost}/n/does-not-exist");
        await client.GetObjectAsync(objectIri);

        // A 404 is not transient: the RetryHandler makes exactly one attempt (no retry storm on
        // not-found).
        Assert.Equal(1, _server.HitsOnPath("/n/does-not-exist"));
    }

    // --- 429: server rate-limit → client honors Retry-After (Phase 17.4 + 18.1) ------------

    [Fact]
    public async Task GetActor_Server429WithRetryAfter_ClientHonorsHeader()
    {
        // A rate-limiting fake server: the first request is served, the second gets 429 +
        // Retry-After: 1 (delta-seconds form), and the third is served (the rate-limit window
        // expired). The client's RetryHandler (Phase 18.1) honors the Retry-After header and
        // retries; the third request succeeds.
        //
        // To make the client's FIRST request hit the 429, we use a server that 429s the first
        // request and serves the second (rateLimitAfter: 0 means "no requests are served before
        // the rate-limit kicks in" — but that's not supported, so we use a different approach:
        // we make a probe request first to consume the "served" slot, then the client's first
        // request will be the 429).
        using var rateLimitedServer = FakeActivityPubServer.Start(
            "rate-limit.domain.local", ServerActor, rateLimitAfter: 1, rateLimitResumeAfter: 1);
        
        // Consume the "served" slot so the client's first request will be the 429.
        var probe = new System.Net.Http.HttpClient(rateLimitedServer.Handler);
        var probeResponse = await probe.GetAsync(rateLimitedServer.ActorIri.Value);
        Assert.Equal(200, (int)probeResponse.StatusCode);
        
        using var client = CreateClient(rateLimitedServer);
        var actor = await client.GetActorAsync(rateLimitedServer.ActorIri);

        // The client's first request was 429 + Retry-After: 1; the RetryHandler honored the
        // header and retried; the second request was served.
        Assert.NotNull(actor);
        Assert.Equal(rateLimitedServer.ActorIri.Value, actor!.Id);
        // 1 probe + 2 client requests (first 429, second served) = 3 total.
        Assert.Equal(3, rateLimitedServer.HitsOnPath($"/u/{ServerActor}"));
    }

    // --- Proxy fallback: a direct 401 is rerouted through the home instance's proxy -
    //
    // The browser has no signed outbound, so a direct GET to a remote that rejects its signature (401)
    // is retried through the home instance's proxy endpoint. This proves the outermost pipeline stage
    // (ProxyFallbackHandler, wired in by the real factory) end-to-end: the direct attempt 401s, the
    // handler strips the signature and forwards a Basic-auth POST to the proxy, which relays the remote
    // doc back to the caller.

    [Fact]
    public async Task GetActor_Direct401_FallsBackToProxy()
    {
        // B is the remote instance. The routing transport below returns 401 for a direct GET to it (the
        // remote rejects the client's signature — it cannot validate cross-origin) and 200 (relaying the
        // remote actor doc) for the proxy's Basic-auth POST to the home instance.
        const string RemoteHost = "remote.domain.local";
        using var remote = FakeActivityPubServer.Start(RemoteHost, ServerActor);

        var keyStore = new InMemoryKeyStore();
        var keyPair = KeyPairGenerator.GenerateRsa(new Iri($"https://{LocalHost}/u/{LocalActor}#key-1"));
        keyStore.PutKey(keyPair);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);
        var localActorIri = new Iri($"https://{LocalHost}/u/{LocalActor}");
        keyProvider.RegisterKey(localActorIri, keyPair.KeyId);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        // One transport serves both legs: a GET to the remote host 401s (direct attempt rejected); a
        // POST to the home proxy host with Basic auth relays the remote actor doc (the proxied leg).
        var transport = new ProxyRoutingTransport(
            remoteHost: RemoteHost,
            proxiedBody: remote.ActorDocJson);

        using var client = factory.Create(new ActivityPubClientOptions
        {
            ActorId = localActorIri,
            ProxyBaseUrl = new Iri($"https://{LocalHost}"),
            ProxyCredentials = new ProxyCredentials(LocalActor, "s3cret!"),
        }, transport);

        var actor = await client.GetActorAsync(remote.ActorIri);

        // The proxied response (the remote actor doc) is returned to the caller.
        Assert.NotNull(actor);
        Assert.Equal(remote.ActorIri.Value, actor!.Id);

        // The direct attempt was a GET to the remote (rejected 401); the fallback was a Basic-auth POST
        // to the home proxy endpoint.
        Assert.Equal(1, transport.DirectGetCount);
        var proxied = Assert.Single(transport.ProxyPosts);
        Assert.Equal("Basic", proxied.Scheme);
        Assert.EndsWith($"/ap/v1/proxy/{remote.ActorIri.Value}", proxied.Path, StringComparison.Ordinal);
    }
}
