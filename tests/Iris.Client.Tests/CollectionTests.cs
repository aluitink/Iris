using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for rich paged collections: <see cref="ActivityPubClient.GetCollectionAsync"/>,
/// <see cref="ActivityPubClient.GetCollectionItemsAsync"/>, and the <see cref="CollectionPage"/>
/// wrapper. Uses a URL-routing <see cref="FakeHttpHandler"/> to serve a multi-page
/// <c>OrderedCollection</c> (collection → first → page1 → page2 → last).
/// </summary>
public class CollectionTests
{
    private const string CollectionIri = "https://a.domain.local/c/outbox";
    private const string FirstIri = "https://a.domain.local/c/outbox/first";
    private const string Page1Iri = "https://a.domain.local/c/outbox/1";
    private const string Page2Iri = "https://a.domain.local/c/outbox/2";

    private static HttpResponseMessage Json(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
        };
        return response;
    }

    private static string CollectionDoc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{CollectionIri}}",
          "type": "OrderedCollection",
          "totalItems": 3,
          "first": "{{FirstIri}}"
        }
        """;

    private static string FirstPageDoc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{FirstIri}}",
          "type": "OrderedCollectionPage",
          "partOf": "{{CollectionIri}}",
          "totalItems": 3,
          "startIndex": 1,
          "items": [
            { "id": "https://a.domain.local/n/1", "type": "Note", "content": "one" },
            { "id": "https://a.domain.local/n/2", "type": "Note", "content": "two" }
          ],
          "next": "{{Page2Iri}}"
        }
        """;

    private static string Page2Doc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{Page2Iri}}",
          "type": "OrderedCollectionPage",
          "partOf": "{{CollectionIri}}",
          "totalItems": 3,
          "startIndex": 3,
          "items": [
            { "id": "https://a.domain.local/n/3", "type": "Note", "content": "three" }
          ],
          "prev": "{{FirstIri}}"
        }
        """;

    private static FakeHttpHandler RoutingHandler()
    {
        return new FakeHttpHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("/c/outbox/1"))
            {
                return Json(FirstPageDoc());
            }

            if (uri.EndsWith("/c/outbox/2"))
            {
                return Json(Page2Doc());
            }

            if (uri.EndsWith("/c/outbox/first"))
            {
                return Json(FirstPageDoc());
            }

            if (uri.EndsWith("/c/outbox"))
            {
                return Json(CollectionDoc());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static ActivityPubClient Client(FakeHttpHandler handler)
        => new(new HttpClient(handler));

    // --- GetCollectionAsync ----------------------------------------------------

    [Fact]
    public async Task GetCollectionAsync_FollowsFirstAndNext_YieldsAllPages()
    {
        var client = Client(RoutingHandler());
        var pages = new List<CollectionPage>();
        await foreach (var page in client.GetCollectionAsync(new Iri(CollectionIri)))
        {
            pages.Add(page);
        }

        // Two pages yielded (first page + page2).
        Assert.True(pages.Count == 2, $"expected 2 pages, got {pages.Count}");

        var first = pages[0];
        Assert.True(first.Items.Count == 2, $"expected 2 items, got {first.Items.Count}");
        Assert.Equal(3, first.TotalItems);
        Assert.Equal(new Iri(Page2Iri), first.NextPage);
        Assert.False(first.IsLastPage);

        var second = pages[1];
        Assert.True(second.Items.Count == 1, $"expected 1 item, got {second.Items.Count}");
        Assert.Equal(new Iri(FirstIri), second.PrevPage);
        Assert.Null(second.NextPage);
        Assert.True(second.IsLastPage);
    }

    [Fact]
    public async Task GetCollectionAsync_ItemsAreDeserializedObjects()
    {
        var client = Client(RoutingHandler());
        CollectionPage? first = null;
        await foreach (var page in client.GetCollectionAsync(new Iri(CollectionIri)))
        {
            first ??= page;
        }

        Assert.NotNull(first);
        var note = Assert.IsType<KristofferStrube.ActivityStreams.Note>(first!.Items[0]);
        Assert.Equal("https://a.domain.local/n/1", note.Id);
    }

    [Fact]
    public async Task GetCollectionAsync_WithLimit_StopsAtLimit()
    {
        var client = Client(RoutingHandler());
        int items = 0;
        await foreach (var page in client.GetCollectionAsync(new Iri(CollectionIri), new CollectionQuery(Limit: 2)))
        {
            items += page.Items.Count;
        }

        // Limit 2 → only the first page's 2 items; the second page is never fetched/yielded.
        Assert.Equal(2, items);
    }

    [Fact]
    public async Task GetCollectionAsync_404_YieldsNothing()
    {
        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);
        int count = 0;
        await foreach (var _ in client.GetCollectionAsync(new Iri("https://a.domain.local/missing")))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    // --- GetCollectionItemsAsync -----------------------------------------------

    [Fact]
    public async Task GetCollectionItemsAsync_FlattensAllItemsAcrossPages()
    {
        var client = Client(RoutingHandler());
        var itemIds = new List<string>();
        await foreach (var item in client.GetCollectionItemsAsync(new Iri(CollectionIri)))
        {
            itemIds.Add(item is KristofferStrube.ActivityStreams.Object o ? o.Id! : "?");
        }

        Assert.Equal(
            ["https://a.domain.local/n/1", "https://a.domain.local/n/2", "https://a.domain.local/n/3"],
            itemIds);
    }

    [Fact]
    public async Task GetCollectionItemsAsync_WithLimit_StopsAtLimit()
    {
        var client = Client(RoutingHandler());
        var itemIds = new List<string>();
        await foreach (var item in client.GetCollectionItemsAsync(new Iri(CollectionIri), new CollectionQuery(Limit: 1)))
        {
            itemIds.Add(item is KristofferStrube.ActivityStreams.Object o ? o.Id! : "?");
        }

        Assert.Equal(["https://a.domain.local/n/1"], itemIds);
    }

    // --- CollectionPage wrapper -----------------------------------------------

    [Fact]
    public void CollectionPage_IsLastPage_TrueWhenNoNext()
    {
        var page = new CollectionPage
        {
            Page = new KristofferStrube.ActivityStreams.OrderedCollectionPage(),
            Items = [],
        };
        Assert.True(page.IsLastPage);
    }

    [Fact]
    public void CollectionPage_IsLastPage_FalseWhenNextPresent()
    {
        var page = new CollectionPage
        {
            Page = new KristofferStrube.ActivityStreams.OrderedCollectionPage(),
            Items = [],
            NextPage = new Iri("https://a.domain.local/next"),
        };
        Assert.False(page.IsLastPage);
    }
}
