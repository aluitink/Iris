using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Caching;

/// <summary>
/// The default <see cref="IRemoteCollectionFetcher"/>, backed by an <see cref="IActivityPubClient"/>.
/// </summary>
/// <remarks>
/// The client is the outbound federation transport (Phase 2); the request is signed with the client's
/// configured identity (<see cref="ActivityPubServerOptions.InstanceActorId"/>). Reads go through the
/// Phase 3 <see cref="CollectionPageCache"/> (keyed by the page IRI, the 30-second
/// <see cref="CachePolicy.CollectionPage"/> policy), so a page is fetched once and reused across
/// outbound paths within the TTL. A <c>bypassCache</c> argument (mirroring the local collection
/// endpoints' <c>?refresh=true</c>) bypasses the read for that call but writes a non-null page back.
/// An absent result (the fetch failed or the object is not an <see cref="OrderedCollectionPage"/>) is
/// not cached, so a later lookup retries.
/// <para>
/// The page is built by fetching the page document and flattening it into a
/// <see cref="Iris.Core.Collections.CollectionPage"/> (items + the next-page link), the same shape the
/// client's <see cref="IActivityPubClient.GetCollectionAsync"/> yields per page — so callers can
/// follow the collection themselves via <see cref="Iris.Core.Collections.CollectionPage.NextPage"/>.
/// </para>
/// </remarks>
public sealed class IrisRemoteCollectionFetcher(IActivityPubClient client, CollectionPageCache collectionPages)
    : IRemoteCollectionFetcher
{
    private readonly IActivityPubClient _client = client!;
    private readonly CollectionPageCache _collectionPages = collectionPages!;

    /// <inheritdoc/>
    public async Task<CollectionPage?> GetCollectionPageAsync(Iri pageIri, bool bypassCache = false, CancellationToken ct = default)
    {
        var (page, _, _) = await _collectionPages
            .GetAsync(
                pageIri,
                bypassCache,
                factory: iri => FetchPageAsync(iri, ct),
                ct)
            .ConfigureAwait(false);

        return page;
    }

    /// <summary>
    /// Fetches a page document from the network (bypassing the cache) and flattens it into a
    /// <see cref="Iris.Core.Collections.CollectionPage"/>.
    /// </summary>
    /// <param name="pageIri">The absolute IRI of the page (the collection IRI for the first page, or a
    /// page IRI for a later page).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page, or null when the fetch fails or the fetched object is not an
    /// <see cref="OrderedCollectionPage"/>.</returns>
    private async Task<CollectionPage?> FetchPageAsync(Iri pageIri, CancellationToken ct)
    {
        // Fetch the page document directly (signed GET). When pageIri is a plain collection IRI the
        // remote returns the OrderedCollection (whose `first` is the first page) — not a page — so the
        // result is null and the caller follows `first`. When pageIri is a page IRI the remote returns
        // the OrderedCollectionPage and it is flattened here.
        using var request = new HttpRequestMessage(HttpMethod.Get, pageIri.Value);
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // Rule 1: deserialize into the range interface, then cast.
        var objectOrLink = ActivityJson.Deserialize<IObjectOrLink>(json);
        return CollectionPageFactory.FromOrderedCollectionPage(objectOrLink as IObject);
    }
}
