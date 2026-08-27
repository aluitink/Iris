using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for wiring the client caches into the call paths:
/// <see cref="ActivityPubClient"/> (actor + collection-page caches) and
/// <see cref="WebFingerClient"/> (WebFinger cache). Verifies that a second read for the same
/// IRI is served from the cache (network hit count stays at one) and that
/// <see cref="CollectionQuery.BypassCache"/> forces a re-fetch.
/// </summary>
public class CacheWiringTests
{
    private const string ActorIri = "https://a.domain.local/users/alice";

    /// <summary>
    /// A handler that serves a pre-configured JSON response and counts how many times it is hit.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private int _hits;

        public CountingHandler(string json)
        {
            _json = json;
        }

        /// <summary>
        /// The number of requests handled.
        /// </summary>
        public int Hits => Volatile.Read(ref _hits);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _hits);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/activity+json"),
            };
            return Task.FromResult(response);
        }
    }

    private static string ActorJson() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{ActorIri}}",
          "type": "Person",
          "name": "Alice"
        }
        """;

    private const string CollectionIri = "https://a.domain.local/c/outbox";
    private const string FirstIri = "https://a.domain.local/c/outbox/first";

    private static string CollectionJson() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{CollectionIri}}",
          "type": "OrderedCollection",
          "first": "{{FirstIri}}"
        }
        """;

    private static string PageJson() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{FirstIri}}",
          "type": "OrderedCollectionPage",
          "totalItems": 1,
          "items": [ { "id": "https://a.domain.local/n/1", "type": "Note" } ]
        }
        """;

    // A handler that routes: collection → CollectionJson, first page → PageJson, and counts hits.
    private sealed class RoutingCountingHandler : HttpMessageHandler
    {
        private int _hits;

        /// <summary>
        /// The number of requests handled.
        /// </summary>
        public int Hits => Volatile.Read(ref _hits);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _hits);
            var uri = request.RequestUri!.ToString();
            var json = uri.EndsWith("/c/outbox/first") ? PageJson() : CollectionJson();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
            };
            return Task.FromResult(response);
        }
    }

    // --- Actor cache -----------------------------------------------------------

    [Fact]
    public async Task GetObjectAsync_WithActorCache_SecondReadHitsCache()
    {
        var handler = new CountingHandler(ActorJson());
        var cache = new ActorCache();
        using var client = new ActivityPubClient(new HttpClient(handler), cache, null);

        var first = await client.GetObjectAsync(new Iri(ActorIri));
        var second = await client.GetObjectAsync(new Iri(ActorIri));

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Second read served from cache: network hit exactly once.
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task GetObjectAsync_WithoutCache_EveryReadHitsNetwork()
    {
        var handler = new CountingHandler(ActorJson());
        using var client = new ActivityPubClient(new HttpClient(handler));

        await client.GetObjectAsync(new Iri(ActorIri));
        await client.GetObjectAsync(new Iri(ActorIri));

        Assert.Equal(2, handler.Hits);
    }

    [Fact]
    public async Task GetActorAsync_WithActorCache_SecondReadHitsCache()
    {
        var handler = new CountingHandler(ActorJson());
        var cache = new ActorCache();
        using var client = new ActivityPubClient(new HttpClient(handler), cache, null);

        var first = await client.GetActorAsync(new Iri(ActorIri));
        var second = await client.GetActorAsync(new Iri(ActorIri));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.Hits);
    }

    // --- Collection page cache -------------------------------------------------

    [Fact]
    public async Task GetCollectionAsync_WithPageCache_SecondEnumerationHitsCache()
    {
        var handler = new RoutingCountingHandler();
        var cache = new CollectionPageCache();
        using var client = new ActivityPubClient(new HttpClient(handler), null, cache);

        int firstPages = 0;
        await foreach (var _ in client.GetCollectionAsync(new Iri(CollectionIri)))
        {
            firstPages++;
        }

        int secondPages = 0;
        await foreach (var _ in client.GetCollectionAsync(new Iri(CollectionIri)))
        {
            secondPages++;
        }

        Assert.Equal(1, firstPages);
        Assert.Equal(1, secondPages);
        // The collection document is fetched on each enumeration (2 hits), but the *page*
        // fetch is cached, so the second enumeration does not re-fetch it. Total = 3.
        // Without the page cache this would be 4 (collection x2 + page x2).
        Assert.Equal(3, handler.Hits);
    }

    [Fact]
    public async Task GetCollectionAsync_BypassCache_RefetchesPage()
    {
        var handler = new RoutingCountingHandler();
        var cache = new CollectionPageCache();
        using var client = new ActivityPubClient(new HttpClient(handler), null, cache);

        await foreach (var _ in client.GetCollectionAsync(new Iri(CollectionIri)))
        {
            // warm the cache
        }

        var hitsBeforeBypass = handler.Hits;

        await foreach (var _ in client.GetCollectionAsync(new Iri(CollectionIri), new CollectionQuery(BypassCache: true)))
        {
            // bypass
        }

        // Bypass forced a re-fetch of the page (hits increased by at least 1).
        Assert.True(handler.Hits > hitsBeforeBypass, $"expected a re-fetch on bypass, hits stayed at {hitsBeforeBypass}");
    }

    // --- WebFinger cache -------------------------------------------------------

    [Fact]
    public async Task ResolveActorAsync_WithCache_SecondReadHitsCache()
    {
        var json = """
        {
          "subject": "acct:alice@a.domain.local",
          "links": [
            { "rel": "self", "type": "application/activity+json", "href": "https://a.domain.local/users/alice" }
          ]
        }
        """;
        var handler = new CountingHandler(json);
        var cache = new WebFingerCache();
        var client = new WebFingerClient(new HttpClient(handler), cache);

        var first = await client.ResolveActorAsync("@alice@a.domain.local");
        var second = await client.ResolveActorAsync("@alice@a.domain.local");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(new Iri("https://a.domain.local/users/alice"), first);
        Assert.Equal(new Iri("https://a.domain.local/users/alice"), second);
        // Second read served from cache: network hit exactly once.
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task ResolveActorAsync_WithoutCache_EveryReadHitsNetwork()
    {
        var json = """
        {
          "subject": "acct:alice@a.domain.local",
          "links": [
            { "rel": "self", "type": "application/activity+json", "href": "https://a.domain.local/users/alice" }
          ]
        }
        """;
        var handler = new CountingHandler(json);
        var client = new WebFingerClient(new HttpClient(handler));

        await client.ResolveActorAsync("@alice@a.domain.local");
        await client.ResolveActorAsync("@alice@a.domain.local");

        Assert.Equal(2, handler.Hits);
    }

    // --- Factory wiring --------------------------------------------------------

    [Fact]
    public async Task Factory_WithCaches_WiresActorCache()
    {
        var keyStore = new InMemoryKeyStore();
        using var keyPair = KeyPairGenerator.GenerateEcP256(new Iri("https://a.domain.local/users/factory-tester#key-1"));
        keyStore.PutKey(keyPair);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var signer = new HttpSignatureSigner(keyStore);

        var actorIri = new Iri("https://a.domain.local/users/factory-tester");
        // Map the actor to its key so the SigningHandler can resolve the identity.
        keyProvider.RegisterKey(actorIri, keyPair.KeyId);

        var handler = new CountingHandler(ActorJson());
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var options = new ActivityPubClientOptions
        {
            ActorId = actorIri,
            Caches = new ClientCaches(Actors: new ActorCache()),
        };

        using var client = factory.Create(options, handler);
        await client.GetObjectAsync(new Iri(ActorIri));
        await client.GetObjectAsync(new Iri(ActorIri));

        // The actor cache is wired through the factory: second read served from cache.
        Assert.Equal(1, handler.Hits);
    }
}
