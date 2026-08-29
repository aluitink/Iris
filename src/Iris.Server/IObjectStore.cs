using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Stores and retrieves generic ActivityStreams objects (notes, articles, media) by IRI.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IActivityStore"/>: this is for non-activity content objects that are
/// addressed by IRI and served over the wire. The inbox pipeline (Phase 4) uses
/// <see cref="IActivityStore"/> for activities; this store is for content objects referenced in
/// <c>object</c> links.
/// </remarks>
public interface IObjectStore
{
    /// <summary>
    /// Attempts to retrieve the object for the given IRI.
    /// </summary>
    /// <param name="objectIri">The IRI identifying the object.</param>
    /// <param name="obj">When successful, the object; otherwise null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if the object was found; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryGetObjectAsync(Iri objectIri, out IObject? obj, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) the object under its IRI. The object's <c>Id</c> must already be set.
    /// </summary>
    /// <param name="obj">The object to store. Must not be null and must have a non-null <c>Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="obj"/> is null.</exception>
    /// <exception cref="ArgumentException">When the object has no <c>Id</c>.</exception>
    public Task PutObjectAsync(IObject obj, CancellationToken ct = default);

    /// <summary>
    /// Removes the object stored under the given IRI (a hard delete).
    /// </summary>
    /// <remarks>
    /// The preferred way to represent a deleted object is to <em>replace</em> it with a
    /// <see cref="Tombstone"/> via <see cref="PutObjectAsync"/> (so the IRI still resolves and serves the
    /// AS2.0 "deleted" marker, F-10); this method is for the rare case where the stored object should be
    /// removed outright.
    /// </remarks>
    /// <param name="objectIri">The IRI of the object to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if an object was removed; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryDeleteObjectAsync(Iri objectIri, CancellationToken ct = default);

    /// <summary>
    /// Lists every object this instance stores (content objects such as <c>Note</c>s, and
    /// <see cref="Tombstone"/>s for deleted objects).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the stored objects (possibly empty). The order is
    /// unspecified; callers that need a stable order sort the result (e.g. by IRI). Callers that search
    /// content should skip <see cref="Tombstone"/>s (a deleted object has no searchable content).</returns>
    public Task<IReadOnlyList<IObject>> ListObjectsAsync(CancellationToken ct = default);
}
