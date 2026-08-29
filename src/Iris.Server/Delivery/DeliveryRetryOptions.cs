namespace Iris.Server.Delivery;

/// <summary>
/// Options for the <see cref="DeliveryWorker"/> retry / dead-letter policy (F-22 at-least-once delivery).
/// </summary>
/// <remarks>
/// A delivery that fails (network error or a non-2xx response) is retried up to
/// <see cref="MaxAttempts"/> <em>total</em> attempts (the initial attempt + <see cref="MaxAttempts"/> − 1
/// retries). Between attempts the worker waits an exponentially-growing delay (<see cref="BaseDelay"/>
/// doubled each retry, capped at <see cref="MaxDelay"/>) so a downed peer is not hammered. When all
/// attempts fail, the job is moved to the dead-letter store (an operator can inspect and re-drive it)
/// rather than dropped silently. A host may rebind this to tune the retry budget (e.g. a higher
/// <see cref="MaxAttempts"/> for a lossy network, or <see cref="MaxAttempts"/> = 1 for fail-fast).
/// </remarks>
/// <para>
/// <strong>Note on "at-least-once":</strong> a delivery that succeeds but whose <em>acknowledgement</em>
/// is lost (the POST returned 2xx but the response was dropped) is not detected — the worker treats a
/// 2xx as delivered. The retry policy therefore gives *at-least-once for failed attempts* (a delivery
/// is never dropped before <see cref="MaxAttempts"/> tries) but not idempotent exactly-once; the
/// receiving instance's inbox pipeline dedupes a re-delivered activity by its <c>Id</c> (C-07), so a
/// retried delivery is harmless (it is a no-op on the receiver).
/// </para>
public sealed class DeliveryRetryOptions
{
    /// <summary>
    /// The maximum number of <em>total</em> attempts for a delivery (the initial attempt + retries).
    /// Must be at least 1 (1 = no retry, fail-fast; the job is dead-lettered after the first failure).
    /// Defaults to 5.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// The initial backoff delay between the first and second attempt. Each subsequent retry doubles
    /// the delay (exponential backoff), capped at <see cref="MaxDelay"/>. Defaults to 1 second.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The upper bound on the backoff delay between retries. The exponentially-growing delay never
    /// exceeds this. Defaults to 60 seconds.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
}
