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
    /// Sends an ActivityPub activity to the given target IRI, signed with the
    /// <see cref="Iris.Core.Signing.SigningProfile.ServerToServer"/> profile (covers <c>digest</c> +
    /// <c>content-type</c>). The target is typically the author's own outbox (the write surface for the
    /// activities an actor authors); the server owns the recipient hop (delivering to the target
    /// actor's inbox).
    /// </summary>
    /// <param name="targetId">The target IRI to deliver to (typically <c>actorId.OutboxOf()</c>).</param>
    /// <param name="activity">The activity to send (must be an <see cref="Activity"/>; serialized
    /// with <see cref="Iris.Core.ActivityJson"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <exception cref="ArgumentException">When <paramref name="activity"/> is not an <see cref="Activity"/>.</exception>
    public Task<DeliveryResult> DeliverAsync(Iri targetId, IObject activity, CancellationToken ct = default);

    /// <summary>
    /// Follows <paramref name="targetId"/> as <paramref name="actorId"/>: builds the
    /// <see cref="Follow"/> (actor = <paramref name="actorId"/>, object = <paramref name="targetId"/>)
    /// and publishes it to <paramref name="actorId"/>'s own outbox, signed by the pipeline. This is the
    /// client's one-call "follow" (the caller supplies only the target's IRI — the
    /// <see cref="Follow"/> and the delivery target are derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor performing the follow (must match the client's
    /// signing identity so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor (or community) to follow.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="Follow"/> is published to <c>actorId.OutboxOf()</c> (the write surface for the
    /// activities an actor authors) and is signed by the pipeline. The server records the follow edge in
    /// <paramref name="actorId"/>'s outbox and server-delivers the <see cref="Follow"/> to the target's
    /// inbox (the server owns the recipient hop). The target's <c>Accept</c> is the remote instance's
    /// responsibility; a <c>202</c> here means the actor's outbox accepted the follow.
    /// </remarks>
    public Task<DeliveryResult> FollowAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-follows as <paramref name="actorId"/> (the inverse of <see cref="FollowAsync"/>): builds an
    /// <see cref="KristofferStrube.ActivityStreams.Undo"/> of the original
    /// <see cref="KristofferStrube.ActivityStreams.Follow"/> and publishes it to <paramref
    /// name="actorId"/>'s own outbox (per the ActivityPub un-follow convention — the party that made the
    /// follow undoes it, so the <c>Undo</c> is authored by the follower, not the un-followed actor).
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-following (must match the client's signing identity so
    /// the request is signed as that actor).</param>
    /// <param name="originalFollowId">The id the server minted for the original follow — learned from
    /// <see cref="DeliveryResult.MintedId"/> when the follow was made via <see cref="FollowAsync"/>.
    /// (Decision 055: the client references the follow by its learned id, never a recomputed formula.)</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, the response
    /// body, and the server-minted id of the <c>Undo</c> (when present).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Undo"/> is published to <c>actorId.OutboxOf()</c>
    /// (the follower's own outbox — the write surface for the activities an actor authors) and is signed by
    /// the pipeline. Its <c>object</c> references the original
    /// <see cref="KristofferStrube.ActivityStreams.Follow"/> by its learned id. The server mints the
    /// <c>Undo</c>'s own id (an unguessable ULID) and returns it in the 2xx body; it then removes the local
    /// follow edge and server-delivers the <c>Undo</c> to the previously-followed actor's inbox.
    /// </remarks>
    public Task<DeliveryResult> UndoFollowAsync(Iri actorId, Iri originalFollowId, CancellationToken ct = default);

    /// <summary>
    /// Accepts an inbound <see cref="KristofferStrube.ActivityStreams.Follow"/> as <paramref name="actorId"/>
    /// (the <em>followed</em> side's decision): builds the deterministic <see cref="KristofferStrube.ActivityStreams.Accept"/>
    /// whose <c>object</c> references <paramref name="followIri"/> and publishes it to the followed actor's
    /// own outbox, signed by the pipeline as <paramref name="actorId"/>.
    /// </summary>
    /// <param name="actorId">The IRI of the actor being followed (the one deciding — must match the client's
    /// signing identity so the request is signed as that actor).</param>
    /// <param name="followIri">The absolute IRI of the inbound <see cref="KristofferStrube.ActivityStreams.Follow"/>
    /// being accepted (the deterministic <c>{follower}/follows/{target}</c> IRI the follower recorded).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Accept"/> is published to <c>actorId.OutboxOf()</c>
    /// (the write surface for the activities an actor authors) and is signed by the pipeline. The server
    /// records the Accept in the actor's outbox, ensures the follower→actor follow edge, and delivers the
    /// Accept to the follower's inbox (the server owns the recipient hop). A <c>202</c> means the actor's
    /// outbox accepted the Accept. The <see cref="KristofferStrube.ActivityStreams.Accept"/> gets a
    /// deterministic, unique-per-(actor,follow) IRI (<c>{actorId}/accepts/{followIri}</c>) so a retried
    /// accept dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> AcceptAsync(Iri actorId, Iri followIri, CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound <see cref="KristofferStrube.ActivityStreams.Follow"/> as <paramref name="actorId"/>
    /// (the <em>followed</em> side's decision): builds the deterministic <see cref="KristofferStrube.ActivityStreams.Reject"/>
    /// whose <c>object</c> references <paramref name="followIri"/> and publishes it to the followed actor's
    /// own outbox, signed by the pipeline as <paramref name="actorId"/>.
    /// </summary>
    /// <param name="actorId">The IRI of the actor being followed (the one deciding — must match the client's
    /// signing identity so the request is signed as that actor).</param>
    /// <param name="followIri">The absolute IRI of the inbound <see cref="KristofferStrube.ActivityStreams.Follow"/>
    /// being rejected (the deterministic <c>{follower}/follows/{target}</c> IRI the follower recorded).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Reject"/> is published to <c>actorId.OutboxOf()</c>
    /// (the write surface for the activities an actor authors) and is signed by the pipeline. The server
    /// records the Reject in the actor's outbox, removes the provisional follower→actor follow edge, and
    /// delivers the Reject to the follower's inbox (the server owns the recipient hop). A <c>202</c> means
    /// the actor's outbox accepted the Reject. The <see cref="KristofferStrube.ActivityStreams.Reject"/> gets
    /// a deterministic, unique-per-(actor,follow) IRI (<c>{actorId}/rejects/{followIri}</c>) so a retried
    /// reject dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> RejectAsync(Iri actorId, Iri followIri, CancellationToken ct = default);

    /// <summary>
    /// Requests to join a community as <paramref name="actorId"/>: builds a <see cref="KristofferStrube.ActivityStreams.Join"/>
    /// (actor = <paramref name="actorId"/>, object = <paramref name="communityIri"/>) and publishes it through
    /// the signed pipeline to the community's inbox. When the community has <c>manuallyApprovesMembers</c>
    /// set, the server records a pending join request (the operator must Accept or Reject); otherwise the
    /// server auto-grants membership (19.5.2).
    /// </summary>
    /// <param name="actorId">The IRI of the actor requesting to join (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="communityIri">The IRI of the community to join.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> RequestJoinAsync(Iri actorId, Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Accepts a pending join request for a community as <paramref name="communityIri"/> (the community
    /// operator's decision): builds an <see cref="KristofferStrube.ActivityStreams.Accept"/> whose
    /// <c>object</c> references <paramref name="joinIri"/> (the original <see cref="KristofferStrube.ActivityStreams.Join"/>)
    /// and publishes it to the community's own outbox. The server adds the requesting actor as a member and
    /// removes the pending join request (19.5.2).
    /// </summary>
    /// <param name="communityIri">The IRI of the community (the operator deciding — must match the
    /// client's signing identity).</param>
    /// <param name="joinIri">The IRI of the pending <see cref="KristofferStrube.ActivityStreams.Join"/> being
    /// accepted.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> AcceptJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default);

    /// <summary>
    /// Rejects a pending join request for a community as <paramref name="communityIri"/> (the community
    /// operator's decision): builds a <see cref="KristofferStrube.ActivityStreams.Reject"/> whose
    /// <c>object</c> references <paramref name="joinIri"/> (the original <see cref="KristofferStrube.ActivityStreams.Join"/>)
    /// and publishes it to the community's own outbox. The server removes the pending join request without
    /// granting membership (19.5.2).
    /// </summary>
    /// <param name="communityIri">The IRI of the community (the operator deciding — must match the
    /// client's signing identity).</param>
    /// <param name="joinIri">The IRI of the pending <see cref="KristofferStrube.ActivityStreams.Join"/> being
    /// rejected.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> RejectJoinAsync(Iri communityIri, Iri joinIri, CancellationToken ct = default);

    /// <summary>
    /// Likes an object as <paramref name="actorId"/>: builds a <see cref="KristofferStrube.ActivityStreams.Like"/>
    /// (actor = <paramref name="actorId"/>, object = <paramref name="objectId"/>) and publishes it through
    /// the signed pipeline to the liker's own outbox (exactly like
    /// <see cref="PostNoteAsync(Iri, string, IEnumerable{Iri}, CancellationToken)"/> — a content object has no
    /// outbox of its own, only actors do). The server records the like edge (liker → object) and
    /// server-delivers it to the object's owner. This is the client's one-call "like" (the caller supplies only
    /// the liked object's IRI — the <see cref="KristofferStrube.ActivityStreams.Like"/> and the delivery target
    /// are derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor issuing the like (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="objectId">The IRI of the object being liked (a note, post, or other content object).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Like"/> is published to the liker's OWN outbox
    /// (<c>actorId.OutboxOf()</c>, the write surface for the activities an actor authors — a content object
    /// has no outbox of its own, only actors do) and is signed by the pipeline. The server records the like
    /// edge (liker → object) in the liker's <c>liked</c> collection and server-delivers the like to the
    /// object's owner. The <see cref="KristofferStrube.ActivityStreams.Like"/> gets a deterministic,
    /// unique-per-(actor,object) IRI so a retried like dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> LikeAsync(Iri actorId, Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Removes a like as <paramref name="actorId"/> (the inverse of <see cref="LikeAsync"/>): builds an
    /// <see cref="KristofferStrube.ActivityStreams.Undo"/> whose object references the original
    /// <see cref="KristofferStrube.ActivityStreams.Like"/> by its learned id and delivers it through the
    /// signed pipeline to the actor's own outbox. The receiving instance removes the like edge (liker →
    /// object) from the liker's <c>liked</c> collection. This is the client's one-call "unlike".
    /// </summary>
    /// <param name="actorId">The IRI of the actor removing the like (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="originalLikeId">The id the server minted for the original like — learned from
    /// <see cref="DeliveryResult.MintedId"/> when the like was made via <see cref="LikeAsync"/>.
    /// (Decision 055: the client references the like by its learned id, never a recomputed formula.)</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, the response
    /// body, and the server-minted id of the <c>Undo</c> (when present).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Undo"/> is delivered to the actor's OWN outbox
    /// (<c>actorId.OutboxOf()</c>) and is signed by the pipeline — the party that made the like undoes it.
    /// Its <c>object</c> references the original <see cref="KristofferStrube.ActivityStreams.Like"/> by its
    /// learned id, and the server mints the <c>Undo</c>'s own id (an unguessable ULID) and returns it in
    /// the 2xx body.
    /// </remarks>
    public Task<DeliveryResult> UnlikeAsync(Iri actorId, Iri originalLikeId, CancellationToken ct = default);

    /// <summary>
    /// Boosts (re-shares) an object as <paramref name="actorId"/>: builds an
    /// <see cref="KristofferStrube.ActivityStreams.Announce"/> (actor = <paramref name="actorId"/>, object =
    /// <paramref name="objectId"/>) and publishes it through the signed pipeline to the announcer's own outbox.
    /// The server records the Announce in the announcer's outbox (so the boost surfaces in the announcer's
    /// feed) and fans it out to the announcer's remote, non-blocked followers (mirroring the Create fan-out).
    /// This is the client's one-call "boost" / "repost" (the caller supplies only the boosted object's IRI —
    /// the <see cref="KristofferStrube.ActivityStreams.Announce"/> and the delivery target are derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor boosting the object (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="objectId">The IRI of the object being boosted (a note, post, or other content object).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Announce"/> is published to the announcer's OWN outbox
    /// (<c>actorId.OutboxOf()</c>) and is signed by the pipeline. Unlike a <see cref="Like"/>, an
    /// <see cref="Announce"/> carries no embedded object — it is a reference to an existing object IRI — so no
    /// object-store write is needed. The <see cref="Announce"/> gets a deterministic, unique-per-(actor,object)
    /// IRI (<c>{actorId}/announces/{objectId}</c>, matching the server's <c>AnnounceIris.AnnounceIri</c>) so a
    /// retried boost dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> AnnounceAsync(Iri actorId, Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Removes a boost as <paramref name="actorId"/> (the inverse of <see cref="AnnounceAsync"/>): builds an
    /// <see cref="KristofferStrube.ActivityStreams.Undo"/> whose object references the original
    /// <see cref="KristofferStrube.ActivityStreams.Announce"/> by its learned id and delivers it through the
    /// signed pipeline to the announcer's own outbox. This is the client's one-call "unboost" / "unrepost".
    /// </summary>
    /// <param name="actorId">The IRI of the actor removing the boost (must match the client's signing identity
    /// so the request is signed as that actor, and must be the actor who made the boost).</param>
    /// <param name="originalAnnounceId">The id the server minted for the original announce — learned from
    /// <see cref="DeliveryResult.MintedId"/> when the boost was made via <see cref="AnnounceAsync"/>.
    /// (Decision 055: the client references the announce by its learned id, never a recomputed formula.)</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, the response
    /// body, and the server-minted id of the <c>Undo</c> (when present).</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Undo"/> is delivered to the announcer's OWN outbox
    /// (<c>actorId.OutboxOf()</c>) and is signed by the pipeline — the party that made the boost undoes it. Its
    /// <c>object</c> references the original <see cref="KristofferStrube.ActivityStreams.Announce"/> by its learned
    /// id, and the server mints the <c>Undo</c>'s own id (an unguessable ULID) and returns it in the 2xx body.
    /// </remarks>
    public Task<DeliveryResult> UnannounceAsync(Iri actorId, Iri originalAnnounceId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a content object as <paramref name="actorId"/> (the inverse of a post): builds an
    /// <see cref="KristofferStrube.ActivityStreams.Delete"/> referencing the object by IRI and delivers it
    /// through the signed pipeline to the actor's own outbox. The receiving instance tombstones the object
    /// (replacing it with a <see cref="KristofferStrube.ActivityStreams.Tombstone"/> so its IRI still
    /// resolves to a "deleted" marker rather than a 404), removes any reply edge the object had, and
    /// propagates the tombstone to the author's remote followers. This is the client's one-call "delete
    /// note".
    /// </summary>
    /// <param name="actorId">The IRI of the actor deleting the object (must match the client's signing
    /// identity so the request is signed as that actor, and must be the object's author).</param>
    /// <param name="objectId">The IRI of the object to delete (a note or reply), referenced by the
    /// <see cref="KristofferStrube.ActivityStreams.Delete"/> as a bare link.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Delete"/> is delivered to the actor's OWN outbox
    /// (<c>actorId.OutboxOf()</c>) and is signed by the pipeline. It references the object being deleted
    /// by IRI, and the <see cref="KristofferStrube.ActivityStreams.Delete"/> itself gets a deterministic,
    /// unique-per-(actor,object) IRI (<c>{actorId}/deletes/{objectId-suffix}</c>) so a retried delete
    /// dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> DeleteAsync(Iri actorId, Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Blocks <paramref name="targetId"/> as <paramref name="actorId"/> (F-07 moderation): builds a
    /// <see cref="KristofferStrube.ActivityStreams.Block"/> activity (actor = <paramref name="actorId"/>,
    /// object = <paramref name="targetId"/>) and publishes it through the signed pipeline to
    /// <paramref name="actorId"/>'s own outbox so that <paramref name="actorId"/> blocks
    /// <paramref name="targetId"/>. This is the client's one-call "block" (the caller supplies only the
    /// target's IRI — the <see cref="KristofferStrube.ActivityStreams.Block"/> and the delivery target are
    /// derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor performing the block (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to block.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="KristofferStrube.ActivityStreams.Block"/> is published to <c>actorId.OutboxOf()</c>
    /// (the blocking actor's own outbox — the write surface for the activities an actor authors) and is
    /// signed by the pipeline. The server records the <c>actorId → targetId</c> block edge in the
    /// moderation store and server-delivers the block to the target's inbox (per ActivityPub §5.2.1.3). The
    /// <see cref="KristofferStrube.ActivityStreams.Block"/> gets a deterministic, unique-per-(actor,target)
    /// IRI so a retried block dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> BlockAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

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
    /// Un-blocks an actor (F-07 moderation): builds an <see cref="Undo"/> of the original
    /// <see cref="Block"/> and publishes it to <paramref name="actorId"/>'s own outbox (the inverse of
    /// <see cref="BlockAsync"/> — the party that made the block undoes it).
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-blocking (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="originalBlockId">The id the server minted for the original block — learned from
    /// <see cref="DeliveryResult.MintedId"/> when the block was made via <see cref="BlockAsync"/>.
    /// (Decision 055: the client references the block by its learned id, never a recomputed formula.)</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, the response
    /// body, and the server-minted id of the <c>Undo</c> (when present).</returns>
    /// <remarks>
    /// The <see cref="Undo"/> is published to <c>actorId.OutboxOf()</c> (the blocking actor's own outbox —
    /// the write surface for the activities an actor authors) and is signed by the pipeline. Its
    /// <c>object</c> references the original <see cref="Block"/> by its learned id, and the server mints
    /// the <see cref="Undo"/>'s own id (an unguessable ULID) and returns it in the 2xx body. The server
    /// then removes the local block edge and server-delivers the <c>Undo</c> to the previously-blocked
    /// actor's inbox.
    /// </remarks>
    public Task<DeliveryResult> UnblockAsync(Iri actorId, Iri originalBlockId, CancellationToken ct = default);

    /// <summary>
    /// Flags an actor (F-07 moderation): builds a <see cref="Flag"/> activity (actor =
    /// <paramref name="actorId"/>, object = <paramref name="targetId"/>) and publishes it to
    /// <paramref name="actorId"/>'s own outbox (a moderation report — the inverse is an
    /// <see cref="Undo"/> of the <see cref="Flag"/>).
    /// </summary>
    /// <param name="actorId">The IRI of the actor flagging (must match the client's signing identity so
    /// the request is signed as that actor).</param>
    /// <param name="targetId">The IRI of the actor to flag.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="Flag"/> is published to <c>actorId.OutboxOf()</c> (the flagging actor's own outbox —
    /// the write surface for the activities an actor authors) and is signed by the pipeline. The server
    /// records the <c>actorId → targetId</c> flag edge in its moderation store when either party is local
    /// (the flag is a report; it does not sever the relationship the way a <see cref="BlockAsync"/> block
    /// does) and server-delivers the flag to the target's inbox. The <see cref="Flag"/> gets a
    /// deterministic, unique-per-(actor,target) IRI so a retried flag dedupes on the receiver.
    /// </remarks>
    public Task<DeliveryResult> FlagAsync(Iri actorId, Iri targetId, CancellationToken ct = default);

    /// <summary>
    /// Un-flags an actor (F-07 moderation): the inverse of <see cref="FlagAsync"/> — builds an
    /// <see cref="Undo"/> activity referencing the original <see cref="Flag"/> (actor =
    /// <paramref name="actorId"/>, object = the <see cref="Flag"/>'s learned id) and publishes it to
    /// <paramref name="actorId"/>'s own outbox, removing the recorded flag edge.
    /// </summary>
    /// <param name="actorId">The IRI of the actor un-flagging (must match the client's signing identity
    /// so the request is signed as that actor).</param>
    /// <param name="originalFlagId">The id the server minted for the original flag — learned from
    /// <see cref="DeliveryResult.MintedId"/> when the flag was made via <see cref="FlagAsync"/>.
    /// (Decision 055: the client references the flag by its learned id, never a recomputed formula.)</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, the response
    /// body, and the server-minted id of the <c>Undo</c> (when present).</returns>
    /// <remarks>
    /// The <see cref="Undo"/> is published to <c>actorId.OutboxOf()</c> (the flagging actor's own outbox —
    /// the write surface for the activities an actor authors) and is signed by the pipeline. It references
    /// the original <see cref="Flag"/> by its learned id, so the server resolves the original flag's
    /// parties from the stored <see cref="Flag"/> and removes the exact recorded edge (a local flagger of
    /// anyone, or a flagger of a local actor), then server-delivers the <c>Undo</c> to the target's inbox.
    /// The server mints the <c>Undo</c>'s own id (an unguessable ULID) and returns it in the 2xx body.
    /// </remarks>
    public Task<DeliveryResult> UnflagAsync(Iri actorId, Iri originalFlagId, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="memberId"/> to <paramref name="communityId"/> (F-16 community membership):
    /// builds an <see cref="KristofferStrube.ActivityStreams.Add"/> activity (actor =
    /// <paramref name="communityId"/>, object = <paramref name="memberId"/>) and delivers it to the
    /// community's own inbox.
    /// </summary>
    /// <param name="communityId">The IRI of the community whose membership is changed (the community is
    /// the activity's <c>actor</c> — a community manages its own membership, see the remarks).</param>
    /// <param name="memberId">The IRI of the actor to add as a member.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// <strong>Self-management.</strong> A community's membership is an act of the community's own
    /// management surface (19.5.2): the <c>Add</c>'s <c>actor</c> is the recipient community, and the
    /// server's <c>AddActivityHandler</c> applies a gate — only an <c>Add</c> whose actor is the recipient
    /// community edits that community's member set. The client therefore sets <c>actor = communityId</c>
    /// (not a calling person), and the request must be signed as the community (the client's signing
    /// identity must be the community so the <c>actor</c> and the signature agree).
    /// </remarks>
    /// <remarks>
    /// <strong>Direct-inbox target (a deviation from the outbox convention).</strong> Unlike every other
    /// one-call method (which publishes to <c>actorId.OutboxOf()</c>), this delivers to
    /// <c>communityId.InboxOf()</c>: the community outbox publish endpoint accepts only
    /// <c>Follow</c>/<c>Undo</c>/<c>Accept</c>/<c>Reject</c>, so a membership <c>Add</c> is posted directly
    /// to the community's inbox (where <c>AddActivityHandler</c> runs). The <c>Add</c> gets a unique IRI
    /// (<c>{community}/add-&lt;guid&gt;</c>) — not a deterministic dedupe IRI — because a member can be
    /// added/removed repeatedly and each operation is a distinct stored activity.
    /// </remarks>
    public Task<DeliveryResult> AddMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="memberId"/> from <paramref name="communityId"/> (F-16 community
    /// membership): the inverse of <see cref="AddMemberAsync"/> — builds a
    /// <see cref="KristofferStrube.ActivityStreams.Remove"/> activity (actor =
    /// <paramref name="communityId"/>, object = <paramref name="memberId"/>) and delivers it to the
    /// community's own inbox.
    /// </summary>
    /// <param name="communityId">The IRI of the community whose membership is changed (the community is
    /// the activity's <c>actor</c> — a community manages its own membership, see the remarks).</param>
    /// <param name="memberId">The IRI of the actor to remove as a member.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// <strong>Self-management + direct-inbox target.</strong> Same model as <see cref="AddMemberAsync"/>:
    /// the <c>Remove</c>'s <c>actor</c> is the recipient community (the 19.5.2 gate — only the community
    /// edits its own member set), the request is signed as the community, and it is delivered to
    /// <c>communityId.InboxOf()</c> (the community outbox publish endpoint accepts only
    /// <c>Follow</c>/<c>Undo</c>/<c>Accept</c>/<c>Reject</c>). The <c>Remove</c> gets a unique IRI
    /// (<c>{community}/remove-&lt;guid&gt;</c>) so a repeated add/remove is a distinct stored activity.
    /// </remarks>
    public Task<DeliveryResult> RemoveMemberAsync(Iri communityId, Iri memberId, CancellationToken ct = default);

    /// <summary>
    /// Creates a community (a <see cref="KristofferStrube.ActivityStreams.Group"/>) owned by the
    /// instance of <paramref name="actorId"/> (19.5.1 community-creation write path): builds a
    /// <see cref="KristofferStrube.ActivityStreams.Create"/> whose embedded <c>Group</c> has the IRI
    /// <c>{instanceBase}/ap/v1/c/{name}</c> and publishes it to <paramref name="actorId"/>'s own outbox.
    /// The server materializes the community (stores it in the community store with a minted signing key),
    /// so the new community's document endpoint, <c>members</c>, <c>feed</c>, and collections resolve.
    /// </summary>
    /// <param name="actorId">The IRI of the local actor who authors the community (the activity's
    /// <c>actor</c>; the instance base for the new community's IRI is derived from it).</param>
    /// <param name="name">The community's handle (the final path segment of its IRI,
    /// <c>{base}/ap/v1/c/{name}</c>; also its <c>preferredUsername</c>).</param>
    /// <param name="displayName">The community's human-readable display name (its <c>name</c>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// <strong>Outbox-publish pattern (AP-native).</strong> Unlike the membership methods (which post
    /// directly to the community's inbox), community creation is a <c>Create</c> authored by a <em>person</em>
    /// to their <em>own outbox</em> — the chicken-and-egg of a community publishing to its own (not-yet
    /// existent) outbox is avoided by having the creator's outbox carry the <c>Create</c>. The server's
    /// outbox-publish handler, on seeing a <c>Create</c> whose embedded object is a local
    /// <c>Group</c>, materializes the community (19.5.1). The <c>Create</c> carries a deterministic IRI
    /// (<c>{actorId}/creates/community-{name}</c>) so a repeated create of the same community is a
    /// no-op re-store (idempotent by IRI).
    /// </remarks>
    public Task<DeliveryResult> CreateCommunityAsync(
        Iri actorId,
        string name,
        string displayName,
        CancellationToken ct = default);

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
    /// embedded <see cref="Note"/> with the given <paramref name="content"/> and publishes it through
    /// the signed pipeline to the actor's own outbox. This is the client's one-call "post a note" (the
    /// caller supplies only the content — the <see cref="Create"/>, the embedded <see cref="Note"/>,
    /// and the delivery target are all derived here).
    /// </summary>
    /// <param name="actorId">The IRI of the actor authoring the note (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="content">The note's content (plain text or HTML).</param>
    /// <param name="to">Optional audience link(s) for the note (e.g. the public
    /// <c>as:Public</c> address). When null the note carries no explicit <c>to</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// The <see cref="Create"/> is published to <c>actorId.OutboxOf()</c> (the author's own outbox) —
    /// the "local post" path: the post reaches the author's instance via the actor's own outbox, which
    /// records it and federates it to followers (the outbound-to-followers leg is the server's
    /// responsibility, not the client's). The <see cref="Create"/> and the embedded <see cref="Note"/>
    /// each get a deterministic, unique IRI so a retried post dedupes on the receiver. The note's
    /// <c>attributedTo</c> is the author.
    /// </remarks>
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, CancellationToken ct = default);

    /// <summary>
    /// Posts a fully-built note as <paramref name="actorId"/>: wraps the caller-supplied
    /// <paramref name="note"/> (which may carry attachments, e.g. an <see cref="KristofferStrube.ActivityStreams.Image"/>
    /// whose <c>url</c> is a same-origin media IRI from an <see cref="IMediaClient"/> upload, Phase 20.4 (a))
    /// in a <see cref="Create"/> and publishes it through the signed pipeline to the actor's own outbox.
    /// This is the overload for a note whose shape the caller has already assembled (content plus any
    /// attachments); the simpler string overload builds a bare note.
    /// </summary>
    /// <param name="actorId">The IRI of the actor authoring the note (must match the client's signing
    /// identity so the request is signed as that actor).</param>
    /// <param name="note">The note to post (its <c>attributedTo</c> should be the author; its
    /// <c>attachment</c> may carry media whose <c>url</c> is a same-origin media IRI).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    public Task<DeliveryResult> PostNoteAsync(Iri actorId, KristofferStrube.ActivityStreams.Note note, CancellationToken ct = default);

    /// <summary>
    /// Posts a **reply** as <paramref name="actorId"/> to the note at <paramref name="parentIri"/>:
    /// builds a <see cref="Create"/> carrying an embedded <see cref="Note"/> whose <c>inReplyTo</c> is
    /// the parent note and whose <c>tag</c> carries an <see cref="Mention"/> per <c>@mention</c> in
    /// <paramref name="mentions"/>, then publishes it through the signed pipeline to the author's own
    /// outbox (F-12). This is the client's one-call "reply to a note" (the caller supplies the parent IRI, the
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
    /// <returns>A <see cref="DeliveryResult"/> carrying the HTTP status code, a success flag, and the response body.</returns>
    /// <remarks>
    /// Mirrors <c>PostNoteAsync</c> but sets <c>inReplyTo</c> (the parent) and, when
    /// <paramref name="mentions"/> is non-empty, a <c>tag</c> of <see cref="Mention"/> entries. The
    /// receiving server's <c>Create</c> handler records the parent → child reply edge (via the note's
    /// <c>inReplyTo</c>), which is what surfaces the reply under the parent's replies collection. The
    /// <see cref="Create"/> is published to <c>actorId.OutboxOf()</c> (the author's own outbox).
    /// </remarks>
    public Task<DeliveryResult> PostReplyAsync(
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
    /// Reads the actors that like a content object (the per-object <c>likes</c> collection — decision
    /// 056 (d), the per-object like counter).
    /// </summary>
    /// <param name="objectIri">The IRI of the object (e.g. a <c>Note</c>) whose likers are read.</param>
    /// <param name="query">Optional paging / limit query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The likers (actor IRIs as links), in no particular order. The count of yielded items is the
    /// object's like count. Yields nothing when the object has no likers or the object is unknown
    /// (404).
    /// </returns>
    /// <remarks>
    /// The <c>likes</c> collection is served at <c>{object}/likes</c> as a full, non-paged
    /// <c>OrderedCollection</c> (a like/boost set is small and bounded, unlike an outbox), so it is
    /// enumerated exactly like any other collection. It is an extension collection under the bare,
    /// non-namespaced term the ecosystem uses, so this read works uniformly for local and external
    /// objects that expose it.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetLikesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the actors that announced (boosted) a content object (the per-object <c>shares</c>
    /// collection — decision 056 (d), the per-object boost counter).
    /// </summary>
    /// <param name="objectIri">The IRI of the object (e.g. a <c>Note</c>) whose announcers are read.</param>
    /// <param name="query">Optional paging / limit query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The announcers (actor IRIs as links), in no particular order. The count of yielded items is the
    /// object's boost count. Yields nothing when the object has no announcers or the object is unknown
    /// (404).
    /// </returns>
    /// <remarks>
    /// The <c>shares</c> collection is served at <c>{object}/shares</c> as a full, non-paged
    /// <c>OrderedCollection</c> (a like/boost set is small and bounded, unlike an outbox), so it is
    /// enumerated exactly like any other collection. It is an extension collection under the bare,
    /// non-namespaced term the ecosystem uses, so this read works uniformly for local and external
    /// objects that expose it.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetSharesAsync(
        Iri objectIri,
        CollectionQuery? query = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the activities delivered TO an actor (their inbox — what they received, as opposed to the
    /// outbox, what they authored). Decision 056: the inbox is a first-class, per-actor collection that is
    /// <em>private</em> — the server serves it only to the owner via Basic auth (the same seam that gates
    /// the owner-only <c>privateKey</c> extension) and with no-store caching.
    /// </summary>
    /// <param name="actorId">The IRI of the actor whose inbox is read.</param>
    /// <param name="credentials">The owner's Basic-auth credentials. The request is sent with an
    /// <c>Authorization: Basic</c> header; without valid owner credentials the server returns 403 and
    /// this method yields nothing.</param>
    /// <param name="query">Optional paging / limit query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The inbox entries (the delivered activities), newest first. Yields nothing when the actor has no
    /// inbox, the credentials are not the owner's (403), the actor is unknown (404), or the request fails.
    /// </returns>
    /// <remarks>
    /// The inbox page is read directly from the network (never through the <see cref="CollectionPageCache"/>)
    /// because it is private, owner-scoped data — the same no-store treatment the server applies to the
    /// owner-only actor document. The inbox is <c>{actor}/inbox</c>.
    /// </remarks>
    public IAsyncEnumerable<IObjectOrLink> GetInboxItemsAsync(
        Iri actorId,
        Pipeline.ProxyCredentials credentials,
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
