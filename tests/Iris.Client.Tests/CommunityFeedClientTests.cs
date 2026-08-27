using System.Net;
using System.Text;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="ActivityPubClient.GetCommunityFeedAsync"/>: it derives the community
/// feed IRI (<c>{community}/feed</c> via <see cref="IriExtensions.FeedOf(Iri)"/>) and enumerates the
/// feed exactly like any other paged collection (the same enumeration + collection-page caching as a
/// personal feed). A URL-routing <see cref="FakeHttpHandler"/> serves a multi-page feed.
/// </summary>
public class CommunityFeedClientTests
{
    private const string CommunityIri = "https://a.domain.local/ap/v1/c/iris";
    private const string FeedIri = "https://a.domain.local/ap/v1/c/iris/feed";
    private const string FirstIri = FeedIri + "/first";
    private const string Page2Iri = FeedIri + "/2";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
    };

    // The feed collection: an OrderedCollection whose `first` points at the first page.
    private static string FeedCollectionDoc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{FeedIri}}",
          "type": "OrderedCollection",
          "totalItems": 3,
          "first": "{{FirstIri}}"
        }
        """;

    // The first page: an OrderedCollectionPage with items + a next to page 2.
    private static string FirstPageDoc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{FirstIri}}",
          "type": "OrderedCollectionPage",
          "partOf": "{{FeedIri}}",
          "totalItems": 3,
          "startIndex": 1,
          "items": [
            { "id": "https://a.domain.local/n/alice-3", "type": "Create" },
            { "id": "https://a.domain.local/n/alice-2", "type": "Create" }
          ],
          "next": "{{Page2Iri}}"
        }
        """;

    private static string Page2Doc() => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{Page2Iri}}",
          "type": "OrderedCollectionPage",
          "partOf": "{{FeedIri}}",
          "totalItems": 3,
          "startIndex": 3,
          "items": [
            { "id": "https://a.domain.local/n/bob-1", "type": "Create" }
          ],
          "prev": "{{FirstIri}}"
        }
        """;

    private static FakeHttpHandler RoutingHandler()
    {
        return new FakeHttpHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("/feed/2"))
            {
                return Json(Page2Doc());
            }

            if (uri.EndsWith("/feed/first"))
            {
                return Json(FirstPageDoc());
            }

            if (uri.EndsWith("/c/iris/feed"))
            {
                return Json(FeedCollectionDoc());
            }

            // Anything else (notably the community document itself, or a wrong path) 404s — this
            // proves the client targets the /feed collection, not the community document.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static ActivityPubClient Client(FakeHttpHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task GetCommunityFeedAsync_DerivesFeedIri_AndYieldsItems()
    {
        var handler = RoutingHandler();
        var client = Client(handler);

        var items = new List<string>();
        await foreach (var item in client.GetCommunityFeedAsync(new Iri(CommunityIri)))
        {
            items.Add(item is IObject { Id: { } id } ? id : throw new InvalidOperationException("expected an item with an id"));
        }

        // The client fetched the /feed collection (not the community document) and yielded all 3 items
        // across the two pages, in order.
        Assert.Equal(
            ["https://a.domain.local/n/alice-3", "https://a.domain.local/n/alice-2", "https://a.domain.local/n/bob-1"],
            items);
    }

    [Fact]
    public async Task GetCommunityFeedAsync_UnknownCommunity_YieldsNothing()
    {
        var client = Client(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var count = 0;
        await foreach (var _ in client.GetCommunityFeedAsync(new Iri("https://a.domain.local/ap/v1/c/nobody")))
        {
            count++;
        }

        Assert.Equal(0, count);
    }
}
