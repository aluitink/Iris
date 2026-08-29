using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Security;

/// <summary>
/// Caches remote actor documents (fetched from other servers during federation) by their IRI, using
/// the default <see cref="CachePolicy.Actor"/> server-side policy (1 hour fresh, 1 hour stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="IObject"/> (the library's object range) — the remote actor's document
/// as deserialized. A 404 / not-found (a <see langword="null"/> factory result) is not cached, so it is
/// retried on the next lookup. This cache is populated by the server's outbound federation paths
/// (inbound signature validation, object delivery) in later phases.
/// </remarks>
public sealed class RemoteActorCache
{
    private readonly CachingReadThrough<IObject> _cache;

    /// <summary>
    /// Initializes a new <see cref="RemoteActorCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.Actor"/> (server-side 1h).</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public RemoteActorCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.Create(TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        _cache = new CachingReadThrough<IObject>(new MemoryCache<IObject>(resolved, capacity));
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
    /// Removes the entry for <paramref name="key"/> (e.g. after receiving an <c>Update</c>).
    /// </summary>
    /// <param name="key">The actor IRI.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Gets the cached remote actor for <paramref name="key"/>, fetching with <paramref name="factory"/> on
    /// a miss (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The actor IRI.</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when refreshing) to fetch the actor; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor (or null when absent), whether it was a stale-while-revalidate hit, and whether it was a hit at all.</returns>
    public Task<(IObject? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<IObject?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, bypassCache, factory, ct);
}
