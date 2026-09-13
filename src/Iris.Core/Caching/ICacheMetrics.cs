namespace Iris.Core.Caching;

/// <summary>
/// Observability counters for a cache: how many reads were hits, misses, or stale-while-revalidate
/// refreshes.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe (counters are incremented concurrently). The default
/// <see cref="CacheMetrics"/> uses <see cref="Interlocked"/> operations. A <see cref="NullCacheMetrics"/>
/// no-op is provided for callers that do not need metrics.
/// </remarks>
public interface ICacheMetrics
{
    /// <summary>
    /// Increments the hit counter (a fresh entry was served from the cache).
    /// </summary>
    void RecordHit();

    /// <summary>
    /// Increments the miss counter (the cache had no entry; the factory was consulted).
    /// </summary>
    void RecordMiss();

    /// <summary>
    /// Increments the stale-hit counter (a stale entry was served and refreshed).
    /// </summary>
    void RecordStaleHit();

    /// <summary>
    /// The total number of fresh hits.
    /// </summary>
    long Hits { get; }

    /// <summary>
    /// The total number of misses.
    /// </summary>
    long Misses { get; }

    /// <summary>
    /// The total number of stale-while-revalidate hits.
    /// </summary>
    long StaleHits { get; }

    /// <summary>
    /// The hit rate (hits / total reads) as a ratio in [0, 1]. Returns 0 when no reads have occurred.
    /// </summary>
    double HitRate { get; }
}
