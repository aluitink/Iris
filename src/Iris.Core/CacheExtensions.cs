namespace Iris.Core;

/// <summary>
/// Extension methods for <see cref="ICache{TValue}"/> that express the common
/// get-or-refresh pattern used by the client and server caching layers.
/// </summary>
public static class CacheExtensions
{
    /// <summary>
    /// Reads <paramref name="key"/> from the cache and returns the result with its freshness
    /// state, using the cache's own clock for "now".
    /// </summary>
    /// <typeparam name="TValue">The type of the cached value.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <returns>A <see cref="CachedValue{TValue}"/> describing the hit/miss and state.</returns>
    public static CachedValue<TValue> Lookup<TValue>(this ICache<TValue> cache, Iri key)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var nowUtc = DateTime.UtcNow;
        return cache.TryGetEntry(key, nowUtc) is { } result
            ? new CachedValue<TValue>(Hit: true, Value: result.Entry.Value, State: result.State)
            : CachedValue<TValue>.Miss;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from the cache as of <paramref name="nowUtc"/> and returns
    /// the result with its freshness state. The clock is injected for deterministic testing.
    /// </summary>
    /// <typeparam name="TValue">The type of the cached value.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="nowUtc">The reference "now".</param>
    /// <returns>A <see cref="CachedValue{TValue}"/> describing the hit/miss and state.</returns>
    public static CachedValue<TValue> Lookup<TValue>(this ICache<TValue> cache, Iri key, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.TryGetEntry(key, nowUtc) is { } result
            ? new CachedValue<TValue>(Hit: true, Value: result.Entry.Value, State: result.State)
            : CachedValue<TValue>.Miss;
    }

    /// <summary>
    /// Gets <paramref name="key"/> from the cache, or on a miss invokes <paramref name="factory"/>
    /// to produce the value and stores it. Returns the value and whether it came from the cache.
    /// </summary>
    /// <typeparam name="TValue">The type of the cached value.</typeparam>
    /// <param name="cache">The cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Invoked only on a miss to produce (and store) the value.</param>
    /// <param name="nowUtc">The reference "now" used for the new entry's timestamp.</param>
    /// <returns>The value and whether it was a cache hit.</returns>
    public static (TValue Value, bool WasHit) GetOrAdd<TValue>(
        this ICache<TValue> cache,
        Iri key,
        Func<TValue> factory,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);

        var existing = cache.TryGetEntry(key, nowUtc);
        if (existing is { } hit)
        {
            return (hit.Entry.Value, true);
        }

        var value = factory();
        cache.Put(key, value, nowUtc);
        return (value, false);
    }
}
