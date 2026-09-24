using System.Net;
using System.Text;
using Iris.Client.Collections;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests.Collections;

/// <summary>
/// S84 regression: the Profile "Following" panel reads the actor's <c>/following</c> collection through
/// the client's <see cref="CollectionPageCache"/>. Before the fix the panel's initial load used a
/// cache-honoring read, so a follow made from another screen (directory, actor detail) left the stale,
/// pre-follow page in the cache and the panel kept showing "Not following anyone" until a full reload.
/// The fix makes the panel read the collection with <c>BypassCache</c> on initial load and on follow
/// changes. These tests pin the seam the fix relies on: a <c>BypassCache</c> re-read of the following
/// collection reflects a follow recorded after the cached (empty) read, whereas a plain cache-honoring
/// read can still return the stale page — the exact stale-vs-fresh delta the fix closes.
/// </summary>
public sealed class FollowingCollectionFreshReadTests : IDisposable
{
    private const string Base = "https://follow-fresh.domain.local";
    private const string FollowingIri = $"{Base}/ap/v1/u/alice/following";
    // The first page, resolved from the collection envelope's `first` link. Served separately so the
    // client's page walk (not the collection-document fast-path) is what consults the
    // CollectionPageCache — the path the Profile Following panel reads.
    private const string Page1Iri = $"{FollowingIri}?page=true";
    private const string TargetIri = $"{Base}/ap/v1/u/bob";

    private readonly List<string> _items = [];
    private readonly HttpClient _http;

    public FollowingCollectionFreshReadTests()
    {
        _http = new HttpClient(new FollowingCollectionHandler(_items));
    }

    public void Dispose() => _http.Dispose();

    private static async Task<IReadOnlyList<string>> ReadAsync(
        IActivityPubClient client, CollectionQuery? query)
    {
        var seen = new List<string>();
        await foreach (var page in client.GetCollectionAsync(new Iri(FollowingIri), query))
        {
            foreach (var item in page.Items)
            {
                var iri = item.ResolveObjectIri()?.Value;
                if (iri is not null)
                {
                    seen.Add(iri);
                }
            }
        }

        return seen;
    }

    [Fact]
    public async Task BypassReRead_AfterFollow_ReflectsNewFollower()
    {
        var cache = new CollectionPageCache();
        using var client = new ActivityPubClient(_http, null, cache);

        // 1. Initial panel load: the following collection is empty and the page is cached.
        Assert.Empty(await ReadAsync(client, null));

        // 2. A follow is recorded server-side (from the directory / actor detail).
        _items.Add(TargetIri);

        // 3. The panel's fixed initial/refresh load bypasses the cache and sees the new follower.
        var fresh = await ReadAsync(client, new CollectionQuery(BypassCache: true));
        Assert.Equal([TargetIri], fresh);
    }

    [Fact]
    public async Task CacheHonoringReRead_AfterFollow_CanStillReturnStalePage()
    {
        // Pins the failure mode the fix removes: within the page-cache TTL, a cache-honoring read of the
        // same collection IRI serves the pre-follow (empty) page. This is why the panel must bypass the
        // cache — a plain re-render would not have updated the list.
        var cache = new CollectionPageCache();
        using var client = new ActivityPubClient(_http, null, cache);

        Assert.Empty(await ReadAsync(client, null));

        _items.Add(TargetIri);

        var stale = await ReadAsync(client, null);
        Assert.Empty(stale);
    }

    /// <summary>
    /// Serves the following collection in the canonical Mastodon shape: a bare <c>OrderedCollection</c>
    /// envelope at <see cref="FollowingIri"/> carrying a string <c>first</c> link (no items of its own),
    /// plus a separate <c>OrderedCollectionPage</c> at <see cref="Page1Iri"/> whose <c>orderedItems</c>
    /// mirror <paramref name="items"/> on every request (so a bypass re-fetch observes the current
    /// state). This routes the client's page walk through the <see cref="CollectionPageCache"/> — the
    /// path the Profile Following panel reads — rather than the collection-document fast-path.
    /// </summary>
    private sealed class FollowingCollectionHandler(List<string> items) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Strip the `refresh=true` marker the client appends on bypass re-fetches, so the same
            // document is served for the cached and the fresh request.
            var raw = request.RequestUri?.AbsoluteUri;
            var uri = raw?.Split('&', 2).First(p => !p.StartsWith("refresh=", StringComparison.Ordinal));

            if (uri == FollowingIri)
            {
                var envelope = $$"""
                    {
                      "@context": "https://www.w3.org/ns/activitystreams",
                      "id": "{{FollowingIri}}",
                      "type": "OrderedCollection",
                      "totalItems": {{items.Count}},
                      "first": "{{Page1Iri}}"
                    }
                    """;
                return Task.FromResult(Ok(envelope));
            }

            if (uri == Page1Iri)
            {
                var itemLinks = string.Join(
                    ", ", items.Select(i => $$"""{ "id": "{{i}}", "type": "Person" }"""));
                var page = $$"""
                    {
                      "@context": "https://www.w3.org/ns/activitystreams",
                      "id": "{{Page1Iri}}",
                      "type": "OrderedCollectionPage",
                      "totalItems": {{items.Count}},
                      "orderedItems": [ {{itemLinks}} ]
                    }
                    """;
                return Task.FromResult(Ok(page));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
        };
    }
}
