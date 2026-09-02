using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records and queries announce (boost / re-share) relationships.
/// </summary>
/// <remarks>
/// An announce (boost) is the directed edge <c>announcer → announcedObject</c>. The store tracks both
/// directions:
/// <list type="bullet">
/// <item>The <c>announcer → [announced objects]</c> direction, so the actor's boosts can be listed
/// (mirroring the actor's <c>liked</c> collection for likes).</item>
/// <item>The <c>announced object → [announcers]</c> reverse index, so an object's <c>shares</c>
/// collection (and thus its boost count) can be assembled without scanning every stored activity —
/// the per-object interaction counter deferred by decision 056 (d).</item>
/// </list>
/// Both directions are maintained atomically on <see cref="RecordAnnounceAsync"/> /
/// <see cref="RemoveAnnounceAsync"/>. The store is the durable record of a boost: the
/// <see cref="AnnounceActivityHandler"/> records an inbound boost here (and in the recipient's outbox),
/// and the <see cref="UndoActivityHandler"/> removes it on an <c>Undo</c> of the boost.
/// </remarks>
public interface IAnnounceStore
{
    /// <summary>
    /// Records an announce (boost) from <paramref name="announcerIri"/> to
    /// <paramref name="announcedObjectIri"/>.
    /// </summary>
    /// <param name="announcerIri">The IRI of the actor who issued the announce.</param>
    /// <param name="announcedObjectIri">The IRI of the object being announced (boosted).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default);

    /// <summary>
    /// Removes an announce edge.
    /// </summary>
    /// <param name="announcerIri">The IRI of the actor who issued the announce.</param>
    /// <param name="announcedObjectIri">The IRI of the object being announced (boosted).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when an announce edge was removed.</returns>
    public Task<bool> RemoveAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of objects that <paramref name="announcerIri"/> has announced (boosted).
    /// </summary>
    /// <param name="announcerIri">The IRI of the actor whose boosts are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the announced-object IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetAnnouncedAsync(Iri announcerIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="announcerIri"/> has announced (boosted)
    /// <paramref name="announcedObjectIri"/>.
    /// </summary>
    /// <param name="announcerIri">The IRI of the potential announcer.</param>
    /// <param name="announcedObjectIri">The IRI of the potential object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the announce edge exists.</returns>
    public Task<bool> HasAnnouncedAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors that have announced (boosted)
    /// <paramref name="announcedObjectIri"/> (the <c>shares</c> reverse index, the per-object
    /// boost counter — decision 056 (d)).
    /// </summary>
    /// <param name="announcedObjectIri">The IRI of the object whose announcers are requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the announcer IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetAnnouncersAsync(Iri announcedObjectIri, CancellationToken ct = default);
}
