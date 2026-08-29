namespace Iris.Core.Caching;

/// <summary>
/// Describes how long entries in a cache stay fresh and how much longer they stay usable
/// (stale) before they are evicted.
/// </summary>
/// <remarks>
/// The default TTLs follow the [Architecture caching strategy](../../docs/ARCHITECTURE.md):
/// actors 5 minutes (client) / 1 hour (server), collection pages 30 seconds, remote keys 1 hour.
/// <see cref="StaleFor"/> enables stale-while-revalidate: a stale entry is served immediately
/// while it is refreshed in the background, so the caller never blocks on a slow refresh.
/// </remarks>
public readonly record struct CachePolicy(TimeSpan Ttl, TimeSpan StaleFor)
{
    /// <summary>
    /// The default policy for cached **actors** (5 minutes fresh, then stale for 5 minutes).
    /// </summary>
    public static CachePolicy Actor { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(5));

    /// <summary>
    /// The default policy for cached **collection pages** (30 seconds fresh, then stale for 30 seconds).
    /// </summary>
    public static CachePolicy CollectionPage { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30));

    /// <summary>
    /// The default policy for cached **remote public keys** (1 hour fresh, then stale for 1 hour).
    /// </summary>
    public static CachePolicy Key { get; } = new(
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(1));

    /// <summary>
    /// The default policy for cached **WebFinger** lookups (15 minutes fresh, then stale for 15 minutes).
    /// </summary>
    public static CachePolicy WebFinger { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(15));

    /// <summary>
    /// Validates the policy and returns it.
    /// </summary>
    /// <param name="ttl">How long an entry is fresh after it is written. Must be &gt; 0.</param>
    /// <param name="staleFor">How long an entry is usable-but-stale after it is written. Must be &gt; 0.</param>
    /// <returns>A validated <see cref="CachePolicy"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="ttl"/> or <paramref name="staleFor"/> is not positive.</exception>
    public static CachePolicy Create(TimeSpan ttl, TimeSpan staleFor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleFor, TimeSpan.Zero);
        return new CachePolicy(ttl, staleFor);
    }
}
