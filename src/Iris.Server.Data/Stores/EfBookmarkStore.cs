using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IBookmarkStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Bookmark"/>). A bookmark edge is <c>bookmarker → bookmarked object</c>.
/// </summary>
public sealed class EfBookmarkStore : IBookmarkStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfBookmarkStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Bookmark, bookmarkerIri.Value, objectIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Bookmark, bookmarkerIri.Value, objectIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBookmarksAsync(Iri bookmarkerIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Bookmark, bookmarkerIri.Value, ct, filterDeletedActors: false);

    /// <inheritdoc/>
    public Task<bool> IsBookmarkedAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Bookmark, bookmarkerIri.Value, objectIri.Value, ct);
}
