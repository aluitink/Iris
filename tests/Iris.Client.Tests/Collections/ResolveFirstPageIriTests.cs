using System.Net;
using System.Text;
using Iris.Client.Collections;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests.Collections;

/// <summary>
/// Unit tests for <see cref="CollectionPageFactory.ResolveFirstPageIri"/> — the shared rule that
/// resolves a fetched collection document to the IRI of its FIRST page. S56: the anonymous
/// (signed-out) collection read in the client's <c>PagedCollection</c> component parsed a bare
/// <c>OrderedCollection</c> ENVELOPE (a <c>first</c> link and no items — the canonical Mastodon
/// outbox shape) as an empty page and rendered the empty state, while the signed-in
/// <c>GetCollectionAsync</c> followed the <c>first</c> link and rendered the actor's posts. The
/// shared rule (previously private to the client) is the single source of truth both paths apply.
/// </summary>
public class ResolveFirstPageIriTests
{
    private static string EnvelopeDoc(string collectionIri, string firstIri) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{collectionIri}}",
          "type": "OrderedCollection",
          "totalItems": 67190,
          "first": "{{firstIri}}",
          "last": "{{collectionIri}}/last"
        }
        """;

    private static string SelfPageCollectionDoc(string collectionIri) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{collectionIri}}",
          "type": "OrderedCollection",
          "totalItems": 2,
          "orderedItems": [
            { "id": "{{collectionIri}}/statuses/1", "type": "Note", "content": "one" },
            { "id": "{{collectionIri}}/statuses/2", "type": "Note", "content": "two" }
          ]
        }
        """;

    private static string PageDoc(string pageIri) => $$"""
        {
          "@context": "https://www.w3.org/ns/activitystreams",
          "id": "{{pageIri}}",
          "type": "OrderedCollectionPage",
          "partOf": "{{pageIri}}",
          "totalItems": 2,
          "items": [
            { "id": "{{pageIri}}/n/1", "type": "Note", "content": "one" }
          ]
        }
        """;

    private static IObject Deserialize(string json)
        => Iris.Core.ActivityJson.Deserialize<IObjectOrLink>(json) as IObject
            ?? throw new InvalidOperationException("expected an IObject document");

    // --- The S56 shape: a bare envelope (Mastodon outbox) ------------------------------------

    [Fact]
    public void ResolveFirstPageIri_EnvelopeWithFirstLink_YieldsFirstPageIri()
    {
        const string collectionIri = "https://mastodon.social/users/deadline/outbox";
        const string firstPageIri = "https://mastodon.social/users/deadline/outbox?page=true";
        var doc = Deserialize(EnvelopeDoc(collectionIri, firstPageIri));

        var first = CollectionPageFactory.ResolveFirstPageIri(doc, new Iri(collectionIri));

        Assert.Equal(new Iri(firstPageIri), first);
    }

    // --- The Lemmy shape: a collection carrying its items, no `first` ------------------------

    [Fact]
    public void ResolveFirstPageIri_CollectionWithItemsAndNoFirst_YieldsCollectionIri()
    {
        const string collectionIri = "https://lemmy.example/c/test/outbox";
        var doc = Deserialize(SelfPageCollectionDoc(collectionIri));

        var first = CollectionPageFactory.ResolveFirstPageIri(doc, new Iri(collectionIri));

        Assert.Equal(new Iri(collectionIri), first);
    }

    // --- A page is its own page ---------------------------------------------------------------

    [Fact]
    public void ResolveFirstPageIri_PageDocument_YieldsItsIri()
    {
        const string pageIri = "https://mastodon.social/users/deadline/outbox?page=true";
        var doc = Deserialize(PageDoc(pageIri));

        var first = CollectionPageFactory.ResolveFirstPageIri(doc, new Iri(pageIri));

        Assert.Equal(new Iri(pageIri), first);
    }

    // --- Non-collection documents have no first page ------------------------------------------

    [Fact]
    public void ResolveFirstPageIri_NonCollection_YieldsNull()
    {
        const string noteIri = "https://mastodon.social/users/deadline/statuses/1";
        var doc = Deserialize($$"""
            {
              "@context": "https://www.w3.org/ns/activitystreams",
              "id": "{{noteIri}}",
              "type": "Note",
              "content": "hello"
            }
            """);

        Assert.Null(CollectionPageFactory.ResolveFirstPageIri(doc, new Iri(noteIri)));
    }

    // --- End-to-end: the client's GetCollectionAsync follows the same rule --------------------

    [Fact]
    public async Task GetCollectionAsync_EnvelopeCollection_YieldsFirstPageItems()
    {
        const string collectionIri = "https://mastodon.social/users/deadline/outbox";
        const string firstPageIri = "https://mastodon.social/users/deadline/outbox?page=true";
        var handler = new FakeHttpHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.StartsWith(firstPageIri, StringComparison.OrdinalIgnoreCase))
            {
                return Json(PageDoc(firstPageIri));
            }

            if (uri.StartsWith(collectionIri, StringComparison.OrdinalIgnoreCase))
            {
                return Json(EnvelopeDoc(collectionIri, firstPageIri));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new ActivityPubClient(new HttpClient(handler));
        var items = new List<IObjectOrLink>();
        await foreach (var page in client.GetCollectionAsync(new Iri(collectionIri)))
        {
            items.AddRange(page.Items);
        }

        Assert.Single(items);
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/activity+json"),
        };
    }
}
