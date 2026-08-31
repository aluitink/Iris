using System.Collections.Concurrent;
using System.Threading;

namespace Iris.Server.Observability;

/// <summary>
/// Iris's outbound-delivery metrics (Phase 17.2).
/// </summary>
/// <remarks>
/// A single shared instance (via DI) is handed to the <c>DeliveryService</c> and
/// <c>DeliveryWorker</c>, which record at the same points they already log — a metric accrues
/// whenever the corresponding log line fires. No behavioral change, only observation.
/// </remarks>
/// <remarks>
/// <strong>Design (no OpenTelemetry dependency).</strong> The counters are plain
/// <see cref="Interlocked"/>-based <see cref="long"/> fields (thread-safe, zero allocation, no NuGet).
/// A host that wants to export the metrics (Prometheus, OTLP, etc.) subscribes to
/// <see cref="SnapshotChanged"/> and reads <see cref="Snapshot"/> — or, once the
/// <c>System.Diagnostics.Metering</c> API is available on the target runtime, wraps this class in an
/// OTel <c>MeterProvider</c> (the recording methods map 1:1 to <c>Counter</c>/<c>Histogram</c>
/// instruments). The metrics are observable in tests by reading <see cref="Snapshot"/> after driving
/// a delivery.
/// </remarks>
/// <remarks>
/// <strong>Counter catalog.</strong>
/// <list type="bullet">
/// <item><c>Enqueued</c> — delivery jobs placed on the queue (cumulative).</item>
/// <item><c>Delivered</c> — deliveries that completed with a 2xx (cumulative).</item>
/// <item><c>AttemptFailed</c> — single delivery attempts that failed (cumulative; a job may
/// fail several attempts before succeeding or dead-lettering).</item>
/// <item><c>DeadLettered</c> — jobs that exhausted their retry budget (cumulative).</item>
/// </list>
/// Per-label breakdowns (by activity type, failure kind) are in <c>Snapshot.ByActivityType</c>
/// and <c>Snapshot.ByFailureKind</c>. The <c>duration</c> (wall-clock time of a single attempt)
/// is not tracked here — the retry backoff already bounds the time, and a histogram would need the
/// <c>System.Diagnostics.Metering</c> API (a follow-up). The <c>pending</c> gauge (queue backlog) is
/// reported by the <c>DeliveryQueueHealthCheck</c> (Phase 17.1), which already surfaces it to the
/// health endpoint.
/// </remarks>
public sealed class IrisDeliveryMetrics
{
    private long _enqueued;
    private long _delivered;
    private long _attemptFailed;
    private long _deadLettered;

    private readonly ConcurrentDictionary<string, long[]> _byActivityType;
    private readonly ConcurrentDictionary<string, long> _byFailureKind;

    /// <summary>
    /// Raised whenever any counter changes. A host (or test) that wants to observe the metrics
    /// subscribes here and reads <see cref="Snapshot"/> on the event. The event is raised on the
    /// thread that recorded the metric (the delivery worker / service thread).
    /// </summary>
    public event Action? SnapshotChanged;

    /// <summary>
    /// Initializes a new <see cref="IrisDeliveryMetrics"/> with all counters at zero.
    /// </summary>
    public IrisDeliveryMetrics()
    {
        // Index 0 = enqueued, 1 = delivered, 2 = attemptFailed, 3 = deadLettered (per activity type).
        _byActivityType = new ConcurrentDictionary<string, long[]>();
        _byFailureKind = new ConcurrentDictionary<string, long>();
    }

    /// <summary>
    /// Records that a delivery job was placed on the queue.
    /// </summary>
    /// <param name="activityType">The activity's type (e.g. "Follow", "Create").</param>
    public void RecordEnqueued(string activityType)
    {
        Interlocked.Increment(ref _enqueued);
        IncrByActivityType(activityType, 0);
        Raise();
    }

    /// <summary>
    /// Records that a delivery completed successfully (a 2xx response).
    /// </summary>
    /// <param name="activityType">The activity's type.</param>
    public void RecordDelivered(string activityType)
    {
        Interlocked.Increment(ref _delivered);
        IncrByActivityType(activityType, 1);
        Raise();
    }

    /// <summary>
    /// Records that a single delivery attempt failed.
    /// </summary>
    /// <param name="activityType">The activity's type.</param>
    /// <param name="kind">How the attempt failed (a non-2xx status or a transport error).</param>
    public void RecordAttemptFailed(string activityType, DeadLetterFailureKind kind)
    {
        Interlocked.Increment(ref _attemptFailed);
        IncrByActivityType(activityType, 2);
        IncrByFailureKind(kind);
        Raise();
    }

    /// <summary>
    /// Records that a job exhausted its retry budget and was dead-lettered (or dropped).
    /// </summary>
    /// <param name="activityType">The activity's type.</param>
    /// <param name="kind">The failure kind of the final attempt.</param>
    public void RecordDeadLettered(string activityType, DeadLetterFailureKind kind)
    {
        Interlocked.Increment(ref _deadLettered);
        IncrByActivityType(activityType, 3);
        IncrByFailureKind(kind);
        Raise();
    }

    /// <summary>
    /// A point-in-time snapshot of all counters. Reading this property is thread-safe (each field is
    /// read atomically); the snapshot is a consistent view of the cumulative totals at the moment of
    /// the read (not a transactional snapshot across all fields).
    /// </summary>
    public DeliveryMetricsSnapshot Snapshot
    {
        get
        {
            var byType = new Dictionary<string, ActivityTypeCounts>(_byActivityType.Count);
            foreach (var (type, counts) in _byActivityType)
            {
                byType[type] = new ActivityTypeCounts(
                    Interlocked.Read(ref counts[0]),
                    Interlocked.Read(ref counts[1]),
                    Interlocked.Read(ref counts[2]),
                    Interlocked.Read(ref counts[3]));
            }

            var byKind = new Dictionary<string, long>(_byFailureKind.Count);
            foreach (var (kind, count) in _byFailureKind)
            {
                byKind[kind] = count;
            }

            return new DeliveryMetricsSnapshot(
                Interlocked.Read(ref _enqueued),
                Interlocked.Read(ref _delivered),
                Interlocked.Read(ref _attemptFailed),
                Interlocked.Read(ref _deadLettered),
                byType,
                byKind);
        }
    }

    private void IncrByActivityType(string activityType, int index)
    {
        var counts = _byActivityType.GetOrAdd(activityType, _ => new long[4]);
        Interlocked.Increment(ref counts[index]);
    }

    private void IncrByFailureKind(DeadLetterFailureKind kind)
    {
        var key = kind.ToString();
        _byFailureKind.AddOrUpdate(key, 1, (_, old) => old + 1);
    }

    private void Raise() => SnapshotChanged?.Invoke();

    /// <summary>
    /// The per-activity-type breakdown of the four counters.
    /// </summary>
    public readonly record struct ActivityTypeCounts(long Enqueued, long Delivered, long AttemptFailed, long DeadLettered);

    /// <summary>
    /// A point-in-time snapshot of all delivery metrics.
    /// </summary>
    /// <param name="Enqueued">Total delivery jobs placed on the queue (cumulative).</param>
    /// <param name="Delivered">Total deliveries that completed with a 2xx (cumulative).</param>
    /// <param name="AttemptFailed">Total single delivery attempts that failed (cumulative).</param>
    /// <param name="DeadLettered">Total jobs that exhausted their retry budget (cumulative).</param>
    /// <param name="ByActivityType">Per-activity-type breakdown of the four counters.</param>
    /// <param name="ByFailureKind">Per-failure-kind breakdown of attempt failures + dead-letters.</param>
    public sealed record DeliveryMetricsSnapshot(
        long Enqueued,
        long Delivered,
        long AttemptFailed,
        long DeadLettered,
        IReadOnlyDictionary<string, ActivityTypeCounts> ByActivityType,
        IReadOnlyDictionary<string, long> ByFailureKind);
}
