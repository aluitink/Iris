using System.Collections.Concurrent;

namespace Iris.Server.Http.Proxy;

/// <summary>
/// Caches targets that returned <c>410 Gone</c> from a remote instance so the proxy does not
/// re-fetch them on every request. A gone remote actor's avatar/icon fetch would otherwise emit a
/// console 410 on every page load (the Blazor WASM client re-requests the same dead IRIs).
/// </summary>
/// <remarks>
/// Entries expire after a fixed TTL (default 1 hour) so a resurrected remote instance is retried
/// after the window. The cache is a bounded LRU: when <see cref="Capacity"/> is exceeded the oldest
/// entry is evicted. Thread-safe (all operations use <see cref="ConcurrentDictionary{TKey,TValue}"/>).
/// </remarks>
public sealed class ProxyGoneCache
{
    private readonly ConcurrentDictionary<string, DateTime> _gone = new(StringComparer.Ordinal);
    private readonly object _lruLock = new();
    private readonly List<string> _lru = new();

    /// <summary>The TTL for a cached 410 entry. Defaults to 1 hour.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>The maximum number of entries before LRU eviction. Defaults to 4096.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Initializes a new <see cref="ProxyGoneCache"/>.
    /// </summary>
    /// <param name="ttl">The time-to-live for a cached 410 entry. Defaults to 1 hour.</param>
    /// <param name="capacity">The maximum number of entries. Defaults to 4096.</param>
    public ProxyGoneCache(TimeSpan? ttl = null, int capacity = 4096)
    {
        Ttl = ttl ?? TimeSpan.FromHours(1);
        Capacity = capacity;
    }

    /// <summary>
    /// Returns true when <paramref name="target"/> is known to be gone (410) and the cache entry
    /// has not expired.
    /// </summary>
    /// <param name="target">The absolute target IRI.</param>
    /// <returns><see langword="true"/> when the target is cached as gone and not yet expired.</returns>
    public bool IsGone(string target)
    {
        if (!_gone.TryGetValue(target, out var cachedAt))
        {
            return false;
        }

        if (DateTime.UtcNow - cachedAt >= Ttl)
        {
            _gone.TryRemove(target, out _);
            lock (_lruLock)
            {
                _lru.Remove(target);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Records that <paramref name="target"/> returned 410 Gone. Evicts the oldest entry when the
    /// cache is at capacity.
    /// </summary>
    /// <param name="target">The absolute target IRI.</param>
    public void RecordGone(string target)
    {
        var now = DateTime.UtcNow;
        _gone[target] = now;

        lock (_lruLock)
        {
            _lru.Remove(target);
            _lru.Add(target);
            while (_lru.Count > Capacity)
            {
                var evict = _lru[0];
                _lru.RemoveAt(0);
                _gone.TryRemove(evict, out _);
            }
        }
    }

    /// <summary>The number of entries currently held (for observability/testing).</summary>
    public int Count
    {
        get
        {
            PruneExpired();
            return _gone.Count;
        }
    }

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (target, cachedAt) in _gone)
        {
            if (now - cachedAt >= Ttl)
            {
                _gone.TryRemove(target, out _);
            }
        }

        lock (_lruLock)
        {
            _lru.RemoveAll(t => !_gone.ContainsKey(t));
        }
    }
}
