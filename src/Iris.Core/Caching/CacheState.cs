namespace Iris.Core.Caching;

/// <summary>
/// The freshness state of a <see cref="CacheEntry{TValue}"/> relative to a given "now".
/// </summary>
public enum CacheState
{
    /// <summary>
    /// The entry is fresh: within <see cref="CacheEntry{TValue}.Ttl"/> of its write time. Served from cache.
    /// </summary>
    Fresh,

    /// <summary>
    /// The entry is stale but still usable: past <see cref="CacheEntry{TValue}.Ttl"/> but within
    /// <see cref="CacheEntry{TValue}.StaleFor"/> of its write time. Served from cache while being
    /// revalidated in the background (stale-while-revalidate).
    /// </summary>
    Stale,

    /// <summary>
    /// The entry has expired: past <see cref="CacheEntry{TValue}.StaleFor"/> of its write time.
    /// Not served; must be refreshed.
    /// </summary>
    Expired,
}
