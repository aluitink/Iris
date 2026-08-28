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
}
