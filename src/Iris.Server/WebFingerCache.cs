using Iris.Client;
using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Caches WebFinger resolutions (account IRI → actor IRI) by the account IRI, using the default
/// <see cref="CachePolicy.WebFinger"/> policy (15 minutes fresh, 15 minutes stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="WebFingerHit"/> (the account IRI + the resolved actor IRI). WebFinger
/// results are stable, so the TTL is longer than collection pages but shorter than keys. A failed
/// resolution (null factory result) is not cached, so it is retried on the next lookup. This cache is
/// populated by the server's outbound federation paths in later phases.
/// </remarks>
public sealed class WebFingerCache
{
    private readonly CachingServerCache<WebFingerHit> _cache;

    /// <summary>
    /// Initializes a new <see cref="WebFingerCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.WebFinger"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public WebFingerCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.WebFinger;
        _cache = new CachingServerCache<WebFingerHit>(new MemoryCache<WebFingerHit>(resolved, capacity));
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
    /// Removes the entry for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The account IRI.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Gets the cached WebFinger resolution for <paramref name="key"/>, fetching with <paramref name="factory"/>
    /// on a miss (or when <paramref name="forceRefresh"/> is set).
    /// </summary>
    /// <param name="key">The account IRI.</param>
    /// <param name="forceRefresh">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when refreshing) to resolve the account; null means not found.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The hit (or null when not found), whether it was a stale-while-revalidate hit, and whether it was a hit at all.</returns>
    public Task<(WebFingerHit? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool forceRefresh,
        Func<Iri, Task<WebFingerHit?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, forceRefresh, factory, ct);
}
