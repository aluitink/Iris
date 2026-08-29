using System.Threading.Channels;

namespace Iris.Server;

/// <summary>
/// The default in-memory <see cref="IDeliveryQueue"/>, backed by a bounded
/// <see cref="Channel{T}"/>.
/// </summary>
/// <remarks>
/// The channel is bounded (default capacity 1000) so an unbounded burst of deliveries cannot exhaust
/// process memory; when the channel is full, <see cref="EnqueueAsync"/> awaits space (back-pressure).
/// The queue completes when <see cref="CompleteAsync"/> is called (typically on host shutdown) and it
/// has drained; thereafter <see cref="TryDequeueAsync"/> returns <see langword="null"/> so a waiting
/// worker can shut down.
/// </remarks>
public sealed class InMemoryDeliveryQueue : IDeliveryQueue
{
    private readonly Channel<DeliveryJob> _channel;

    /// <summary>
    /// The default channel capacity (jobs).
    /// </summary>
    public const int DefaultCapacity = 1000;

    /// <summary>
    /// Initializes a new <see cref="InMemoryDeliveryQueue"/> with the default capacity.
    /// </summary>
    public InMemoryDeliveryQueue()
        : this(DefaultCapacity)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="InMemoryDeliveryQueue"/> with the given capacity.
    /// </summary>
    /// <param name="capacity">The maximum number of jobs the channel holds before back-pressure. Must be greater than 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is less than or equal to 0.</exception>
    public InMemoryDeliveryQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
        }

        _channel = Channel.CreateBounded<DeliveryJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <inheritdoc/>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// A point-in-time snapshot of the jobs currently queued, for inspection (e.g. by a test asserting
    /// which deliveries the <see cref="DeliveryService"/> scheduled). Reading does not remove the jobs
    /// (they are drained and re-enqueued, preserving order).
    /// </summary>
    /// <returns>The jobs currently pending, in queue order.</returns>
    public List<DeliveryJob> Jobs
    {
        get
        {
            var jobs = new List<DeliveryJob>();
            while (_channel.Reader.TryRead(out var job))
            {
                jobs.Add(job);
            }

            foreach (var job in jobs)
            {
                _channel.Writer.TryWrite(job);
            }

            return jobs;
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DeliveryJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        // CompleteAsync + empty → TryReadAsync returns false (the queue is done, no more items).
        if (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            _channel.Reader.TryRead(out var job);
            return job;
        }

        return null;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
