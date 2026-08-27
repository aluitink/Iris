using Iris.Core;

namespace Iris.Server;

/// <summary>
/// A server-side, async, read-through cache over an <see cref="ICache{TValue}"/>. It mirrors the
/// client's read-through cache but is the server's building block: a <c>forceRefresh</c> escape
/// hatch (the <c>?refresh=true</c> query param) and stale-while-revalidate (serve a stale entry
/// immediately and refresh it before returning).
/// </summary>
/// <typeparam name="TValue">The type of the cached value. Reference types may use <see langword="null"/>
/// to mean "absent" (which is never cached); value types are always considered present.</typeparam>
/// <remarks>
/// <see cref="GetAsync"/> never caches an "absent" factory result (a 404 / not-found, i.e. a
/// <see langword="null"/> reference) so a later lookup retries. A stale hit is served immediately and the
/// entry is refreshed with the factory before returning; if the refresh is absent the stale value is
/// kept. The <see cref="CachePolicy"/> (TTL / stale window) comes from the underlying cache.
/// </remarks>
public sealed class CachingServerCache<TValue>
    where TValue : class
{
    private readonly ICache<TValue> _cache;

    /// <summary>
    /// Initializes a new <see cref="CachingServerCache{TValue}"/>.
    /// </summary>
    /// <param name="cache">The underlying store (its <see cref="ICache{TValue}.Policy"/> governs TTL / staleness).</param>
    public CachingServerCache(ICache<TValue> cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// The number of entries currently held by the underlying cache (for observability/testing).
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Removes the entry for <paramref name="key"/> from the underlying cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Reads <paramref name="key"/> from the cache, fetching with <paramref name="factory"/> on a miss
    /// (or when <paramref name="forceRefresh"/> is set) and storing the result when non-null.
    /// </summary>
    /// <param name="key">The cache key (the object/actor/page IRI).</param>
    /// <param name="forceRefresh">When true, the cache is skipped for the read (the factory is always
    /// consulted) but a non-null result is still written back. This is the server's <c>?refresh=true</c> bypass.</param>
    /// <param name="factory">Invoked on a miss (or always, when <paramref name="forceRefresh"/> is set) to
    /// fetch the value. May return <see langword="null"/> to indicate the value is absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The value (or null when absent), whether the served value was a stale-while-revalidate
    /// hit, and whether the value came from the cache at all (a hit) rather than the factory (a miss).</returns>
    public async Task<(TValue? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool forceRefresh,
        Func<Iri, Task<TValue?>> factory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!forceRefresh)
        {
            var nowUtc = DateTime.UtcNow;
            if (_cache.TryGetEntry(key, nowUtc) is { } existing)
            {
                var value = existing.Entry.Value;
                if (existing.State == CacheState.Fresh)
                {
                    return (value, false, true);
                }

                // Stale: serve immediately, then refresh (stale-while-revalidate).
                var refreshed = await factory(key).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    _cache.Put(key, refreshed, DateTime.UtcNow);
                }

                return (value, true, true);
            }
        }

        var fetched = await factory(key).ConfigureAwait(false);
        if (fetched is not null)
        {
            _cache.Put(key, fetched, DateTime.UtcNow);
        }

        return (fetched, false, false);
    }
}
