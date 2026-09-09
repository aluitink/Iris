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

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> GetLikersBatchAsync(
        IReadOnlyCollection<Iri> likedObjectIris, CancellationToken ct = default)
    {
        var targets = likedObjectIris.Select(i => i.Value).ToList();
        return await _edges.InSourcesBatchAsync(EdgeKind.Like, targets, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Iri>> HasLikedBatchAsync(
        Iri likerIri, IReadOnlyCollection<Iri> objectIris, CancellationToken ct = default)
    {
        if (objectIris.Count == 0)
        {
            return new HashSet<Iri>();
        }

        var pairs = objectIris.Select(o => (likerIri.Value, o.Value)).ToList();
        var found = await _edges.ContainsBatchAsync(EdgeKind.Like, pairs, ct).ConfigureAwait(false);
        return found
            .Select(s => s.Split('\0')[1])
            .Select(v => new Iri(v))
            .ToHashSet();
    }
}
