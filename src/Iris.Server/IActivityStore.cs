using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

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
    /// Returns the outbox activities for an actor, newest first, as an <see cref="OrderedCollectionPage"/>-
    /// ready sequence of <see cref="IObjectOrLink"/> (the wire items of the outbox collection).
    /// </summary>
    /// <param name="actorIri">The IRI identifying the actor whose outbox is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the outbox items (possibly empty).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default);
}
