using System.Collections.Concurrent;

namespace Iris.Core.Caching;

/// <summary>
/// A generic, async, read-through cache over an <see cref="ICache{TValue}"/>. Adds the concerns
/// the core cache does not express: a bypass/force-refresh escape hatch and stale-while-revalidate
/// (serve a stale entry immediately and refresh it in the foreground before returning).
/// </summary>
/// <typeparam name="TValue">The type of the cached value. Must be a reference type so that
/// <see langword="null"/> can mean "absent" (which is never cached).</typeparam>
/// <remarks>
/// <see cref="GetAsync"/> never caches an "absent" factory result (a 404 / not-found, i.e. a
/// <see langword="null"/> reference) so a later lookup retries. A stale hit is served immediately and the
/// entry is refreshed with the factory before returning; if the refresh is absent the stale value is
/// kept. The <see cref="CachePolicy"/> (TTL / stale window) comes from the underlying cache.
/// This is the single shared engine for both the client and server typed cache façades
/// (previously duplicated as <c>CachingClientCache</c> and <c>CachingServerCache</c>).
/// </remarks>
public sealed class CachingReadThrough<TValue>
    where TValue : class
{
    private readonly ICache<TValue> _cache;
    private readonly ICacheMetrics _metrics;

    // Negative cache: keys whose factory most recently returned null (absent — e.g. a remote actor
    // document that 404s), mapped to the UTC time the absence was observed. While the entry is within
    // <see cref="_negativeTtl"/>, a lookup returns null WITHOUT invoking the factory. This bounds the
    // cost of an unresolvable key to ONE fetch per TTL window instead of one per lookup, which is what
    // turned a peer delivering from an unresolvable actor into an unbounded outbound-fetch storm
    // (each rejection -> peer retry -> another fetch -> another rejection; the HttpClient connection
    // pool + thread pool saturated at ~300% CPU). A key that later becomes resolvable still resolves:
    // the negative entry expires after <see cref="_negativeTtl"/> and <see cref="Invalidate"/> clears it.
    private readonly ConcurrentDictionary<Iri, DateTime> _negative = new();
    private readonly TimeSpan _negativeTtl;

    /// <summary>
    /// Initializes a new <see cref="CachingReadThrough{TValue}"/>.
    /// </summary>
    /// <param name="cache">The underlying store (its <see cref="ICache{TValue}.Policy"/> governs TTL / staleness).</param>
    /// <param name="metrics">Optional hit/miss counters. Defaults to <see cref="NullCacheMetrics"/> (no-op).</param>
    /// <param name="negativeTtl">How long an "absent" (null factory) result is remembered before the
    /// factory is retried. Defaults to 60s. Set to <see cref="TimeSpan.Zero"/> to disable negative
    /// caching (the prior always-retry behavior).</param>
    public CachingReadThrough(ICache<TValue> cache, ICacheMetrics? metrics = null, TimeSpan? negativeTtl = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _metrics = metrics ?? NullCacheMetrics.Instance;
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// The hit/miss counters for this cache (for observability).
    /// </summary>
    public ICacheMetrics Metrics => _metrics;

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
    public bool Invalidate(Iri key)
    {
        _negative.TryRemove(key, out _);
        return _cache.Invalidate(key);
    }

    /// <summary>
    /// Removes all entries from the underlying cache (test isolation / teardown).
    /// Only effective when the underlying cache is a <see cref="MemoryCache{TValue}"/>.
    /// </summary>
    public void Clear()
    {
        if (_cache is MemoryCache<TValue> memoryCache)
        {
            memoryCache.Clear();
        }

        _negative.Clear();
    }

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
    /// <returns>The value (or null when absent), whether the served value was a stale-while-revalidate
    /// hit, and whether the value came from the cache at all (a hit) rather than the factory (a miss).</returns>
    public async Task<(TValue? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<TValue?>> factory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!bypassCache)
        {
            var nowUtc = DateTime.UtcNow;

            // A recently-observed absence short-circuits to null without invoking the factory, so an
            // unresolvable key costs one fetch per _negativeTtl window rather than one per lookup.
            if (_negativeTtl > TimeSpan.Zero
                && _negative.TryGetValue(key, out var absentAt)
                && nowUtc - absentAt < _negativeTtl)
            {
                _metrics.RecordHit();
                return (null, false, true);
            }

            if (_cache.TryGetEntry(key, nowUtc) is { } existing)
            {
                var value = existing.Entry.Value;
                if (existing.State == CacheState.Fresh)
                {
                    _metrics.RecordHit();
                    return (value, false, true);
                }

                // Stale: serve immediately, then refresh (stale-while-revalidate).
                _metrics.RecordStaleHit();
                var refreshed = await factory(key).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    _cache.Put(key, refreshed, DateTime.UtcNow);
                    _negative.TryRemove(key, out _);
                }
                else
                {
                    RecordAbsent(key, DateTime.UtcNow);
                }

                return (value, true, true);
            }
        }

        _metrics.RecordMiss();
        var fetched = await factory(key).ConfigureAwait(false);
        if (fetched is not null)
        {
            _cache.Put(key, fetched, DateTime.UtcNow);
            _negative.TryRemove(key, out _);
        }
        else
        {
            RecordAbsent(key, DateTime.UtcNow);
        }

        return (fetched, false, false);
    }

    private void RecordAbsent(Iri key, DateTime nowUtc)
    {
        if (_negativeTtl > TimeSpan.Zero)
        {
            _negative[key] = nowUtc;
        }
    }
}
