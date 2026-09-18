namespace Iris.Core.Caching;

/// <summary>
/// A no-op <see cref="ICacheMetrics"/> that discards all counters. Use when metrics are not needed
/// (the default when no metrics instance is supplied to <see cref="CachingReadThrough{TValue}"/>).
/// </summary>
public sealed class NullCacheMetrics : ICacheMetrics
{
    /// <summary>
    /// The shared no-op instance.
    /// </summary>
    public static readonly NullCacheMetrics Instance = new();

    private NullCacheMetrics()
    {
    }

    /// <inheritdoc/>
    public long Hits => 0;

    /// <inheritdoc/>
    public long Misses => 0;

    /// <inheritdoc/>
    public long StaleHits => 0;

    /// <inheritdoc/>
    public double HitRate => 0.0;

    /// <inheritdoc/>
    public void RecordHit()
    {
    }

    /// <inheritdoc/>
    public void RecordMiss()
    {
    }

    /// <inheritdoc/>
    public void RecordStaleHit()
    {
    }
}
