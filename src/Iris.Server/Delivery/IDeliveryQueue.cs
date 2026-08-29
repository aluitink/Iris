namespace Iris.Server.Delivery;

/// <summary>
/// A queue of outbound federation <see cref="DeliveryJob"/>s awaiting delivery.
/// </summary>
/// <remarks>
/// The <see cref="InMemoryDeliveryQueue"/> default is an in-memory
/// <see cref="System.Threading.Channels.Channel{T}"/> (per the async conventions — background delivery
/// work uses a <c>Channel</c>, never <c>Task.Run</c> fire-and-forget). A production host may swap in a
/// persistent queue (e.g. a database table) so pending deliveries survive a restart; the worker and
/// service depend only on this interface.
/// </remarks>
public interface IDeliveryQueue
{
    /// <summary>
    /// The approximate number of jobs currently queued (pending delivery).
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Adds a job to the queue for asynchronous delivery.
    /// </summary>
    /// <param name="job">The job to enqueue. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the job has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="job"/> is null.</exception>
    public Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default);

    /// <summary>
    /// Removes and returns the next queued job, waiting (up to the caller's cancellation) when the
    /// queue is empty.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The next <see cref="DeliveryJob"/>, or <see langword="null"/> when the queue is
    /// complete and empty (no further jobs will arrive).</returns>
    public Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the queue as complete: no further jobs will be enqueued, and once the queue drains,
    /// <see cref="TryDequeueAsync"/> returns <see langword="null"/>.
    /// </summary>
    /// <returns>A task that completes when completion has been signaled.</returns>
    public Task CompleteAsync(CancellationToken ct = default);
}
