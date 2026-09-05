using System.Collections.Concurrent;

namespace Iris.Core.Caching;

/// <summary>
/// A thread-safe, in-memory <see cref="ICache{TValue}"/> with TTL, LRU eviction under a bounded
/// capacity, and stale-while-revalidate.
/// </summary>
/// <typeparam name="TValue">The type of the cached value.</typeparam>
/// <remarks>
/// The "clock" is injectable via the <c>clock</c> constructor parameter (defaults to
/// <see cref="DateTime.UtcNow"/>), which makes TTL and eviction deterministic in tests. LRU is
/// tracked with a doubly-linked list over the concurrent dictionary; evictions of expired or
/// stale entries are opportunistic (triggered on read/write) plus a hard capacity bound.
/// </remarks>
public sealed class MemoryCache<TValue> : ICache<TValue>
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Func<DateTime> _clock;

    private readonly Dictionary<Iri, LinkedListNode<(Iri Key, CacheEntry<TValue> Entry)>> _list = new();
    private readonly LinkedList<(Iri Key, CacheEntry<TValue> Entry)> _lru = new();

    /// <summary>
    /// Initializes a new <see cref="MemoryCache{TValue}"/>.
    /// </summary>
    /// <param name="policy">The policy (TTL / stale window) applied to written entries.</param>
    /// <param name="capacity">The maximum number of entries to hold before LRU eviction.</param>
    /// <param name="clock">The source of "now". Defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public MemoryCache(CachePolicy policy, int capacity = 1024, Func<DateTime>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        Policy = policy;
        _capacity = capacity;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc/>
    public CachePolicy Policy { get; }

    /// <inheritdoc/>
    public int Count { get; private set; }

    /// <inheritdoc/>
    public TValue? Get(Iri key, DateTime nowUtc)
    {
        if (TryGetEntry(key, nowUtc) is { } entry)
        {
            return entry.Entry.Value;
        }

        return default;
    }

    /// <inheritdoc/>
    public (CacheEntry<TValue> Entry, CacheState State)? TryGetEntry(Iri key, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_list.TryGetValue(key, out var node))
            {
                return null;
            }

            var (entry, state) = (node.Value.Entry, node.Value.Entry.GetState(nowUtc));
            if (state == CacheState.Expired)
            {
                RemoveNode(node);
                return null;
            }

            MoveToMostRecentlyUsed(node);
            return (entry, state);
        }
    }

    /// <inheritdoc/>
    public void Put(Iri key, TValue value, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(value);
        var entry = new CacheEntry<TValue>(value, nowUtc, Policy.Ttl, Policy.StaleFor);

        lock (_gate)
        {
            if (_list.TryGetValue(key, out var existing))
            {
                RemoveNode(existing);
            }

            var node = new LinkedListNode<(Iri, CacheEntry<TValue>)>((key, entry));
            _list[key] = node;
            _lru.AddLast(node);
            Count = _list.Count;

            EvictExpired(nowUtc);
            EvictLeastRecentlyUsed();
        }
    }

    /// <inheritdoc/>
    public bool Invalidate(Iri key)
    {
        lock (_gate)
        {
            if (_list.TryGetValue(key, out var node))
            {
                RemoveNode(node);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Removes all entries from the cache (test isolation / teardown).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _list.Clear();
            _lru.Clear();
            Count = 0;
        }
    }

    private void RemoveNode(LinkedListNode<(Iri Key, CacheEntry<TValue> Entry)> node)
    {
        _list.Remove(node.Value.Key);
        _lru.Remove(node);
        Count = _list.Count;
    }

    private void MoveToMostRecentlyUsed(LinkedListNode<(Iri Key, CacheEntry<TValue> Entry)> node)
    {
        if (ReferenceEquals(_lru.Last, node))
        {
            return;
        }

        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void EvictExpired(DateTime nowUtc)
    {
        // Opportunistically drop entries that have fully expired so the capacity bound is not
        // wasted on dead data. Walk from the least-recently-used end and stop at the first live
        // entry (the list is not ordered by expiry, so this is a best-effort pass).
        var node = _lru.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Entry.GetState(nowUtc) == CacheState.Expired)
            {
                RemoveNode(node);
            }

            node = next;
        }
    }

    private void EvictLeastRecentlyUsed()
    {
        while (_list.Count > _capacity)
        {
            if (_lru.First is { } lru)
            {
                RemoveNode(lru);
            }
            else
            {
                break;
            }
        }
    }
}
