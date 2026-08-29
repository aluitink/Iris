using Iris.Core;
using KristofferStrube.ActivityStreams;
using CollectionPage = Iris.Core.CollectionPage;

namespace Iris.Client;

/// <summary>
/// The primary ActivityPub client surface. Performs signed HTTP requests against remote
/// ActivityPub servers and operates on <c>KristofferStrube.ActivityStreams</c> types.
/// </summary>
/// <remarks>
/// Requests are signed by the client's <see cref="SigningHandler"/> (wired into the
/// <see cref="HttpMessageHandler"/> pipeline) using the <see cref="Iris.Core.SigningProfile.ClientToServer"/>
/// profile for bodyless GETs and the <see cref="Iris.Core.SigningProfile.ServerToServer"/> profile for
/// body-carrying POSTs. Responses are deserialized into <see cref="IObjectOrLink"/> and then
/// pattern-matched — never into a concrete type. See <see cref="ActivityPubClient"/> for the
/// default implementation and <see cref="IActivityPubClientFactory"/> for construction.
/// Implementations own their HTTP pipeline and must be disposed when no longer needed.
/// </remarks>
public interface IActivityPubClient : IDisposable
{
    /// <summary>
    /// Fetches an object (actor or otherwise) by IRI, signed with the
    /// <see cref="Iris.Core.SigningProfile.ClientToServer"/> profile.
    /// </summary>
    /// <param name="objectId">The IRI of the object to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized object, or null if the request failed or the body was empty.</returns>
    public Task<IObject?> GetObjectAsync(Iri objectId, CancellationToken ct = default);

    /// <summary>
    /// Fetches an actor by IRI, signed with the <see cref="Iris.Core.SigningProfile.ClientToServer"/>
    /// profile.
    /// </summary>
    /// <param name="actorId">The IRI of the actor to fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The deserialized actor, or null if the request failed, the body was empty, or the
    /// fetched object is not an <see cref="Actor"/>.</returns>
    public Task<Actor?> GetActorAsync(Iri actorId, CancellationToken ct = default);

    /// <summary>
    /// Sends an ActivityPub activity to the given inbox IRI, signed with the
    /// <see cref="Iris.Core.SigningProfile.ServerToServer"/> profile (covers <c>digest</c> +
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
