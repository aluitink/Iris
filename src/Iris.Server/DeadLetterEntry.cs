using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// A delivery that exhausted its retry budget (F-22): the activity, the target inbox, how many times it
/// was tried, the last failure (status or error), and when it was dead-lettered.
/// </summary>
/// <remarks>
/// An operator inspects the dead-letter store to see which deliveries permanently failed (a downed peer,
/// a malformed inbox, a persistent 4xx) and can re-drive them manually (re-enqueue the job once the
/// recipient is reachable). A <see cref="FailureKind"/> distinguishes a non-2xx response (the status
/// code is recorded in <see cref="FailureDetail"/>) from a network/transport exception (the exception
/// message is recorded). The entry is an immutable snapshot; re-driving creates a fresh
/// <see cref="DeliveryJob"/> (attempt count reset).
/// </remarks>
/// <param name="InboxIri">The absolute IRI of the recipient's inbox the delivery targeted.</param>
/// <param name="Activity">The activity that could not be delivered.</param>
/// <param name="ActorIri">The local actor the delivery was signed as, or null (signed as the instance
/// actor).</param>
/// <param name="Attempts">The number of delivery attempts made before the job was dead-lettered (the
/// configured retry budget, <see cref="DeliveryRetryOptions.MaxAttempts"/>).</param>
/// <param name="FailureKind">Whether the last failure was a non-2xx response or a transport error.</param>
/// <param name="FailureDetail">For a non-2xx: the HTTP status code; for a transport error: the
/// exception message. May be null/empty.</param>
/// <param name="DeadLetteredAtUtc">The UTC timestamp the job was moved to the dead-letter store.</param>
public sealed record DeadLetterEntry(
    Iri InboxIri,
    Activity Activity,
    Iri? ActorIri,
    int Attempts,
    DeadLetterFailureKind FailureKind,
    string? FailureDetail,
    DateTimeOffset DeadLetteredAtUtc)
{
    /// <summary>
    /// Returns the original <see cref="DeliveryJob"/> this entry came from (attempt count reset to 0),
    /// for an operator to re-drive the delivery (e.g. once the recipient is reachable again).
    /// </summary>
    /// <returns>A fresh <see cref="DeliveryJob"/> for the dead-lettered activity.</returns>
    public DeliveryJob ToJob() => new(InboxIri, Activity, ActorIri);
}

/// <summary>
/// The kind of failure that caused a delivery to be dead-lettered (F-22).
/// </summary>
public enum DeadLetterFailureKind
{
    /// <summary>The recipient returned a non-2xx HTTP status (recorded in <see cref="DeadLetterEntry.FailureDetail"/>).</summary>
    NonSuccessStatus = 0,

    /// <summary>A network/transport error (connection failed, timeout, etc.; the exception message is recorded in
    /// <see cref="DeadLetterEntry.FailureDetail"/>).</summary>
    TransportError = 1,
}
