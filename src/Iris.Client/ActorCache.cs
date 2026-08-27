using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client;

/// <summary>
/// Caches fetched ActivityPub objects (actors and other documents) by their IRI, using the
/// default <see cref="CachePolicy.Actor"/> (5 minutes fresh, 5 minutes stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="IObject"/> (the library's object range) — callers pattern-match
/// the returned object rather than receiving a concrete type. A 404 / not-found (a
/// <see langword="null"/> factory result) is not cached, so it is retried on the next lookup.
/// </remarks>
public sealed class ActorCache
{
    private readonly CachingClientCache<IObject> _cache;

    /// <summary>
    /// Initializes a new <see cref="ActorCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.Actor"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public ActorCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.Actor;
        _cache = new CachingClientCache<IObject>(new MemoryCache<IObject>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// Gets the cached object for <paramref name="key"/>, fetching with <paramref name="factory"/> on a
    /// miss (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The object IRI.</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when bypassing) to fetch the object; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The object (or null when absent) and whether it was a stale-while-revalidate hit.</returns>
    public Task<(IObject? Value, bool WasStale)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<IObject?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, bypassCache, factory, ct);
}
