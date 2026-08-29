using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Records and queries relay subscriptions (F-06): the directed edges <c>subscribingActor →
/// relay</c> an instance's local actors have to relays (fan-out servers).
/// </summary>
/// <remarks>
/// A relay is a <c>star</c>-subscribed fan-out server (ActivityPub §5.1.3): an actor advertises the
/// relays it subscribes to via the <c>star</c> actor property, and its content is delivered to those
/// relays so the relays can fan it out to the wider federation. This store records which relays a
/// local actor subscribes to, so:
/// <list type="bullet">
/// <item>the actor's <c>relays</c> collection (served at <c>GET /ap/v1/u/{handle}/relays</c>) and the
/// <c>star</c> property on the actor document list the relays it subscribes to, and</item>
/// <item>the instance can deliver the actor's content to each subscribed relay (the relay fan-out, the
/// follow-up slice).</item>
/// </list>
/// A relay subscription is an Iris-specific <em>local</em> decision (a local actor configures the
/// relays it wants to fan out through): it is recorded from a local, authenticated request — it is not
/// interpreted from a federated activity (a relay is a remote server the actor points at, not an
/// activity the actor receives). A production host may swap in a persistent store; the handlers and
/// endpoints depend only on this interface.
/// </remarks>
public interface IRelayStore
{
    /// <summary>
    /// Records a relay subscription from <paramref name="actorIri"/> to <paramref name="relayIri"/>
    /// (F-06). Idempotent (subscribing to the same relay twice is a no-op).
    /// </summary>
    /// <param name="actorIri">The IRI of the local actor subscribing to the relay.</param>
    /// <param name="relayIri">The IRI of the relay (fan-out server) being subscribed to.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a relay subscription (an un-subscribe).
    /// </summary>
    /// <param name="actorIri">The IRI of the local actor that subscribed to the relay.</param>
    /// <param name="relayIri">The IRI of the relay being unsubscribed from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a subscription was removed.</returns>
    public Task<bool> RemoveRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the relays that <paramref name="actorIri"/> subscribes to (the actor's
    /// <c>relays</c> collection, the <c>star</c> set).
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose relay subscriptions are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the relay IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetRelaysAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="actorIri"/> subscribes to <paramref name="relayIri"/>.
    /// </summary>
    /// <param name="actorIri">The IRI of the potential subscriber.</param>
    /// <param name="relayIri">The IRI of the potential relay.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the subscription exists.</returns>
    public Task<bool> IsRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default);
}
