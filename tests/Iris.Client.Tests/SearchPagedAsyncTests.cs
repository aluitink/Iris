using System.Net;
using System.Text;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Verifies <see cref="ActivityPubClient.SearchPagedAsync"/> walks the global-search result page by
/// page: the first request is the offset-0 page (an <c>OrderedCollection</c> whose <c>next</c> link is
/// carried in the deserialized object's extension data), and each subsequent request is exactly the
/// previous page's <c>next</c> IRI, until the server stops offering a <c>next</c>. The walk also
/// flattens each page into a <see cref="Iris.Core.Collections.CollectionPage"/> (items,
/// <c>NextPage</c>, <c>TotalItems</c>) and honors the <c>?type</c>/<c>?local</c> segments on every hop.
/// </summary>
public class SearchPagedAsyncTests
{
    private const string InstanceBase = "https://a.domain.local/ap/v1";

    private static readonly Iri Base = new(InstanceBase);

    [Fact]
    public async Task SearchPagedAsync_FollowsNextLinksUntilExhausted()
    {
        const string page1 = """
            {
              "type": "OrderedCollection",
              "id": "https://a.domain.local/ap/v1/search",
              "items": [ {"type": "Person", "id": "https://a.domain.local/ap/v1/u/alice", "preferredUsername": "alice"} ],
              "totalItems": 3,
              "first": "https://a.domain.local/ap/v1/search",
              "next": "https://a.domain.local/ap/v1/search/?offset=2&limit=2"
            }
            """;
        const string page2 = """
            {
              "type": "OrderedCollectionPage",
              "id": "https://a.domain.local/ap/v1/search/?offset=2&limit=2",
              "items": [
                {"type": "Person", "id": "https://a.domain.local/ap/v1/u/bob", "preferredUsername": "bob"},
                {"type": "Person", "id": "https://a.domain.local/ap/v1/u/carol", "preferredUsername": "carol"}
              ],
              "totalItems": 3,
              "partOf": "https://a.domain.local/ap/v1/search",
              "startIndex": 2,
              "prev": "https://a.domain.local/ap/v1/search"
            }
            """;

        var requestedQueries = new List<string>();
        var fake = new FakeHttpHandler(request =>
        {
            requestedQueries.Add(request.RequestUri!.Query);
            var query = request.RequestUri!.Query;
            return query.Contains("offset=2")
                ? Json(page2)
                : Json(page1);
        });
        var client = new ActivityPubClient(new HttpClient(fake));

        var pages = new List<Iris.Core.Collections.CollectionPage>();
        await foreach (var page in client.SearchPagedAsync(Base, string.Empty, new SearchOptions { Type = "Actor" }))
        {
            pages.Add(page);
        }

        // Two hops: offset 0 (no offset param) then exactly the page-1 next IRI (offset=2).
        Assert.Equal(2, fake.RequestCount);
        Assert.Equal(2, pages.Count);

        Assert.Single(pages[0].Items);
        Assert.Equal("https://a.domain.local/ap/v1/u/alice", ((IObject)pages[0].Items[0]).Id);
        Assert.Equal("https://a.domain.local/ap/v1/search/?offset=2&limit=2", pages[0].NextPage?.Value);
        Assert.Equal(3, pages[0].TotalItems);
        Assert.False(pages[0].IsLastPage);

        Assert.Equal(2, pages[1].Items.Count);
        Assert.Equal("https://a.domain.local/ap/v1/u/bob", ((IObject)pages[1].Items[0]).Id);
        Assert.Equal("https://a.domain.local/ap/v1/u/carol", ((IObject)pages[1].Items[1]).Id);
        Assert.Null(pages[1].NextPage);
        Assert.True(pages[1].IsLastPage);
        Assert.Equal(3, pages[1].TotalItems);

        // The first hop carries the type filter and starts at offset 0. (The second hop is the
        // server-built next IRI from page 1, which intentionally carries no type segment.)
        Assert.Contains("type=Actor", requestedQueries[0]);
        Assert.Contains("offset=0", requestedQueries[0]);
    }

    [Fact]
    public async Task SearchPagedAsync_SinglePage_YieldsOnePageWithoutNext()
    {
        const string page1 = """
            {
              "type": "OrderedCollection",
              "id": "https://a.domain.local/ap/v1/search",
              "items": [ {"type": "Person", "id": "https://a.domain.local/ap/v1/u/alice", "preferredUsername": "alice"} ],
              "totalItems": 1,
              "first": "https://a.domain.local/ap/v1/search"
            }
            """;

        var fake = new FakeHttpHandler(Json(page1));
        var client = new ActivityPubClient(new HttpClient(fake));

        var pages = new List<Iris.Core.Collections.CollectionPage>();
        await foreach (var page in client.SearchPagedAsync(Base, string.Empty))
        {
            pages.Add(page);
        }

        Assert.Equal(1, fake.RequestCount);
        Assert.Single(pages);
        Assert.True(pages[0].IsLastPage);
        Assert.Equal(1, pages[0].TotalItems);
    }

    [Fact]
    public async Task SearchPagedAsync_WithLocalOnly_AppendsLocalSegment()
    {
        const string page1 = """
            {
              "type": "OrderedCollection",
              "id": "https://a.domain.local/ap/v1/search",
              "items": [],
              "totalItems": 0,
              "first": "https://a.domain.local/ap/v1/search"
            }
            """;

        var fake = new FakeHttpHandler(Json(page1));
        var client = new ActivityPubClient(new HttpClient(fake));

        await foreach (var _ in client.SearchPagedAsync(Base, string.Empty, new SearchOptions { LocalOnly = true }))
        {
        }

        Assert.Equal("/ap/v1/search?q=&limit=100&offset=0&local=true", fake.LastUri!.AbsolutePath + fake.LastUri.Query);
    }

    [Fact]
    public async Task SearchPagedAsync_UnreachableEndpoint_YieldsNothing()
    {
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new ActivityPubClient(new HttpClient(fake));

        var count = 0;
        await foreach (var _ in client.SearchPagedAsync(Base, string.Empty))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
}
