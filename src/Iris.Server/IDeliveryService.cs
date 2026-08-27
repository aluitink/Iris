using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Schedules outbound federation activities for asynchronous delivery to remote inboxes.
/// </summary>
/// <remarks>
/// This is the outbound half of federation (the mirror of the inbound <see cref="IInboxProcessor"/>).
/// Activity handlers (e.g. <see cref="FollowActivityHandler"/>) call
/// <see cref="DeliverAsync(Iri, Activity, CancellationToken)"/> to schedule a response (an
/// <c>Accept</c>, a <c>Create</c>, etc.) for delivery to a recipient's inbox. The call is
/// non-blocking: it enqueues a <see cref="DeliveryJob"/> on the <see cref="IDeliveryQueue"/> and
/// returns immediately; a background <see cref="DeliveryWorker"/> dequeues jobs and POSTs them, signed
/// with the instance actor's key. Failures are logged and (per the error-handling conventions) do not
/// throw back to the handler — a delivery failure is a recoverable condition (the activity remains
/// stored and can be retried), not an exception.
/// </remarks>
public interface IDeliveryService
{
    /// <summary>
    /// Schedules an activity for delivery to the given inbox IRI.
    /// </summary>
    /// <param name="inboxIri">The absolute IRI of the recipient's inbox endpoint.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued (not when it has been
    /// delivered — delivery is asynchronous and background).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default);

    /// <summary>
    /// Schedules an activity for delivery to the recipient actor's inbox. The inbox IRI is derived
    /// from the recipient's actor IRI (<c>actorIri + "/inbox"</c>, the ActivityPub convention).
    /// </summary>
    /// <param name="recipientIri">The absolute IRI of the recipient actor.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    /// <remarks>
    /// This convenience overload derives the inbox as <c>recipientIri.InboxOf()</c>. A recipient whose
    /// document advertises a <c>sharedInbox</c> (or whose inbox is at a non-conventional IRI) should be
    /// delivered via <see cref="DeliverAsync(Iri, Activity, CancellationToken)"/> with the explicit
    /// inbox IRI. Resolving a remote actor's advertised inbox from its document is a follow-up (the
    /// remote object caches from Phase 3 are the seam).
    /// </remarks>
    public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default);
}
