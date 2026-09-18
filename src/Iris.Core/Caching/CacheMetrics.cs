using System.Threading;

namespace Iris.Core.Caching;

/// <summary>
/// A thread-safe <see cref="ICacheMetrics"/> backed by <see cref="Interlocked"/> counters.
/// </summary>
/// <remarks>
/// All counters are monotonically increasing; there is no reset (a cache's lifetime is the
/// measurement window). The <see cref="HitRate"/> is computed on read as
/// <c>hits / (hits + misses + staleHits)</c>.
/// </remarks>
public sealed class CacheMetrics : ICacheMetrics
{
    private long _hits;
    private long _misses;
    private long _staleHits;

    /// <inheritdoc/>
    public long Hits => Interlocked.Read(ref _hits);

    /// <inheritdoc/>
    public long Misses => Interlocked.Read(ref _misses);

    /// <inheritdoc/>
    public long StaleHits => Interlocked.Read(ref _staleHits);

    /// <inheritdoc/>
    public double HitRate
    {
        get
        {
            var total = Hits + Misses + StaleHits;
            return total == 0 ? 0.0 : (double)Hits / total;
        }
    }

    /// <inheritdoc/>
    public void RecordHit() => Interlocked.Increment(ref _hits);

    /// <inheritdoc/>
    public void RecordMiss() => Interlocked.Increment(ref _misses);

    /// <inheritdoc/>
    public void RecordStaleHit() => Interlocked.Increment(ref _staleHits);
}
