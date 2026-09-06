using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="ILikeStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Like"/>).
/// </summary>
public sealed class EfLikeStore : ILikeStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfLikeStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Like, likerIri.Value, likedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Like, likerIri.Value, likedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Like, likerIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Like, likerIri.Value, likedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikersAsync(Iri likedObjectIri, CancellationToken ct = default)
        => _edges.InSourcesAsync(EdgeKind.Like, likedObjectIri.Value, ct);
}
