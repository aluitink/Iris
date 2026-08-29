using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Server.Caching;

/// <summary>
/// Fetches a remote <see cref="OrderedCollectionPage"/> by its page IRI. A seam so the server's outbound
/// federation paths can retrieve a single page of a remote actor's collection (e.g. a remote actor's
/// <c>outbox</c> or <c>followers</c>) without depending on a concrete HTTP client.
/// </summary>
/// <remarks>
/// The default implementation (<see cref="IrisRemoteCollectionFetcher"/>) wraps the
/// <see cref="IActivityPubClient"/> and reads through the Phase 3 <see cref="CollectionPageCache"/>
/// (keyed by the page IRI, 30-second policy) so a page is fetched once and reused within the TTL.
/// Fetch failures (404, network error, not-a-page) are an expected condition — the default returns
/// null, it does not throw.
/// </remarks>
public interface IRemoteCollectionFetcher
{
    /// <summary>
    /// Fetches a single collection page from a remote instance.
    /// </summary>
    /// <param name="pageIri">The absolute IRI of the page (e.g. a remote outbox's first page
    /// <c>https://b.test/ap/v1/u/bob/outbox?min_id=…</c>, or a query-string page
    /// <c>…/outbox/?page=2</c>).</param>
    /// <param name="bypassCache">When true, the collection-page cache is skipped for the read (a
    /// non-null page is written back). Mirrors the local collection endpoints' <c>?refresh=true</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page (its items plus the next-page link), or null when the fetch fails or the
    /// fetched object is not an <see cref="OrderedCollectionPage"/>.</returns>
    /// <remarks>
    /// Fetch failures (404, network error, not-a-page) are an expected condition — return null,
    /// do not throw.
    /// </remarks>
    public Task<CollectionPage?> GetCollectionPageAsync(Iri pageIri, bool bypassCache = false, CancellationToken ct = default);
}
