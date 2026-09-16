using System.Collections.Concurrent;

namespace Iris.Testing;

/// <summary>
/// A test-only <see cref="IDeliveryQueue"/> that accumulates jobs without delivering them.
/// <see cref="TryDequeueAsync"/> always returns <c>null</c> (after a short timeout) so a background
/// <c>DeliveryWorker</c> registered against this queue idles without racing a test driver.
/// A test calls <see cref="TakeAll"/> to atomically retrieve and clear the accumulated jobs, then
/// delivers them inline via a <see cref="DeterministicDeliveryDriver"/> or directly.
/// </summary>
/// <remarks>
/// This queue is designed for round-trip integration tests where the test needs to drive delivery
/// deterministically (synchronously) instead of relying on the background worker's pump task being
/// scheduled under full-suite load. The background worker's async continuations can go unscheduled
/// (thread-pool starvation), leaving a job "stuck in-flight" — neither in the queue nor in the
/// dead-letter store. By making <c>TryDequeueAsync</c> return null, the worker never dequeues, and
/// the test has exclusive access to the jobs.
/// </remarks>
public sealed class TestDeliveryQueue : IDeliveryQueue
{
    private readonly ConcurrentQueue<DeliveryJob> _jobs = new();

    /// <inheritdoc/>
    public int Count => _jobs.Count;

    /// <summary>
    /// Atomically removes and returns all accumulated jobs (clearing the queue). Returns an empty
    /// list when no jobs are pending.
    /// </summary>
    public List<DeliveryJob> TakeAll()
    {
        var taken = new List<DeliveryJob>();
        while (_jobs.TryDequeue(out var job))
        {
            taken.Add(job);
        }

        return taken;
    }

    /// <inheritdoc/>
    public Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs.Enqueue(job);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        // Always return null (queue appears empty to the background worker). A short delay avoids a
        // tight spin in the worker's pump loop.
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        return null;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(CancellationToken ct = default)
    {
        // No-op: the queue is not channel-backed, so there is nothing to complete.
        return Task.CompletedTask;
    }
}
