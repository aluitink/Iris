namespace Iris.Server.Delivery;

/// <summary>
/// Stores deliveries that exhausted their retry budget (F-22 dead-letter) so an operator can inspect and
/// re-drive them.
/// </summary>
/// <remarks>
/// The <see cref="InMemoryDeliveryDeadLetterStore"/> default is an in-memory bounded collection (the
/// most recently dead-lettered entries, up to <c>capacity</c>; the oldest are evicted when full). A
/// production host may swap in a persistent store (e.g. a database table) so dead letters survive a
/// restart; the <see cref="DeliveryWorker"/> depends only on this interface. The store is a sink — it
/// does not re-drive deliveries on its own; re-driving is an explicit operator action (call
/// <see cref="DeadLetterEntry.ToJob"/> and enqueue the result).
/// </remarks>
public interface IDeliveryDeadLetterStore
{
    /// <summary>
    /// Records a dead-lettered delivery.
    /// </summary>
    /// <param name="entry">The dead-letter entry. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the entry has been recorded.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="entry"/> is null.</exception>
    public Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default);

    /// <summary>
    /// The number of dead-lettered deliveries currently held (bounded; older entries may have been
    /// evicted once the store is full).
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Lists the currently-held dead-lettered deliveries (newest first).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes with the held entries (newest first; possibly empty).</returns>
    public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes the given dead-lettered entry from the store (S60: an operator re-driving a delivery via
    /// <see cref="DeadLetterEntry.ToJob"/> clears it so the replayed delivery is not shown — and, if it
    /// fails again, is re-recorded fresh — as a lingering dead letter).
    /// </summary>
    /// <param name="entry">The entry to remove (matched by reference/equality).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the entry has been removed (or is absent).</returns>
    public Task RemoveAsync(DeadLetterEntry entry, CancellationToken ct = default);
}
