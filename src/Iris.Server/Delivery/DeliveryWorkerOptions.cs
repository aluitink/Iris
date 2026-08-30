namespace Iris.Server.Delivery;

/// <summary>
/// Options for the <see cref="DeliveryWorker"/> outbound-delivery concurrency (Phase 16.1, production
/// scaling).
/// </summary>
/// <remarks>
/// The worker pumps <see cref="DeliveryJob"/>s off the <see cref="IDeliveryQueue"/> and POSTs them to
/// recipients' inboxes. By default it delivers them <em>serially</em> (one in flight at a time), which
/// is simple but leaves a production instance's outbound bandwidth idle when a burst of deliveries is
/// queued (a popular local actor posting, a community following many remote actors, a relay fan-out).
///
/// <see cref="MaxConcurrentDeliveries"/> bounds how many deliveries may be in flight at once. A value
/// greater than 1 lets the worker deliver a burst in parallel — overlapping the per-delivery network
/// round-trips — while still capping the number of concurrent outbound connections the instance opens
/// (so a burst cannot exhaust the local connection pool or hammer a single remote peer). The value is a
/// <em>concurrency</em> cap, not a rate limit: deliveries are not throttled in time, only bounded in
/// parallelism. Each in-flight delivery still honors the per-job <see cref="DeliveryRetryOptions"/>
/// retry / dead-letter policy independently.
/// </remarks>
public sealed class DeliveryWorkerOptions
{
    /// <summary>
    /// The maximum number of deliveries the worker may have in flight at once. Must be at least 1
    /// (1 = serial delivery, the pre-Phase-16 behavior; a higher value delivers a burst in parallel).
    /// Defaults to 1.
    /// </summary>
    public int MaxConcurrentDeliveries { get; init; } = 1;
}
