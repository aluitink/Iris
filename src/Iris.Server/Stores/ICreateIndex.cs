using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records the <c>Create</c> that produced each content object, so a later <c>Delete</c> can find the
/// object's originating <c>Create</c> to remove it from the author's outbox.
/// </summary>
/// <remarks>
/// When the server mints object ids (decision 055), an object's IRI (<c>{actor}/notes/{ulid}</c>) and
/// its <c>Create</c>'s IRI (<c>{actor}/creates/{ulid}</c>) carry two <em>independent</em> ULIDs — the
/// old "derive the Create IRI from the object IRI's last segment" trick no longer holds, because the
/// note's ULID and the Create's ULID are unrelated. This index records, at <c>Create</c> time, the
/// object IRI → Create IRI link so a <c>Delete</c> (and anything else that needs the object's Create)
/// can resolve it by lookup instead of by derivation.
/// </remarks>
public interface ICreateIndex
{
    /// <summary>
    /// Records that <paramref name="createIri"/> is the <c>Create</c> that produced <paramref name="objectIri"/>.
    /// </summary>
    /// <param name="objectIri">The IRI of the created object (the note).</param>
    /// <param name="createIri">The IRI of the <c>Create</c> activity that produced the object.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordAsync(Iri objectIri, Iri createIri, CancellationToken ct = default);

    /// <summary>
    /// Removes the recorded link for <paramref name="objectIri"/> (called when the object's <c>Create</c>
    /// is removed from the outbox, so the index does not accumulate stale links).
    /// </summary>
    /// <param name="objectIri">The IRI of the created object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a link was removed.</returns>
    public Task<bool> RemoveAsync(Iri objectIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRI of the <c>Create</c> that produced <paramref name="objectIri"/>, or null when the
    /// object has no recorded <c>Create</c> (it was not created through a <c>Create</c> this instance
    /// recorded, or its <c>Create</c> was already removed).
    /// </summary>
    /// <param name="objectIri">The IRI of the created object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the <c>Create</c> IRI, or null.</returns>
    public Task<Iri?> TryGetCreateIriAsync(Iri objectIri, CancellationToken ct = default);
}
