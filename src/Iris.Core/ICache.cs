namespace Iris.Core;

/// <summary>
/// A cache of values keyed by <see cref="Iri"/> (the natural identity of ActivityPub objects).
/// Supports time-based expiry (TTL), LRU eviction under a bounded size, and
/// stale-while-revalidate (serve a stale entry while refreshing it in the background).
/// </summary>
/// <typeparam name="TValue">The type of the cached value.</typeparam>
/// <remarks>
/// Implementations are expected to be thread-safe. The "clock" is injectable so that TTL and
/// eviction behavior can be tested deterministically without real sleeps.
/// </remarks>
public interface ICache<TValue>
{
    /// <summary>
    /// The policy (TTL / stale window) applied to entries written by this cache.
    /// </summary>
    public CachePolicy Policy { get; }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> when present and usable (fresh or
    /// stale), or null when there is no entry or it has expired.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="nowUtc">The reference "now" (injected for deterministic testing).</param>
    /// <returns>The value, or null when there is no usable entry.</returns>
    /// <remarks>
    /// A stale hit also returns the value (stale-while-revalidate) and records a "needs
    /// revalidation" flag visible to <see cref="TryGetEntry"/>; the caller is responsible for
    /// refreshing it in the background.
    /// </remarks>
    public TValue? Get(Iri key, DateTime nowUtc);

    /// <summary>
    /// Returns the full cached entry for <paramref name="key"/> when present and usable (fresh
    /// or stale), along with its <see cref="CacheState"/>. Returns null when there is no entry
    /// or it has expired.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="nowUtc">The reference "now" (injected for deterministic testing).</param>
    /// <returns>The entry and its state, or null when there is no usable entry.</returns>
    public (CacheEntry<TValue> Entry, CacheState State)? TryGetEntry(Iri key, DateTime nowUtc);

    /// <summary>
    /// Writes a value into the cache, timestamped with <paramref name="nowUtc"/>. Replaces any
    /// existing entry for <paramref name="key"/> and evicts the least-recently-used entry when
    /// the cache is at capacity.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="nowUtc">The reference "now" used as the entry's creation timestamp.</param>
    public void Put(Iri key, TValue value, DateTime nowUtc);

    /// <summary>
    /// Removes the entry for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key);

    /// <summary>
    /// The number of entries currently held (fresh or stale, not yet evicted).
    /// </summary>
    public int Count { get; }
}
