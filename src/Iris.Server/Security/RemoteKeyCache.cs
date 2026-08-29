using Iris.Client;
using Iris.Core;

namespace Iris.Server.Security;

/// <summary>
/// Caches remote actor public keys (JWKs) by their key IRI, using the default
/// <see cref="CachePolicy.Key"/> policy (1 hour fresh, 1 hour stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="JwkKey"/> (JWK JSON + algorithm label) — the public key material the
/// server fetches from a remote actor's <c>publicKey</c> link and later uses to verify inbound
/// signatures. A missing key (null factory result) is not cached, so it is retried on the next
/// lookup. Key rotation invalidates the entry for that key IRI.
/// <para>
/// The type is (deliberately) not sealed so a host can extend it — for example, to count or observe
/// <see cref="Invalidate"/> calls (the F-21 key-rotation invalidation path) or to back the cache with
/// a different store — while still using the default <see cref="CachingReadThrough{TValue}"/> behavior
/// for everything else.
/// </para>
/// </remarks>
public class RemoteKeyCache
{
    private readonly CachingReadThrough<JwkKey> _cache;

    /// <summary>
    /// Initializes a new <see cref="RemoteKeyCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.Key"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public RemoteKeyCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.Key;
        _cache = new CachingReadThrough<JwkKey>(new MemoryCache<JwkKey>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// The number of entries currently held (for observability/testing).
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Removes the entry for <paramref name="key"/> (e.g. after key rotation).
    /// </summary>
    /// <param name="key">The key IRI (the <c>publicKey.id</c> of the remote actor).</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    /// <remarks>
    /// <see langword="virtual"/> so a host can extend the cache and observe or count invalidations
    /// (the F-21 key-rotation path) while reusing the default read-through behavior.
    /// </remarks>
    public virtual bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Gets the cached key for <paramref name="key"/>, fetching with <paramref name="factory"/> on a miss
    /// (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The key IRI (the <c>publicKey.id</c> of the remote actor).</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when refreshing) to fetch the JWK; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The key (or null when absent), whether it was a stale-while-revalidate hit, and whether it was a hit at all.</returns>
    public Task<(JwkKey? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<JwkKey?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, bypassCache, factory, ct);
}
