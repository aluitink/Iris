using Iris.Core;
using Iris.Client.Pipeline;

namespace Iris.Client;

/// <summary>
/// The client for **local, non-federated moderation** decisions: the Iris-specific capabilities that are
/// not ActivityStreams activities and so are *not* part of the AP-protocol
/// <see cref="IActivityPubClient"/> (which is a pure protocol layer).
/// </summary>
/// <remarks>
/// A mute (F-07) and a relay subscription (F-06) are local decisions of an actor on its own home
/// instance: there is no ActivityStreams <c>Mute</c> or "subscribe-to-relay" type, so neither is a
/// signed inbox delivery. Each is a **Basic-authenticated** request to the acting actor's own instance
/// (the instance identifies the actor from the credentials and records/removes the edge). This client
/// is the dedicated surface for those writes; the corresponding *reads* (the actor's
/// <c>mutes</c>/<c>relays</c> collections) remain on <see cref="IActivityPubClient"/> because they are
/// ordinary ActivityStreams collection reads.
/// </remarks>
public interface ILocalModerationClient
{
    /// <summary>
    /// Mutes an actor (F-07 moderation): a local moderation decision that hides
    /// <paramref name="targetId"/>'s content from <paramref name="actorId"/>'s feed without severing
    /// the follow (the inverse of a block's hard exclusion). Because there is no ActivityStreams
    /// <c>Mute</c> type (and a mute is a local, not federated, decision), this is a local,
    /// Basic-authenticated request to the actor's own instance (<c>POST {actorId}/mutes/{targetId}</c>),
    /// not a signed delivery to an inbox.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor performing the mute.</param>
    /// <param name="targetId">The IRI of the actor (a follow of the muter) to mute.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The request is authenticated by Basic auth (the acting actor's credentials, supplied at
    /// construction or the explicit-credentials overload
    /// <c>MuteAsync(Iri, Iri, ProxyCredentials, CancellationToken)</c>), not by an ActivityPub HTTP
    /// signature: a mute is not a federated activity, so it is not signed or delivered to an inbox.
    /// </remarks>
    public Task<DeliveryResult> MuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Mutes an actor (F-07 moderation) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor performing the mute.</param>
    /// <param name="targetId">The IRI of the actor (a follow of the muter) to mute.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> MuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Un-mutes an actor (F-07 moderation): the inverse of <see cref="MuteAsync(Iri, Iri,
    /// CancellationToken)"/> — a local, Basic-authenticated request to the actor's own instance
    /// (<c>POST {actorId}/mutes/{targetId}?unmute=true</c>) that removes the recorded mute edge.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-muting.</param>
    /// <param name="targetId">The IRI of the actor to un-mute (previously muted).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> UnmuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-mutes an actor (F-07 moderation) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-muting.</param>
    /// <param name="targetId">The IRI of the actor to un-mute.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> UnmuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Subscribes an actor to a relay (F-06): a local, Basic-authenticated request to the actor's own
    /// instance (<c>POST {actorId}/relays/{relayId}</c>) that records the relay (fan-out server) the
    /// actor's content will be fanned out through (the ActivityPub <c>star</c> set, AP §5.1.3).
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor subscribing to the relay.</param>
    /// <param name="relayId">The IRI of the relay (fan-out server) to subscribe to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// A relay subscription is an Iris-specific local decision (a local actor configures the relays it
    /// wants to fan out through), so — like a mute — it is a Basic-authenticated local request, not a
    /// signed inbox delivery (the acting actor's credentials are supplied at construction or the
    /// explicit-credentials overload <c>SubscribeRelayAsync(Iri, Iri, ProxyCredentials, CancellationToken)</c>).
    /// </remarks>
    public Task<DeliveryResult> SubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default);

    /// <summary>
    /// Subscribes an actor to a relay (F-06) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor subscribing to the relay.</param>
    /// <param name="relayId">The IRI of the relay (fan-out server) to subscribe to.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> SubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Un-subscribes an actor from a relay (F-06): the inverse of <see cref="SubscribeRelayAsync(Iri,
    /// Iri, CancellationToken)"/> — a local, Basic-authenticated request to the actor's own instance
    /// (<c>POST {actorId}/relays/{relayId}?unsubscribe=true</c>) that removes the recorded relay
    /// subscription.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-subscribing.</param>
    /// <param name="relayId">The IRI of the relay to un-subscribe from (previously subscribed).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> UnsubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default);

    /// <summary>
    /// Un-subscribes an actor from a relay (F-06) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-subscribing.</param>
    /// <param name="relayId">The IRI of the relay to un-subscribe from.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> UnsubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default);
}
