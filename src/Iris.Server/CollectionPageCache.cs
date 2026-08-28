using Iris.Client;
using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Caches remote collection pages (fetched from other servers during federation) by the page's
/// <c>partOf</c> + cursor IRI, using the default <see cref="CachePolicy.CollectionPage"/> policy
/// (30 seconds fresh, 30 seconds stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="CollectionPage"/> (the items of one page plus the link to the next
/// page). Collection pages are the most volatile data, hence the short TTL. A missing page (null
/// factory result) is not cached. This cache is populated by the server's outbound federation paths
/// in later phases.
/// </remarks>
public sealed class CollectionPageCache
{
    private readonly CachingReadThrough<CollectionPage> _cache;

    /// <summary>
    /// Initializes a new <see cref="CollectionPageCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.CollectionPage"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public CollectionPageCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.CollectionPage;
        _cache = new CachingReadThrough<CollectionPage>(new MemoryCache<CollectionPage>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// The number of entries currently held (for observability/testing).
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Removes the entry for <paramref name="key"/> (e.g. after posting to the collection).
    /// </summary>
    /// <param name="key">The page IRI.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Gets the cached collection page for <paramref name="key"/>, fetching with <paramref name="factory"/>
    /// on a miss (or when <paramref name="forceRefresh"/> is set).
    /// </summary>
    /// <param name="key">The page IRI.</param>
    /// <param name="forceRefresh">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when refreshing) to fetch the page; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page (or null when absent), whether it was a stale-while-revalidate hit, and whether it was a hit at all.</returns>
    public Task<(CollectionPage? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool forceRefresh,
        Func<Iri, Task<CollectionPage?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, forceRefresh, factory, ct);
}
