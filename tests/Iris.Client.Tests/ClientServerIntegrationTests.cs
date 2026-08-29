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
        var resolved = await webFinger.ResolveActorAsync($"acct:{ServerActor}@{ServerHost}");

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
}
