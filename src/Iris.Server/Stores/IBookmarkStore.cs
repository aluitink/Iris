using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// Records and queries bookmark edges: the directed <c>bookmarker → bookmarked object</c> relationship
/// that lets a local actor save posts for later.
/// </summary>
public interface IBookmarkStore
{
    /// <summary>
    /// Records a bookmark edge. Idempotent.
    /// </summary>
    Task RecordBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a bookmark edge.
    /// </summary>
    Task<bool> RemoveBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of objects that <paramref name="bookmarkerIri"/> has bookmarked.
    /// </summary>
    Task<IReadOnlyList<Iri>> GetBookmarksAsync(Iri bookmarkerIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="bookmarkerIri"/> has bookmarked <paramref name="objectIri"/>.
    /// </summary>
    Task<bool> IsBookmarkedAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default);
}
