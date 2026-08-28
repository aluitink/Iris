using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Caches the rendered **local** collection documents (an actor's <c>outbox</c>, <c>followers</c>, and
/// <c>following</c> — served from persistence) by the page's IRI. This is the server → client response
/// cache for the paged collection endpoints: it avoids re-reading persistence and re-rendering on every
/// public read, and it backs those endpoints' <c>Cache-Control</c> headers and <c>?refresh=true</c> bypass.
/// </summary>
/// <remarks>
/// The value type is the serialized collection document (<see cref="string"/>). The cache key is the
/// <em>page</em> IRI (<c>{collection}/?page=N</c>), which is distinct from the collection IRI itself:
/// each page (and each distinct <c>?limit</c>/<c>?page</c> combination) is its own entry, so a reader that
/// walks <c>next</c> links reuses a previously rendered page within the TTL. The default policy mirrors the
/// actor document: <c>max-age=60, stale-while-revalidate=300</c>.
/// </remarks>
public sealed class LocalCollectionPageCache
{
    /// <summary>
    /// The default fresh window for local collection pages (60 seconds), matching the
    /// <c>max-age=60</c> response header.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The default stale-while-revalidate window for local collection pages (300 seconds), matching the
    /// <c>stale-while-revalidate=300</c> response header.
    /// </summary>
    public static readonly TimeSpan DefaultStaleFor = TimeSpan.FromSeconds(300);

    private readonly CachingReadThrough<string> _cache;

    /// <summary>
    /// Initializes a new <see cref="LocalCollectionPageCache"/>.
    /// </summary>
    /// <param name="policy">The policy to apply. Defaults to 60s fresh / 300s stale.</param>
    /// <param name="capacity">The maximum number of entries before LRU eviction. Defaults to 1024.</param>
    public LocalCollectionPageCache(CachePolicy? policy = null, int capacity = 1024)
    {
        var resolved = policy ?? CachePolicy.Create(DefaultTtl, DefaultStaleFor);
        _cache = new CachingReadThrough<string>(new MemoryCache<string>(resolved, capacity));
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
    /// Removes the entry for <paramref name="key"/> (e.g. after the local collection changes).
    /// </summary>
    /// <param name="key">The page IRI.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Invalidate(Iri key) => _cache.Invalidate(key);

    /// <summary>
    /// Gets the cached rendered collection document for <paramref name="key"/>, rendering with
    /// <paramref name="factory"/> on a miss (or when <paramref name="forceRefresh"/> is set).
    /// </summary>
    /// <param name="key">The page IRI.</param>
    /// <param name="forceRefresh">When true, the cache is skipped for the read but a non-null result is written back.</param>
    /// <param name="factory">Invoked on a miss (or always, when refreshing) to render the document; null means absent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The document (or null when absent), whether it was a stale-while-revalidate hit, and whether it was a hit at all.</returns>
    public Task<(string? Value, bool WasStale, bool WasHit)> GetAsync(
        Iri key,
        bool forceRefresh,
        Func<Iri, Task<string?>> factory,
        CancellationToken ct = default)
        => _cache.GetAsync(key, forceRefresh, factory, ct);
}
