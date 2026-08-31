using System.Net;
using System.Text;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Tests;

/// <summary>
/// Verifies the client's global-search endpoint is derived from the instance base via the canonical
/// <see cref="IriExtensions.SearchOf"/> helper (the single source of truth for where global search
/// lives) rather than an ad-hoc string, and that <see cref="ActivityPubClient.SearchAsync"/> requests
/// exactly that IRI.
/// </summary>
public class SearchEndpointDerivationTests
{
    private const string InstanceBase = "https://a.domain.local/ap/v1";

    [Fact]
    public void SearchAsync_RequestsTheSearchOfDerivedEndpoint()
    {
        var pageJson = """
            {"type":"OrderedCollection","id":"https://a.domain.local/ap/v1/search","items":[]}
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(pageJson, Encoding.UTF8, ActivityJson.ActivityJsonContentType),
        };
        var fake = new FakeHttpHandler(response);
        var client = new ActivityPubClient(new HttpClient(fake));

        var baseIri = new Iri(InstanceBase);

        _ = EnumerateAsync(client.SearchAsync(baseIri, "alice", new SearchOptions { Limit = 50, Offset = 20 }));

        // The requested path is the canonical SearchOf derivation; the query carries q/limit/offset.
        Assert.Equal(InstanceBase + "/search", baseIri.SearchOf().Value);
        Assert.Equal("/ap/v1/search?q=alice&limit=50&offset=20", fake.LastUri!.AbsolutePath + fake.LastUri.Query);
    }

    [Fact]
    public void SearchOf_DerivesTheInstanceSearchPath()
    {
        Assert.Equal("https://a.domain.local/ap/v1/search", new Iri(InstanceBase).SearchOf().Value);
    }

    private static async Task EnumerateAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var _ in source)
        {
            // drain
        }
    }
}
