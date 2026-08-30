using Iris.Core;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.Collections.CollectionPage;

namespace Iris.Client;

/// <summary>
/// The primary ActivityPub client surface. Performs signed HTTP requests against remote
/// ActivityPub servers and operates on <c>KristofferStrube.ActivityStreams</c> types.
/// </summary>
/// <remarks>
/// Requests are signed by the client's <see cref="SigningHandler"/> (wired into the
/// <see cref="HttpMessageHandler"/> pipeline) using the <see cref="Iris.Core.Signing.SigningProfile.ClientToServer"/>
/// profile for bodyless GETs and the <see cref="Iris.Core.Signing.SigningProfile.ServerToServer"/> profile for
/// body-carrying POSTs. Responses are deserialized into <see cref="IObjectOrLink"/> and then
/// pattern-matched — never into a concrete type. See <see cref="ActivityPubClient"/> for the
/// default implementation and <see cref="IActivityPubClientFactory"/> for construction.
/// Implementations own their HTTP pipeline and must be disposed when no longer needed.
/// </remarks>
public interface IActivityPubClient : IDisposable
{
    /// <summary>
    /// Fetches an object (actor or otherwise) by IRI, signed with the
    /// <see cref="Iris.Core.Signing.SigningProfile.ClientToServer"/> profile.
    /// </summary>
    /// <param name="objectId">The IRI of the object to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized object, or null if the request failed or the body was empty.</returns>
    public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Fetches an actor by IRI, signed with the <see cref="Iris.Core.Signing.SigningProfile.ClientToServer"/>
    /// profile.
    /// </summary>
    /// <param name="actorId">The IRI of the actor to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized actor, or null if the request failed, the body was empty, or the
    /// fetched object is not an <see cref="Actor"/>.</returns>
    public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default);

    /// <summary>
    /// Fetches an instance's RFC 8555 NodeInfo document (instance metadata), served at
    /// <c>{instanceBase}/nodeinfo/2.0</c>.
    /// </summary>
    /// <param name="instanceBase">The instance's <c>/ap/v1</c> base IRI (e.g.
    /// <c>https://a.domain.local/ap/v1</c>). The NodeInfo IRI is derived from it as
    /// <c>{base}/nodeinfo/2.0</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The parsed <see cref="NodeInfo"/>, or null if the request failed or the body was not
    /// valid NodeInfo JSON.</returns>
    /// <remarks>
    /// The explorer's instance-overview screen reads this to show the instance name, software, and
    /// protocols. Like other reads, the request is signed with the
    /// <see cref="Iris.Core.Signing.SigningProfile.ClientToServer"/> profile.
    /// </remarks>
    public Task<NodeInfo?> GetNodeInfoAsync(Iri instanceBase, CancellationToken ct = default);

    /// <summary>
    /// Sends an ActivityPub activity to the given inbox IRI, signed with the
    /// <see cref="Iris.Core.Signing.SigningProfile.ServerToServer"/> profile (covers <c>digest</c> +
    /// <c>content-type</c>).
    /// </summary>
    /// <param name="inboxId">The inbox IRI to deliver to.</param>
    /// <param name="activity">The activity to send (must be an <see cref="Activity"/>; serialized
    /// with <see cref="Iris.Core.ActivityJson"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <exception cref="ArgumentException">When <paramref name="activity"/> is not an <see cref="Activity"/>.</exception>
    public Task<int> DeliverAsync(Iri inboxId, IObject activity, CancellationToken ct = default);

    /// <summary>
    /// Sends a signed <see cref="Follow"/> activity to the target actor's inbox so that
    /// <paramref name="actorId"/> follows <paramref name="targetId"/>. This is the client's
    /// one-call "follow" (it derives the target's inbox from the actor IRI via
    /// <see cref="IriExtensions.InboxOf(Iri)"/> and builds the <see cref="Follow"/> — the caller does
    /// not need to know the inbox IRI or hand-build the activity).
    /// </summary>
    /// <param name="actorId">The IRI of the actor performing the follow (must match the client's
    /// signing identity so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor (or community) to follow.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="Follow"/> is delivered to <c>targetId.InboxOf()</c> and is signed by the
    /// pipeline. The target's <c>Accept</c> (outbound delivery) is the remote instance's
    /// responsibility; a <c>202</c> here means the target's inbox accepted the follow.
    /// </remarks>
    public Task<int> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-follows <paramref name="targetId"/> as <paramref name="actorId"/> (the inverse of
    /// <see cref="FollowAsync"/>): builds an <see cref="KristofferStrube.ActivityStreams.Undo"/> of the
    /// <see cref="KristofferStrube.ActivityStreams.Follow"/> <paramref name="actorId"/> made of
    /// <paramref name="targetId"/> and delivers it through the signed pipeline to the follower's own inbox
    /// (per the ActivityPub un-follow convention — the party that made the follow undoes it, so the
    /// <c>Undo</c> is addressed to the follower's inbox, not the un-followed actor's).
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-following (must match the client's signing identity so
    /// the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor (or community) previously followed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Undo"/> is delivered to <c>actorId.InboxOf()</c>
    /// (the follower's own inbox) and is signed by the pipeline. Its <c>object</c> references the original
    /// <see cref="KristofferStrube.ActivityStreams.Follow"/> by IRI (the same deterministic
    /// <c>{actorId}/follows/{targetId}</c> IRI <see cref="FollowAsync"/> mints), and the <see
    /// cref="KristofferStrube.ActivityStreams.Undo"/> itself gets a deterministic,
    /// unique-per-(actor,target) IRI so a retried un-follow dedupes on the receiver.
    /// </remarks>
    public Task<int> UndoFollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Likes an object as <paramref name="actorId"/>: builds a <see cref="KristofferStrube.ActivityStreams.Like"/>
    /// (actor = <paramref name="actorId"/>, object = <paramref name="objectId"/>) and delivers it through
    /// the signed pipeline to the liker's own inbox (the local-write path, exactly like
    /// <see cref="PostNoteAsync(Iri, string, IEnumerable{Iri}, CancellationToken)"/> — a content object has no
    /// inbox of its own, only actors do). The instance records the like edge (liker → object) and federates it
    /// to the object's owner. This is the client's one-call "like" (the caller supplies only the liked
    /// object's IRI — the <see cref="KristofferStrube.ActivityStreams.Like"/> and the delivery target are
    /// derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor issuing the like (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="objectId">The IRI of the object being liked (a note, post, or other content object).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Like"/> is delivered to the liker's OWN inbox
    /// (<c>actorId.InboxOf()</c>, the local-write path — a content object has no inbox of its own, only
    /// actors do) and is signed by the pipeline. The instance records the like edge (liker → object) in the
    /// liker's <c>liked</c> collection and federates it to the object's owner. The <see
    /// cref="KristofferStrube.ActivityStreams.Like"/> gets a deterministic, unique-per-(actor,object) IRI so a
    /// retried like dedupes on the receiver.
    /// </remarks>
    public Task<int> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Blocks <paramref name="targetId"/> as <paramref name="actorId"/> (F-07 moderation): builds a
    /// <see cref="KristofferStrube.ActivityStreams.Block"/> activity and delivers it through the signed
    /// pipeline to the target actor's inbox so that <paramref name="actorId"/> blocks
    /// <paramref name="targetId"/>. This is the client's one-call "block" (it derives the target's
    /// inbox from the actor IRI via <see cref="IriExtensions.InboxOf(Iri)"/> and builds the
    /// <see cref="KristofferStrube.ActivityStreams.Block"/> — the caller does not need to know the inbox
    /// IRI or hand-build the activity).
    /// </summary>
    /// <param name="actorId">The IRI of the actor performing the block (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to block.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Block"/> is delivered to <c>targetId.InboxOf()</c>
    /// (the blocked actor's inbox, per ActivityPub §5.2.1.3) and is signed by the pipeline. The receiving
    /// instance records the <c>actorId → targetId</c> block edge in its moderation store. The
    /// <see cref="KristofferStrube.ActivityStreams.Block"/> gets a deterministic, unique-per-(actor,target)
    /// IRI so a retried block dedupes on the receiver.
    /// </remarks>
    public Task<int> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the actors that <paramref name="actorId"/> has blocked (F-07 moderation): reads the
    /// actor's <c>blocks</c> collection (served at <c>actorId.BlocksOf()</c>, i.e.
    /// <c>{actor}/blocks</c>) as a paged <see cref="OrderedCollection"/> of items, so the same
    /// enumeration/caching semantics apply (read through the <see cref="CollectionPageCache"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor whose blocks collection is requested.</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>,
    /// <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the blocked actors (as <see cref="IObjectOrLink"/> — a
    /// <see cref="Link"/> to each blocked actor's IRI), in the order the collection yields them.</returns>
    /// <remarks>
    /// The <c>blocks</c> collection is a stable, paged collection (page 1 an
    /// <see cref="OrderedCollection"/> with <c>first</c>; page N&gt;1 an
    /// <see cref="OrderedCollectionPage"/>), so it is enumerated exactly like any other collection (the
    /// items are the blocked actors' IRIs).
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetBlocksAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Un-blocks an actor (F-07 moderation): builds an <see cref="Undo"/> of the
    /// <see cref="Block"/> <paramref name="actorId"/> made of <paramref name="targetId"/> and delivers
    /// it to the target's inbox (the inverse of <see cref="BlockAsync"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-blocking (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to un-block (the actor previously blocked).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="Undo"/> is delivered to <c>targetId.InboxOf()</c> (the previously-blocked actor's
    /// inbox, so the receiving instance can remove the recorded edge) and is signed by the pipeline. Its
    /// <c>object</c> references the original <see cref="Block"/> by IRI (the same deterministic
    /// <c>{actor}/blocks/{target}</c> IRI <see cref="BlockAsync"/> mints), and the <see cref="Undo"/>
    /// itself gets a deterministic, unique-per-(actor,target) IRI so a retried un-block dedupes on the
    /// receiver.
    /// </remarks>
    public Task<int> UnblockAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Flags an actor (F-07 moderation): builds a <see cref="Flag"/> activity (actor =
    /// <paramref name="actorId"/>, object = <paramref name="targetId"/>) and delivers it to the target's
    /// inbox (a moderation report — the inverse is an <see cref="Undo"/> of the <see cref="Flag"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor flagging (must match the client's signing identity so
    /// the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to flag.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="Flag"/> is delivered to <c>targetId.InboxOf()</c> (the flagged actor's inbox) and is
    /// signed by the pipeline. The receiving instance records the <c>actorId → targetId</c> flag edge in
    /// its moderation store when either party is local (the flag is a report; it does not sever the
    /// relationship the way a <see cref="BlockAsync"/> block does). The <see cref="Flag"/> gets a
    /// deterministic, unique-per-(actor,target) IRI so a retried flag dedupes on the receiver.
    /// </remarks>
    public Task<int> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-flags an actor (F-07 moderation): the inverse of <see cref="FlagAsync"/> — builds an
    /// <see cref="Undo"/> activity referencing the original <see cref="Flag"/> (actor =
    /// <paramref name="actorId"/>, object = the <see cref="Flag"/> IRI for the pair) and delivers it to
    /// the target's inbox, removing the recorded <c>actorId → targetId</c> flag edge.
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-flagging (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to un-flag.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="Undo"/> is delivered to <c>targetId.InboxOf()</c> (the flagged actor's inbox) and
    /// is signed by the pipeline. It references the deterministic <see cref="Flag"/> IRI
    /// <c>{actorId}/flags/{targetId}</c> (the same IRI <see cref="FlagAsync"/> used), so the receiving
    /// instance resolves the original flag's parties from the stored <see cref="Flag"/> and removes the
    /// exact recorded edge (a local flagger of anyone, or a flagger of a local actor).
    /// </remarks>
    public Task<int> UnflagAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the actors that <paramref name="actorId"/> has flagged (F-07 moderation): reads the
    /// actor's <c>flags</c> collection (served at <c>actorId.FlagsOf()</c>, i.e.
    /// <c>{actor}/flags</c>) as a paged <see cref="OrderedCollection"/> of items, so the same
    /// enumeration/caching semantics apply (read through the <see cref="CollectionPageCache"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor whose flags collection is requested.</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>,
    /// <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the flagged actors (as <see cref="IObjectOrLink"/> — a
    /// <see cref="Link"/> to each flagged actor's IRI), in the order the collection yields them.</returns>
    /// <remarks>
    /// The <c>flags</c> collection is a stable, paged collection (page 1 an
    /// <see cref="OrderedCollection"/> with <c>first</c>; page N&gt;1 an
    /// <see cref="OrderedCollectionPage"/>), so it is enumerated exactly like any other collection (the
    /// items are the flagged actors' IRIs).
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetFlagsAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

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
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    /// <remarks>
    /// The request is authenticated by Basic auth (the acting actor's credentials, supplied via
    /// <see cref="ActivityPubClientOptions.LocalCredentials"/> or the explicit-credentials overload
    /// <c>MuteAsync(Iri, Iri, ProxyCredentials, CancellationToken)</c>), not by an ActivityPub HTTP
    /// signature: a mute is not a federated activity, so it is not signed or delivered to an inbox.
    /// </remarks>
    public Task<int> MuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Mutes an actor (F-07 moderation) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor performing the mute.</param>
    /// <param name="targetId">The IRI of the actor (a follow of the muter) to mute.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> MuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Un-mutes an actor (F-07 moderation): the inverse of <see cref="MuteAsync(Iri, Iri,
    /// CancellationToken)"/> — a local, Basic-authenticated request to the actor's own instance
    /// (<c>POST {actorId}/mutes/{targetId}/unmute</c>) that removes the recorded mute edge.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-muting.</param>
    /// <param name="targetId">The IRI of the actor to un-mute (previously muted).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> UnmuteAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-mutes an actor (F-07 moderation) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-muting.</param>
    /// <param name="targetId">The IRI of the actor to un-mute.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> UnmuteAsync(Iri actorId, Iri targetId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the actors that <paramref name="actorId"/> has muted (F-07 moderation): reads the
    /// actor's <c>mutes</c> collection (served at <c>actorId.MutesOf()</c>, i.e.
    /// <c>{actor}/mutes</c>) as a paged <see cref="OrderedCollection"/> of items, so the same
    /// enumeration/caching semantics apply (read through the <see cref="CollectionPageCache"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor whose mutes collection is requested.</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>,
    /// <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the muted actors (as <see cref="IObjectOrLink"/> — a
    /// <see cref="Link"/> to each muted actor's IRI), in the order the collection yields them.</returns>
    /// <remarks>
    /// The <c>mutes</c> collection is a stable, paged collection (page 1 an
    /// <see cref="OrderedCollection"/> with <c>first</c>; page N&gt;1 an
    /// <see cref="OrderedCollectionPage"/>), so it is enumerated exactly like any other collection (the
    /// items are the muted actors' IRIs).
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetMutesAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes an actor to a relay (F-06): a local, Basic-authenticated request to the actor's own
    /// instance (<c>POST {actorId}/relays/{relayId}</c>) that records the relay (fan-out server) the
    /// actor's content will be fanned out through (the ActivityPub <c>star</c> set, AP §5.1.3).
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor subscribing to the relay.</param>
    /// <param name="relayId">The IRI of the relay (fan-out server) to subscribe to.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    /// <remarks>
    /// A relay subscription is an Iris-specific local decision (a local actor configures the relays it
    /// wants to fan out through), so — like a mute — it is a Basic-authenticated local request, not a
    /// signed inbox delivery (the acting actor's credentials are supplied via
    /// <see cref="ActivityPubClientOptions.LocalCredentials"/> or the explicit-credentials overload
    /// <c>SubscribeRelayAsync(Iri, Iri, ProxyCredentials, CancellationToken)</c>).
    /// </remarks>
    public Task<int> SubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default);

    /// <summary>
    /// Subscribes an actor to a relay (F-06) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor subscribing to the relay.</param>
    /// <param name="relayId">The IRI of the relay (fan-out server) to subscribe to.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> SubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Un-subscribes an actor from a relay (F-06): the inverse of <see cref="SubscribeRelayAsync(Iri,
    /// Iri, CancellationToken)"/> — a local, Basic-authenticated request to the actor's own instance
    /// (<c>POST {actorId}/relays/{relayId}?unsubscribe=true</c>) that removes the recorded relay
    /// subscription.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-subscribing.</param>
    /// <param name="relayId">The IRI of the relay to un-subscribe from (previously subscribed).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> UnsubscribeRelayAsync(Iri actorId, Iri relayId, CancellationToken ct = default);

    /// <summary>
    /// Un-subscribes an actor from a relay (F-06) with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor un-subscribing.</param>
    /// <param name="relayId">The IRI of the relay to un-subscribe from.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the request (<c>204</c> on success).</returns>
    public Task<int> UnsubscribeRelayAsync(Iri actorId, Iri relayId, ProxyCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the relays that <paramref name="actorId"/> subscribes to (F-06): reads the actor's
    /// <c>relays</c> collection (served at <c>actorId.RelaysOf()</c>, i.e. <c>{actor}/relays</c> — the
    /// <c>star</c> set) as a paged <see cref="OrderedCollection"/> of items, so the same
    /// enumeration/caching semantics apply (read through the <see cref="CollectionPageCache"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor whose relay subscriptions are requested.</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>,
    /// <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the subscribed relays (as <see cref="IObjectOrLink"/> — a
    /// <see cref="Link"/> to each relay's IRI), in the order the collection yields them.</returns>
    public IAsyncEnumerable<IObjectOrLink> GetRelaysAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Posts a note as <paramref name="actorId"/>: builds a <see cref="Create"/> activity carrying an
    /// embedded <see cref="Note"/> with the given <paramref name="content"/> and delivers it through
    /// the signed pipeline to the actor's own inbox. This is the client's one-call "post a note" (the
    /// caller supplies only the content — the <see cref="Create"/>, the embedded <see cref="Note"/>,
    /// and the delivery target are all derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor authoring the note (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="content">The note's content (plain text or HTML).</param>
    /// <param name="to">Optional audience link(s) for the note (e.g. the public
    /// <c>as:Public</c> address). When null the note carries no explicit <c>to</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// The <see cref="Create"/> is delivered to <c>actorId.InboxOf()</c> (the author's own inbox) —
    /// the "local post" path: the post reaches the author's instance, which records it and federates
    /// it to followers (the outbound-to-followers leg is the server's responsibility, not the
    /// client's). The <see cref="Create"/> and the embedded <see cref="Note"/> each get a
    /// deterministic, unique IRI so a retried post dedupes on the receiver. The note's
    /// <c>attributedTo</c> is the author.
    /// </remarks>
    public Task<int> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default);

    /// <summary>
    /// Posts a **reply** as <paramref name="actorId"/> to the note at <paramref name="parentIri"/>:
    /// builds a <see cref="Create"/> carrying an embedded <see cref="Note"/> whose <c>inReplyTo</c> is
    /// the parent note and whose <c>tag</c> carries an <see cref="Mention"/> per <c>@mention</c> in
    /// <paramref name="mentions"/>, then delivers it through the signed pipeline to the author's inbox
    /// (F-12). This is the client's one-call "reply to a note" (the caller supplies the parent IRI, the
    /// content, and any mentions — the <see cref="Create"/>/embedded <see cref="Note"/>, the
    /// <c>inReplyTo</c>, the <c>tag</c> mentions, and the delivery target are all derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor authoring the reply (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="parentIri">The IRI of the note being replied to (the thread's parent). It is set as
    /// the note's <c>inReplyTo</c> and is what the parent's replies collection
    /// (<see cref="GetRepliesAsync"/>) lists.</param>
    /// <param name="content">The reply's content (plain text or HTML).</param>
    /// <param name="mentions">Optional IRIs of actors to <c>@mention</c> (each becomes an
    /// <see cref="Mention"/> <c>tag</c> whose <c>href</c> is the actor IRI). When null/empty the note
    /// carries no mention tags.</param>
    /// <param name="to">Optional audience link(s) for the reply (e.g. the public <c>as:Public</c>
    /// address). When null the reply carries no explicit <c>to</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The HTTP status code of the delivery (e.g. <c>202</c>).</returns>
    /// <remarks>
    /// Mirrors <see cref="PostNoteAsync"/> but sets <c>inReplyTo</c> (the parent) and, when
    /// <paramref name="mentions"/> is non-empty, a <c>tag</c> of <see cref="Mention"/> entries. The
    /// receiving server's <c>Create</c> handler records the parent → child reply edge (via the note's
    /// <c>inReplyTo</c>), which is what surfaces the reply under the parent's replies collection. The
    /// <see cref="Create"/> is delivered to <c>actorId.InboxOf()</c> (the author's own inbox).
    /// </remarks>
    public Task<int> PostReplyAsync(
        Iri actorId,
        Iri parentIri,
        string content,
        IEnumerable<Iri>? mentions = null,
        IEnumerable<Iri>? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerates the **replies** to a content object by the object's IRI: the objects that reply to it
    /// (their <c>inReplyTo</c> is the object's IRI), served by the server's
    /// <c>GET /o/{object-path}/replies</c> endpoint as a paged <see cref="OrderedCollection"/> of the
    /// reply objects' IRIs (F-12). Works identically to a personal/community/followed feed (a paged
    /// <see cref="OrderedCollection"/> of items), so the same enumeration/caching semantics apply.
    /// </summary>
    /// <param name="objectIri">The IRI of the object whose replies are requested (e.g. a
    /// <c>https://a.domain.local/ap/v1/u/alice/notes/n1</c> note).</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>, <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the reply IRIs (each an <see cref="IObjectOrLink"/> — a
    /// <see cref="Link"/> to the reply object; resolve it to the full object via
    /// <see cref="GetObjectAsync"/>). Yields nothing when the object has no replies, cannot be fetched,
    /// or is not stored by its instance.</returns>
    /// <remarks>
    /// The replies-collection IRI is derived from the object IRI via <see cref="IriExtensions.RepliesOf(Iri)"/>
    /// (<c>{object}/replies</c>). It is read through the client's <see cref="CollectionPageCache"/> like
    /// any other collection, so a replies page is fetched once and reused within the TTL.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetRepliesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a raw HTTP request through the client's signed pipeline and returns the response.
    /// </summary>
    /// <param name="request">The request to send. It is signed by the pipeline (the
    /// <see cref="SigningHandler"/> resolves the signing identity from the request's <c>X-Iris-Actor</c>
    /// header when present, otherwise the client's default actor).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The (unconsumed) response; the caller owns and disposes it.</returns>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the pages of an <see cref="OrderedCollection"/> by IRI, following the
    /// <c>next</c> link from the collection's <c>first</c> page until the last page (or until
    /// <see cref="CollectionQuery.Limit"/> items have been yielded).
    /// </summary>
    /// <param name="collectionId">The IRI of the collection (or of its <c>first</c> page).</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>, <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of <see cref="CollectionPage"/> in order. Yields nothing when the
    /// collection cannot be fetched (e.g. 404 / not an <see cref="OrderedCollectionPage"/>).</returns>
    /// <remarks>
    /// The collection's <c>first</c> link is followed to reach the first page; if the fetched
    /// object is itself an <see cref="OrderedCollectionPage"/> it is used directly. Each yielded
    /// page's <see cref="CollectionPage.NextPage"/> is followed until it is null (last page) or the
    /// <see cref="CollectionQuery.Limit"/> is reached.
    /// </remarks>
    public IAsyncEnumerable<CollectionPage> GetCollectionAsync(
        Iri collectionId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerates the **items** of an <see cref="OrderedCollection"/> by IRI, flattening the
    /// per-page <see cref="CollectionPage.Items"/> across pages in order.
    /// </summary>
    /// <param name="collectionId">The IRI of the collection (or of its <c>first</c> page).</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>, <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the collection's items (each an <see cref="IObjectOrLink"/>;
    /// callers pattern-match). Yields nothing when the collection cannot be fetched.</returns>
    public IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(
        Iri collectionId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerates the **community feed** items by community IRI: the community's unified feed
    /// (the union of its members' outbox activities, newest first), served by the server's
    /// <c>GET /c/{name}/feed</c> endpoint. Works identically to a personal feed (a paged
    /// <see cref="OrderedCollection"/> of items), so the same enumeration/caching semantics apply.
    /// </summary>
    /// <param name="communityId">The IRI of the community (e.g. <c>https://a.domain.local/ap/v1/c/iris</c>).</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>, <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the community feed's items (each an <see cref="IObjectOrLink"/>;
    /// callers pattern-match). Yields nothing when the feed cannot be fetched (e.g. 404).</returns>
    /// <remarks>
    /// The feed IRI is derived from the community IRI via <see cref="IriExtensions.FeedOf(Iri)"/>
    /// (<c>{community}/feed</c>). The feed is read through the client's
    /// <see cref="CollectionPageCache"/> like any other collection, so a feed page is fetched once and
    /// reused within the TTL.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetCommunityFeedAsync(
        Iri communityId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerates the **followed feed** (home timeline) items by actor IRI: the union of the actor's
    /// local and remote follows' outbox items (newest first, de-duplicated, capped by the server's feed
    /// options), served by the server's <c>GET /u/{handle}/feed</c> endpoint. Works
    /// identically to a community feed (a paged <see cref="OrderedCollection"/> of items), so the same
    /// enumeration/caching semantics apply.
    /// </summary>
    /// <param name="actorId">The IRI of the actor (e.g. <c>https://a.domain.local/ap/v1/u/alice</c>).</param>
    /// <param name="query">Optional enumeration options (<see cref="CollectionQuery.Limit"/>, <see cref="CollectionQuery.BypassCache"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async sequence of the followed feed's items (each an <see cref="IObjectOrLink"/>;
    /// callers pattern-match). Yields nothing when the feed cannot be fetched (e.g. 404) or the actor
    /// follows no one.</returns>
    /// <remarks>
    /// The feed IRI is derived from the actor IRI as <c>{actor}/feed</c> (the same wire shape the server
    /// advertises in the actor document's <c>feed</c> extension). The feed is read through the client's
    /// <see cref="CollectionPageCache"/> like any other collection, so a feed page is fetched once and
    /// reused within the TTL.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetFollowFeedAsync(
        Iri actorId,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Searches an instance's local actors (the directory) and stored content objects (F-13 global
    /// search), served by the server's <c>GET /ap/v1/search</c> endpoint.
    /// </summary>
    /// <param name="instanceBase">The instance's <c>/ap/v1</c> base IRI (e.g.
    /// <c>https://a.domain.local/ap/v1</c>). The search IRI is derived from it as
    /// <c>{base}/search</c> (via <see cref="IriExtensions.SearchOf(Iri)"/>).</param>
    /// <param name="query">The search query (a case-insensitive substring). An empty/whitespace query
    /// matches all actors and content objects (the directory / full listing).</param>
    /// <param name="options">Optional enumeration options (<see cref="SearchOptions.Limit"/>,
    /// <see cref="SearchOptions.BypassCache"/>, <see cref="SearchOptions.Offset"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching items (actors first, then content objects, each sorted by IRI), as an
    /// <see cref="IObjectOrLink"/> sequence; callers pattern-match. Returns an empty sequence when the
    /// endpoint is unreachable (e.g. 404) or the instance serves no global search.</returns>
    /// <remarks>
    /// Unlike the paged collections (which are walked by following <c>next</c> links), global search
    /// uses the <c>limit</c>/<c>offset</c> pagination shape, so the client requests a single page of up to
    /// <see cref="SearchOptions.Limit"/> items (default 100) at <see cref="SearchOptions.Offset"/> and
    /// returns it. The response is not cached (a search is a fresh query, not a stable collection).
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> SearchAsync(
        Iri instanceBase,
        string? query = null,
        SearchOptions? options = null,
        CancellationToken ct = default);
}
