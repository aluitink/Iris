using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Stores;

/// <summary>
/// Reads and writes activity objects (notes, follows, etc.) by their IRI.
/// </summary>
/// <remarks>
/// Activities are stored as <see cref="IObject"/> (the library's polymorphic object interface) so
/// any activity type can round-trip. The store does not interpret activity semantics; that is the
/// job of the inbox pipeline (Phase 4).
/// </remarks>
public interface IActivityStore
{
    /// <summary>
    /// Attempts to retrieve the activity for the given IRI.
    /// </summary>
    /// <param name="activityIri">The IRI identifying the activity.</param>
    /// <param name="activity">When successful, the activity; otherwise null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if the activity was found; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryGetActivityAsync(Iri activityIri, out IObject? activity, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) the activity under its IRI. The activity's <c>Id</c> must already be set.
    /// </summary>
    /// <param name="activity">The activity to store. Must not be null and must have a non-null <c>Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    /// <exception cref="ArgumentException">When the activity has no <c>Id</c>.</exception>
    public Task PutActivityAsync(IObject activity, CancellationToken ct = default);

    /// <summary>
    /// Adds the activity under its IRI <em>only if</em> no activity with that IRI is stored yet. Unlike
    /// <see cref="PutActivityAsync"/>, this never replaces an existing entry.
    /// </summary>
    /// <param name="activity">The activity to add. Must not be null and must have a non-null <c>Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if the activity was added; otherwise
    /// <see langword="false"/> (an activity with the same IRI was already stored — the add was a no-op).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="activity"/> is null.</exception>
    /// <exception cref="ArgumentException">When the activity has no <c>Id</c>.</exception>
    /// <remarks>
    /// The inbox pipeline uses this for idempotent, at-least-once delivery (C-07): a re-delivered activity
    /// (a retry, a restart replay, or — for mutual follows — a peer re-fan-out) is detected by the
    /// <see langword="false"/> result so it is not re-dispatched to handlers. Re-dispatching a received
    /// <c>Create</c> is what re-federates it back to the origin, so the check is the loop-safety guard for
    /// the two-instance network (19.3.1/19.3.2).
    /// </remarks>
    public Task<bool> TryAddActivityAsync(IObject activity, CancellationToken ct = default);

    /// <summary>
    /// Returns the outbox activities for an actor, newest first, as an <see cref="OrderedCollectionPage"/>-
    /// ready sequence of <see cref="IObjectOrLink"/> (the wire items of the outbox collection).
    /// </summary>
    /// <param name="actorIri">The IRI identifying the actor whose outbox is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the outbox items (possibly empty).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Adds an activity to an actor's outbox (newest first).
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose outbox is updated.</param>
    /// <param name="item">The activity to add. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been added to the outbox.</returns>
    /// <remarks>
    /// The outbox is the actor's posted/announced activities (newest first), served by the outbox
    /// collection endpoint. Activity handlers that record a local actor's activity in the outbox
    /// (e.g. <see cref="AnnounceActivityHandler"/> recording a boost) call this. The store does not
    /// de-duplicate by the item's IRI; callers that re-record the same activity should ensure
    /// idempotency.
    /// </remarks>
    public Task AddToOutboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default);

    /// <summary>
    /// Removes the outbox item with the given IRI from an actor's outbox, if present.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose outbox is updated.</param>
    /// <param name="itemIri">The IRI of the outbox item to remove (matched against the item's <c>Id</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if an item was removed; otherwise <see langword="false"/> (the item was not in the outbox).</returns>
    /// <remarks>
    /// The inverse of <see cref="AddToOutboxAsync"/>: a <c>Delete</c> (or <c>Undo(Create)</c>) removes the
    /// deleted object's <see cref="Create"/> from the author's outbox so the outbox collection no longer
    /// lists the deleted content. Removing a missing item is a no-op (returns <see langword="false"/>).
    /// </remarks>
    public Task<bool> RemoveFromOutboxAsync(Iri actorIri, Iri itemIri, CancellationToken ct = default);

    /// <summary>
    /// Enumerates every activity stored in the activity store (regardless of author or type).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with all stored activities (possibly empty).</returns>
    /// <remarks>
    /// A read-only enumeration of the store's full contents. Decision 055 mints unguessable object ids
    /// (ULIDs), so a received activity's id can no longer be recomputed from its originator + a formula;
    /// this enumeration lets a consumer (e.g. an inbound <c>Accept</c>/<c>Reject</c> lookup by its
    /// <c>object</c> reference) find a stored activity when its minted id is not known in advance.
    /// </remarks>
    public Task<IReadOnlyList<IObject>> GetAllActivitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the activities delivered to an actor's inbox (what was received, as opposed to the outbox,
    /// which is what the actor authored), newest first, as an <see cref="OrderedCollectionPage"/>-ready
    /// sequence of <see cref="IObjectOrLink"/>.
    /// </summary>
    /// <param name="actorIri">The IRI identifying the actor whose inbox is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the inbox items (possibly empty).</returns>
    /// <remarks>
    /// Decision 056: the inbox is a first-class, per-actor collection distinct from the outbox. It holds
    /// the activities <em>delivered to</em> the actor (inbound <c>Create</c>/<c>Follow</c>/<c>Like</c>/
    /// <c>Announce</c>/…). It is recorded on first delivery by the inbox pipeline and is the read surface
    /// for an owner-only <c>GET /{actor}/inbox</c>.
    /// </remarks>
    public Task<IReadOnlyList<IObjectOrLink>> GetInboxAsync(Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Adds an activity to an actor's inbox (newest first).
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose inbox is updated.</param>
    /// <param name="item">The activity to add. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the activity has been added to the inbox.</returns>
    /// <remarks>
    /// Decision 056: the inbox is the actor's <em>received</em> activities (as opposed to the outbox, the
    /// <em>authored</em> ones). Recorded by the inbox pipeline on first delivery. Like the outbox, the
    /// store de-duplicates by the item's IRI, so a re-delivered (at-least-once) activity is not duplicated.
    /// </remarks>
    public Task AddToInboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default);
}
