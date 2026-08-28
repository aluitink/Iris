using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Caches remote actor public keys (JWKs) by their key IRI, using the default
/// <see cref="CachePolicy.Key"/> (1 hour fresh, 1 hour stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="JwkKey"/> (JWK JSON + algorithm label) — the public key material
/// a client fetches from a remote actor's <c>publicKey</c> link and later uses to verify inbound
/// signatures. A missing key (null factory result) is not cached, so it is retried on the next
/// lookup.
/// </remarks>
public sealed class KeyCache
{
    private readonly CachingReadThrough<JwkKey> _cache;

    /// <summary>
    /// Initializes a new <see cref="KeyCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.Key"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public KeyCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.Key;
        _cache = new CachingReadThrough<JwkKey>(new MemoryCache<JwkKey>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// Gets the cached key for <paramref name="key"/>, fetching with <paramref name="factory"/> on a
    /// miss (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The key IRI (the <c>publicKey.id</c> of the remote actor).</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when bypassing) to fetch the JWK; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The key (or null when absent) and whether it was a stale-while-revalidate hit.</returns>
    public async Task<(JwkKey? Value, bool WasStale)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<JwkKey?>> factory,
        CancellationToken ct = default)
    {
        var (value, wasStale, _) = await _cache.GetAsync(key, bypassCache, factory, ct).ConfigureAwait(false);
        return (value, wasStale);
    }
}
