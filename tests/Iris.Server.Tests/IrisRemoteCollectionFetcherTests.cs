using System.Net;
using System.Text;
using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using ClientCollectionPage = Iris.Core.CollectionPage;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for <see cref="IrisRemoteCollectionFetcher"/>: it reads remote collection pages through
/// the Phase 3 <see cref="CollectionPageCache"/> (keyed by the page IRI) so a page is fetched once and
/// reused across lookups, an absent result is not cached (retried), and a <c>bypassCache</c> bypasses
/// the read while writing back.
/// </summary>
/// <remarks>
/// The outbound transport is a fake <see cref="IActivityPubClient"/> that returns a fixed page (or
/// null / a non-page) and counts <see cref="IActivityPubClient.SendAsync"/> calls, so cache
/// hit/miss/bypass behavior is observable without a network.
/// </remarks>
public sealed class IrisRemoteCollectionFetcherTests
{
    private const string BHost = "b.domain.local";
    private const string PageIri = $"https://{BHost}/u/bob/outbox/first";

    [Fact]
    public async Task GetPage_Miss_FetchesAndCaches()
    {
        var client = new StubCollectionClient(PageDoc("one", "two"));
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);

        var page = await sut.GetCollectionPageAsync(new Iri(PageIri));

        Assert.NotNull(page);
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(1, client.SendCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetPage_FreshHit_IsCached()
    {
        var client = new StubCollectionClient(PageDoc("one", "two"));
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);
        var pageIri = new Iri(PageIri);

        var first = await sut.GetCollectionPageAsync(pageIri);
        var second = await sut.GetCollectionPageAsync(pageIri);

        // Same page served from the cache on the second call; the client is not hit again.
        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, client.SendCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetPage_Absent_IsNotCached()
    {
        var client = new StubCollectionClient(null); // the remote page does not exist (404)
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);
        var pageIri = new Iri($"{BHost}/u/nobody/outbox/first");

        var first = await sut.GetCollectionPageAsync(pageIri);
        var second = await sut.GetCollectionPageAsync(pageIri);

        // Absent results are never cached, so the second call retries the fetch.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, client.SendCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task GetPage_NotAPage_IsNotCached()
    {
        // A plain collection IRI returns an OrderedCollection (not a page) → null, not cached.
        var client = new StubCollectionClient(CollectionDoc());
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);
        var collectionIri = new Iri($"{BHost}/u/bob/outbox");

        var first = await sut.GetCollectionPageAsync(collectionIri);
        var second = await sut.GetCollectionPageAsync(collectionIri);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, client.SendCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task GetPage_ForceRefresh_BypassesReadAndWritesBack()
    {
        var client = new StubCollectionClient(PageDoc("one", "two"));
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);
        var pageIri = new Iri(PageIri);

        await sut.GetCollectionPageAsync(pageIri); // populates the cache
        Assert.Equal(1, client.SendCalls);

        // A bypassCache bypasses the cached read (re-fetches) but writes the page back.
        var refreshed = await sut.GetCollectionPageAsync(pageIri, bypassCache: true);

        Assert.NotNull(refreshed);
        Assert.Equal(2, client.SendCalls); // the read was bypassed → a second network fetch
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetPage_PreservesNextAndPrevLinksAndTotal()
    {
        var client = new StubCollectionClient(PageWithLinks());
        var cache = new CollectionPageCache();
        var sut = new IrisRemoteCollectionFetcher(client, cache);

        var page = await sut.GetCollectionPageAsync(new Iri(PageIri));

        Assert.NotNull(page);
        Assert.Equal($"https://{BHost}/u/bob/outbox/2", page!.NextPage?.Value);
        Assert.Equal($"https://{BHost}/u/bob/outbox/first-prev", page.PrevPage?.Value);
        Assert.Equal(3, page.TotalItems);
        Assert.False(page.IsLastPage);
    }

    // --- Wire documents --------------------------------------------------------

    /// <summary>A page with two Note items.</summary>
    private static string PageDoc(string one, string two) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{BHost}}/u/bob/outbox/first",
          "type": "OrderedCollectionPage",
          "partOf": "https://{{BHost}}/u/bob/outbox",
          "totalItems": 3,
          "startIndex": 1,
          "items": [
            { "id": "https://{{BHost}}/n/1", "type": "Note", "content": "{{one}}" },
            { "id": "https://{{BHost}}/n/2", "type": "Note", "content": "{{two}}" }
          ],
          "next": "https://{{BHost}}/u/bob/outbox/2"
        }
        """;

    /// <summary>A page with explicit <c>prev</c> and <c>next</c> links and a total count.</summary>
    private static string PageWithLinks() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{BHost}}/u/bob/outbox/first",
          "type": "OrderedCollectionPage",
          "partOf": "https://{{BHost}}/u/bob/outbox",
          "totalItems": 3,
          "startIndex": 1,
          "items": [
            { "id": "https://{{BHost}}/n/1", "type": "Note", "content": "one" }
          ],
          "prev": "https://{{BHost}}/u/bob/outbox/first-prev",
          "next": "https://{{BHost}}/u/bob/outbox/2"
        }
        """;

    /// <summary>A plain collection document (not a page) — the first page is at its <c>first</c> link.</summary>
    private static string CollectionDoc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "https://{{BHost}}/u/bob/outbox",
          "type": "OrderedCollection",
          "totalItems": 3,
          "first": "https://{{BHost}}/u/bob/outbox/first"
        }
        """;

    // --- Fake transport --------------------------------------------------------

    /// <summary>
    /// A fake <see cref="IActivityPubClient"/> whose <see cref="IActivityPubClient.SendAsync"/> returns a
    /// fixed activity+json body (or null / a 404) and counts calls.
    /// </summary>
    private sealed class StubCollectionClient(string? json) : IActivityPubClient
    {
        private readonly string? _json = json;

        /// <summary>The number of times <see cref="IActivityPubClient.SendAsync"/> has been invoked.</summary>
        public int SendCalls { get; private set; }

        /// <inheritdoc/>
        public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default)
            => Task.FromResult<IObject?>(null);

        /// <inheritdoc/>
        public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default)
            => Task.FromResult<Actor?>(null);

        /// <inheritdoc/>
        public Task<int> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default)
            => Task.FromResult(202);

        /// <inheritdoc/>
        public Task<int> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default)
            => Task.FromResult(202);

        /// <inheritdoc/>
        public Task<int> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default)
            => Task.FromResult(202);

        /// <inheritdoc/>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            SendCalls++;
            if (_json is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
            };
            return Task.FromResult(response);
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<ClientCollectionPage> GetCollectionAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<ClientCollectionPage>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
            Iri collectionId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
            Iri communityId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
            Iri actorId,
            CollectionQuery? query = null,
            CancellationToken ct = default)
            => EmptyAsync<IObjectOrLink>(ct);

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<T> EmptyAsync<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
