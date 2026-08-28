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
    /// Schedules an activity for delivery to the given inbox IRI, signed as the instance actor
    /// (the system key for automated events).
    /// </summary>
    /// <param name="inboxIri">The absolute IRI of the recipient's inbox endpoint.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued (not when it has been
    /// delivered — delivery is asynchronous and background).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    public Task DeliverAsync(Iri inboxIri, Activity activity, CancellationToken ct = default);

    /// <summary>
    /// Schedules an activity for delivery to the given inbox IRI, signed as a specific local actor
    /// (the actor performing the automated event, e.g. the local actor being followed in a
    /// <c>Follow</c> → <c>Accept</c>).
    /// </summary>
    /// <param name="inboxIri">The absolute IRI of the recipient's inbox endpoint.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="actorIri">The local actor to sign the delivery as.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    public Task DeliverAsync(Iri inboxIri, Activity activity, Iri? actorIri, CancellationToken ct = default);

    /// <summary>
    /// Schedules an activity for delivery to the recipient actor's inbox. The inbox IRI is resolved
    /// from the recipient's advertised <c>endpoints.sharedInbox</c> (when its document advertises one)
    /// and otherwise falls back to the ActivityPub convention (<c>recipientIri + "/inbox"</c>).
    /// </summary>
    /// <param name="recipientIri">The absolute IRI of the recipient actor.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    /// <remarks>
    /// This convenience overload resolves the recipient's delivery target and signs as the instance
    /// actor. A remote actor's <c>sharedInbox</c> is read from its document (via
    /// <see cref="IActorDocumentFetcher"/>, which reads through the remote-actor cache); when the
    /// document cannot be fetched or advertises no <c>sharedInbox</c>, delivery falls back to
    /// <c>recipientIri.InboxOf()</c>. A recipient whose inbox is at a non-conventional IRI and that
    /// advertises no <c>sharedInbox</c> should be delivered via
    /// <see cref="DeliverAsync(Iri, Activity, CancellationToken)"/> with the explicit inbox IRI.
    /// </remarks>
    public Task DeliverToActorAsync(Iri recipientIri, Activity activity, CancellationToken ct = default);

    /// <summary>
    /// Schedules an activity for delivery to the recipient actor's inbox, signed as a specific local
    /// actor. The inbox IRI is resolved from the recipient's advertised <c>endpoints.sharedInbox</c>
    /// (when its document advertises one) and otherwise falls back to
    /// <c>recipientIri + "/inbox"</c>.
    /// </summary>
    /// <param name="recipientIri">The absolute IRI of the recipient actor.</param>
    /// <param name="activity">The activity to deliver. Must not be null.</param>
    /// <param name="actorIri">The local actor to sign the delivery as. When null, the delivery is
    /// signed as the instance actor (the system key for automated events).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the delivery has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    public Task DeliverToActorAsync(Iri recipientIri, Activity activity, Iri? actorIri, CancellationToken ct = default);
}
