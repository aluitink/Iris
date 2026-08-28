using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Caches WebFinger resolution results (account → actor IRI) by the account IRI, using the
/// default <see cref="CachePolicy.WebFinger"/> (15 minutes fresh, 15 minutes stale).
/// </summary>
/// <remarks>
/// The value type is <see cref="WebFingerHit"/> (account + resolved actor IRI). A failed
/// resolution (a <see langword="null"/> factory result) is not cached, so it is retried on the
/// next lookup.
/// </remarks>
public sealed class WebFingerCache
{
    private readonly CachingReadThrough<WebFingerHit> _cache;

    /// <summary>
    /// Initializes a new <see cref="WebFingerCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to <see cref="CachePolicy.WebFinger"/>.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public WebFingerCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.WebFinger;
        _cache = new CachingReadThrough<WebFingerHit>(new MemoryCache<WebFingerHit>(resolved, capacity));
    }

    /// <summary>
    /// The policy (TTL / stale window) in effect for this cache.
    /// </summary>
    public CachePolicy Policy => _cache.Policy;

    /// <summary>
    /// Gets the cached resolved actor IRI for <paramref name="key"/>, resolving with
    /// <paramref name="factory"/> on a miss (or when <paramref name="bypassCache"/> is set).
    /// </summary>
    /// <param name="key">The account IRI (or acct: resource) to resolve.</param>
    /// <param name="bypassCache">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when bypassing) to resolve the actor IRI; null means unresolvable.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The resolved actor IRI (or null when unresolvable) and whether it was a stale-while-revalidate hit.</returns>
    public async Task<(Iri? Value, bool WasStale)> GetAsync(
        Iri key,
        bool bypassCache,
        Func<Iri, Task<Iri?>> factory,
        CancellationToken ct = default)
    {
        var (hit, wasStale, _) = await _cache.GetAsync(
            key,
            bypassCache,
            async account =>
            {
                var actorId = await factory(account).ConfigureAwait(false);
                if (!actorId.HasValue)
                {
                    return null;
                }

                return new WebFingerHit(account, actorId.Value);
            },
            ct).ConfigureAwait(false);

        return (hit?.ActorId, wasStale);
    }
}
