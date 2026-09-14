using System.Collections.Concurrent;

namespace Iris.Server.Observability;

/// <summary>
/// The default <see cref="IFederationTraceCollector"/>: a bounded, in-memory, thread-safe capture of
/// federation request/response observations (Phase 136.1).
/// </summary>
/// <remarks>
/// <strong>Bounded memory.</strong> The collector keeps at most the configured capacity entries (the most
/// recent). When the buffer is full, the oldest entry is dropped as new ones arrive (a simple ring).
/// This bounds memory for a long-running instance while still retaining the recent window an operator
/// needs to diagnose a scenario. The default capacity (1000) comfortably holds a full interop scenario
/// (dozens of requests) while capping the worst case.
///
/// <strong>Thread safety.</strong> Inbound (the ASP.NET request pipeline) and outbound (the delivery
/// worker) threads record concurrently; the buffer is a <see cref="ConcurrentQueue{T}"/> and the
/// overflow trim is best-effort (a lost trim under contention is harmless — it only delays dropping an
/// old entry by one more record).
///
/// <strong>Disabled state.</strong> A capacity of 0 disables capture (the collector becomes a no-op),
/// so a host can turn trace capture off without changing wiring.
/// </remarks>
public sealed class InMemoryFederationTraceCollector : IFederationTraceCollector
{
    /// <summary>
    /// The default maximum number of entries retained (1000).
    /// </summary>
    public const int DefaultCapacity = 1000;

    private readonly ConcurrentQueue<FederationTraceEntry> _entries = new();
    private readonly int _capacity;

    /// <summary>
    /// Initializes a new <see cref="InMemoryFederationTraceCollector"/> with the default capacity
    /// (<see cref="DefaultCapacity"/>).
    /// </summary>
    public InMemoryFederationTraceCollector()
        : this(DefaultCapacity)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="InMemoryFederationTraceCollector"/>.
    /// </summary>
    /// <param name="capacity">The maximum number of entries to retain. A value of 0 disables capture
    /// (the collector becomes a no-op); a negative value is clamped to 0.</param>
    public InMemoryFederationTraceCollector(int capacity)
    {
        _capacity = Math.Max(0, capacity);
    }

    /// <summary>
    /// Records a federation request/response observation. A no-op when the buffer is disabled
    /// (capacity 0). When the buffer exceeds the capacity, the oldest entries are dropped until it is
    /// back within the bound.
    /// </summary>
    /// <param name="entry">The observed entry. Must not be null.</param>
    public void Record(FederationTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_capacity == 0)
        {
            return;
        }

        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // Drop the oldest entry to stay within the bound.
        }
    }

    /// <summary>
    /// Returns a point-in-time, ordered (oldest-first) snapshot of the captured entries as a copy.
    /// Subsequent <see cref="Record"/> calls do not affect the returned array.
    /// </summary>
    public IReadOnlyList<FederationTraceEntry> Snapshot()
    {
        return _capacity == 0 ? [] : _entries.ToArray();
    }

    /// <summary>
    /// Clears the captured entries.
    /// </summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            // Drain.
        }
    }
}
