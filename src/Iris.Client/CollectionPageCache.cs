using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client;

/// <summary>
/// Caches fetched collection pages by their page IRI, using the default
/// <see cref="CachePolicy.CollectionPage"/> (30 seconds fresh, 30 seconds stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="IObject"/> (the page document; callers cast to
/// <see cref="OrderedCollectionPage"/>). Pages are short-lived, so the TTL is the tightest of the
/// client caches. A not-found page (null factory result) is not cached.
/// </remarks>
public sealed class CollectionPageCache
{
    private readonly CachingReadThrough<IObject> _cache;

    /// <summary>
    /// Initializes a new <see cref="CollectionPageCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.CollectionPage"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public CollectionPageCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.CollectionPage;
        _cache = new CachingReadThrough<IObject>(new MemoryCache<IObject>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// Gets the cached page for <paramref name="key"/>, fetching with <paramref name="factory"/> on a
    /// miss (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The page IRI.</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when bypassing) to fetch the page; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page (or null when absent) and whether it was a stale-while-revalidate hit.</returns>
    public async Task<(IObject? Value, bool WasStale)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<IObject?>> factory,
        CancellationToken ct = default)
    {
        var (value, wasStale, _) = await _cache.GetAsync(key, bypassCache, factory, ct).ConfigureAwait(false);
        return (value, wasStale);
    }
}
