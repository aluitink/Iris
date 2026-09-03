namespace Iris.Server.Delivery;

/// <summary>
/// A per-peer outbound-delivery rate limiter (Phase 16.3, production scaling): bounds how many
/// deliveries the <see cref="DeliveryWorker"/> may send to a single remote peer per unit of time, so a
/// burst of deliveries (a relay fan-out, a popular actor posting to many followers, a retry storm to a
/// downed peer) does not hammer one peer's inbox endpoint.
/// </summary>
/// <remarks>
/// The peer is keyed by the <em>host</em> of the recipient's inbox IRI (e.g.
/// <c>https://b.domain.local/ap/v1/u/bob/inbox</c> → <c>b.domain.local</c>), not the full IRI: a peer's
/// shared inbox and per-actor inboxes all land on the same host, and a well-behaved instance wants a
/// single outbound rate to that host, not one per inbox. Delivery is bounded per host, so all inboxes
/// on a peer share the host's rate budget.
/// </remarks>
public interface IDeliveryRateLimiter
{
    /// <summary>
    /// Waits until a delivery to the peer behind <paramref name="inboxIri"/> is permitted, then records
    /// that the delivery was sent (consuming one slot in the peer's window). Returns immediately when the
    /// limiter is disabled or the peer's window still has room.
    /// </summary>
    /// <param name="inboxIri">The IRI of the inbox being delivered to. Its host is the rate-limit peer
    /// key. Must not be null.</param>
    /// <param name="ct">The cancellation token. A host shutdown cancels it; the wait returns
    /// (<see cref="OperationCanceledException"/>) promptly rather than blocking the shutdown.</param>
    /// <returns>A task that completes when the delivery is permitted and recorded.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="inboxIri"/> is null.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled while waiting.</exception>
    public Task WaitUntilPermittedAsync(Iri inboxIri, CancellationToken ct);
}
