using Iris.Core;

namespace Iris.Client;

/// <summary>
/// A client-side, async, read-through cache over an <see cref="ICache{TValue}"/>. It adds the
/// client concerns the core cache does not express: a <c>bypassCache</c> escape hatch and
/// stale-while-revalidate (serve a stale entry immediately and refresh it in the foreground).
/// </summary>
/// <typeparam name="TValue">The type of the cached value. Reference types may use <see langword="null"/>
/// to mean "absent" (which is never cached); value types are always considered present.</typeparam>
/// <remarks>
/// <see cref="GetAsync"/> never caches an "absent" factory result (a 404 / not-found, i.e. a
/// <see langword="null"/> reference) so a later lookup retries. A stale hit is served immediately and the
/// entry is refreshed with the factory before returning; if the refresh is absent the stale value is
/// kept. The <see cref="CachePolicy"/> (TTL / stale window) comes from the underlying cache.
/// </remarks>
public sealed class CachingClientCache<TValue>
    where TValue : class
{
    private readonly ICache<TValue> _cache;

    /// <summary>
    /// Initializes a new <see cref="CachingClientCache{TValue}"/>.
    /// </summary>
    /// <param name="cache">The underlying store (its <see cref="ICache{TValue}.Policy"/> governs TTL / staleness).</param>
    public CachingClientCache(ICache<TValue> cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// Reads <paramref name="key"/> from the cache, fetching with <paramref name="factory"/> on a miss
    /// (or when <paramref name="bypassCache"/> is set) and storing the result when non-null.
    /// </summary>
    /// <param name="key">The cache key (the object/actor/page IRI).</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read (the factory is always
    /// consulted) but a non-null result is still written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when <paramref name="bypassCache"/> is set) to
    /// fetch the value. May return <see langword="null"/> to indicate the value is absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The value (or null when absent) and whether the served value was a stale-while-revalidate hit.</returns>
    public async Task<(TValue? Value, bool WasStale)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<TValue?>> factory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!bypassCache)
        {
            var nowUtc = DateTime.UtcNow;
            if (_cache.TryGetEntry(key, nowUtc) is { } existing)
            {
                var value = existing.Entry.Value;
                if (existing.State == CacheState.Fresh)
                {
                    return (value, false);
                }

                // Stale: serve immediately, then refresh (stale-while-revalidate).
                var refreshed = await factory(key).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    _cache.Put(key, refreshed, DateTime.UtcNow);
                }

                return (value, true);
            }
        }

        var fetched = await factory(key).ConfigureAwait(false);
        if (fetched is not null)
        {
            _cache.Put(key, fetched, DateTime.UtcNow);
        }

        return (fetched, false);
    }
}
