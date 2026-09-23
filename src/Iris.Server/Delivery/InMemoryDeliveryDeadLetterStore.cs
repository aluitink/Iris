using System.Collections.Concurrent;
using System.Linq;

namespace Iris.Server.Delivery;

/// <summary>
/// The default in-memory <see cref="IDeliveryDeadLetterStore"/> (F-22): a bounded, thread-safe
/// collection of the most recently dead-lettered deliveries.
/// </summary>
/// <remarks>
/// The store holds at most <c>capacity</c> entries (default
/// <see cref="DefaultCapacity"/> = 1000); when it is full, the oldest entry is evicted to make room for
/// the newest (a dead-letter is an operational signal, and the most recent failures are the most
/// actionable). Entries are returned by <see cref="ListAsync"/> newest-first. Ephemeral: a restart
/// clears the store (a production host swaps in a persistent store via the
/// <see cref="IDeliveryDeadLetterStore"/> seam).
/// </remarks>
public sealed class InMemoryDeliveryDeadLetterStore : IDeliveryDeadLetterStore
{
    /// <summary>
    /// The default maximum number of dead-lettered entries held before the oldest is evicted.
    /// </summary>
    public const int DefaultCapacity = 1000;

    private readonly int _capacity;
    private readonly ConcurrentDictionary<long, DeadLetterEntry> _entries = new();
    private long _sequence;

    /// <summary>
    /// Initializes a new store with the default capacity.
    /// </summary>
    public InMemoryDeliveryDeadLetterStore()
        : this(DefaultCapacity)
    {
    }

    /// <summary>
    /// Initializes a new store with the given capacity.
    /// </summary>
    /// <param name="capacity">The maximum number of entries held. Must be greater than 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is less than or equal to 0.</exception>
    public InMemoryDeliveryDeadLetterStore(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
        }

        _capacity = capacity;
    }

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        var key = _sequence++;
        _entries[key] = entry;
        // Evict beyond the capacity (a bounded operational signal; the most recent failures are the most
        // actionable). Drop the oldest (smallest sequence) until the bound holds.
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.MinBy(kv => kv.Key);
            _entries.TryRemove(oldest.Key, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Present entries newest-first (the dashboard shows the most recent failures on top).
        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(
            _entries.Values.OrderByDescending(e => e.DeadLetteredAtUtc).ToList());
    }

    /// <inheritdoc/>
    public Task RemoveAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        foreach (var kv in _entries)
        {
            if (kv.Value == entry)
            {
                _entries.TryRemove(kv.Key, out _);
                break;
            }
        }

        return Task.CompletedTask;
    }
}
