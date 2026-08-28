using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 integration test for the <strong>remote collection fetch through the cache</strong> slice:
/// the server's outbound path fetches a single page of a <em>remote</em> actor's collection over the
/// wire (signed), reading through the Phase 3 <see cref="CollectionPageCache"/> so a page is fetched
/// once and reused within the TTL, and an absent (non-page) result is not cached.
/// </summary>
/// <remarks>
/// Topology: a single live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> (b.domain.local,
/// bob) hosts the real paged collection endpoints, serving bob's 4-item outbox. The
/// <see cref="IRemoteCollectionFetcher"/> under test is wired (via the client factory + a
/// <see cref="LazyHandler"/> over B's handler) to fetch from B over a genuine HTTP stack — so each
/// fetch is a real signed round-trip. The outbox is paged at a size of 2 (<c>?limit=2</c>), so:
/// <list type="bullet">
/// <item>
/// Page 1 (the plain collection IRI) is served as an <c>OrderedCollection</c> (carrying <c>first</c>) —
/// not a page — so fetching it yields null and is not cached.
/// </item>
/// <item>
/// Page 2 (the <c>?page=2</c> IRI) is served as an <c>OrderedCollectionPage</c> (the last page, holding
/// the two oldest items, with <c>prev</c> and no <c>next</c>) and is cached under its page IRI.
/// </item>
/// </list>
/// The test asserts the miss→fetch→cache path, the per-page-IRI caching, the absent-result-not-cached
/// path (the plain collection IRI), and the <c>bypassCache</c> read bypass with write-back.
/// </remarks>
public sealed class RemoteCollectionFetcherIntegrationTests : IDisposable
{
    private const string BHost = "b.domain.local";
    private const string Bob = "bob";
    private const int OutboxItems = 4;
    private const int PageSize = 2;

    private readonly TestServer _b;
    private readonly IRemoteCollectionFetcher _fetcher;
    private readonly CollectionPageCache _cache;

    private readonly Iri BobActorIri;
    private readonly Iri BobOutboxIri;
    private readonly Iri BobOutboxPage2;

    public RemoteCollectionFetcherIntegrationTests()
    {
        var persistence = new InMemoryPersistenceProvider();
        Seed(persistence);

        _b = StartServer(persistence);

        // The fetcher under test: a real signed client (signed as bob) whose transport routes to B's
        // in-process TestServer, reading through the standalone CollectionPageCache (the same instance
        // the server's DI registers).
        _cache = new CollectionPageCache();
        var keyStore = _b.Services.GetRequiredService<IKeyStore>();
        var keyProvider = _b.Services.GetRequiredService<IKeyProvider>();
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions
            {
                ActorId = new Iri($"https://{BHost}/ap/v1/u/{Bob}"),
                EnableRetry = false,
            },
            new LazyHandler(() => _b.CreateHandler()));

        _fetcher = new IrisRemoteCollectionFetcher(client, _cache);

        BobActorIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        BobOutboxIri = new Iri($"{BobActorIri}/outbox");
        BobOutboxPage2 = new Iri($"{BobOutboxIri}/?limit={PageSize}&page=2");
    }

    public void Dispose() => _b.Dispose();

    // --- A page fetch is cached (fetched once, reused) --------------------------

    [Fact]
    public async Task GetPage_Miss_FetchesOverWire_ThenCached()
    {
        var first = await _fetcher.GetCollectionPageAsync(BobOutboxPage2);

        // The first read is a miss: it fetches the page over the wire and flattens it.
        Assert.NotNull(first);
        Assert.Equal(2, first!.Items.Count); // page 2 of bob's 4-item outbox at limit 2
        Assert.Equal(OutboxItems, first.TotalItems);
        Assert.True(first.IsLastPage); // page 2 is the last page (no next)
        Assert.Equal($"{BobOutboxIri}/?page=1", first.PrevPage?.Value);
        Assert.Null(first.NextPage);
        Assert.Equal(1, _cache.Count);

        // The second read is served from the cache (same instance, not re-fetched).
        var second = await _fetcher.GetCollectionPageAsync(BobOutboxPage2);
        Assert.NotNull(second);
        Assert.Same(first, second);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public async Task GetPage_ForceRefresh_BypassesCachedRead()
    {
        var first = await _fetcher.GetCollectionPageAsync(BobOutboxPage2); // populates the cache
        Assert.Equal(1, _cache.Count);

        // A bypassCache bypasses the cached read (re-fetches over the wire) but writes the page back.
        var refreshed = await _fetcher.GetCollectionPageAsync(BobOutboxPage2, bypassCache: true);

        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed!.Items.Count);
        // A distinct instance is returned (the page was re-fetched), but the cache still holds one entry.
        Assert.NotSame(first, refreshed);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public async Task GetPage_PlainCollectionIri_IsNotAPage_AndNotCached()
    {
        // Fetching the plain collection IRI returns the OrderedCollection (not a page) → null, not
        // cached (the caller would follow the collection's `first` link to reach page 1).
        var result = await _fetcher.GetCollectionPageAsync(BobOutboxIri);
        Assert.Null(result);
        Assert.Equal(0, _cache.Count);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Seeds the persistence provider: actor bob with a real EC key and a 4-item outbox (newest-first:
    /// note-4, note-3, note-2, note-1).
    /// </summary>
    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var (_, actorIri, _) = TestSeeder.SeedPersonWithKey(persistence, BHost, Bob);

        var outbox = (InMemoryActivityStore)persistence.Activities;
        // Outbox items, added oldest→newest so AddToOutboxAsync (inserts at index 0) leaves the list
        // newest-first: note-4, note-3, note-2, note-1.
        for (var i = 1; i <= OutboxItems; i++)
        {
            var create = new Create
            {
                Id = $"{actorIri.Value}/activities/create-{i}",
                Actor = [new Link { Href = new Uri(actorIri.Value) }],
                Object = [new Note { Id = $"{actorIri.Value}/objects/note-{i}", Content = [$"note {i}"] }],
            };
            outbox.AddToOutboxAsync(actorIri, create).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> hosting the real paged collection endpoints (bob's
    /// outbox). The local actor's key is bound to the <c>IKeyStore</c> and registered with the
    /// <c>IKeyProvider</c> so the outbound fetcher (signed as bob) can sign its requests.
    /// </summary>
    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = persistence,
        });

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request (breaks the B-handler wiring chicken-and-egg) and clones each request so the inner
    /// pipeline (which may retry) never re-sends the same message instance.
    /// </summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> innerFactory) : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory = innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                clone.Content = new ByteArrayContent(content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return _client.SendAsync(clone, cancellationToken);
        }
    }
}
