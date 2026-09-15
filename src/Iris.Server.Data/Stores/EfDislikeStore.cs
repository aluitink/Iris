using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IDislikeStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Dislike"/>).
/// </summary>
public sealed class EfDislikeStore : IDislikeStore
{
    private readonly EdgeStore _edges;

    public EfDislikeStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    public Task RecordDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Dislike, dislikerIri.Value, objectIri.Value, ct);

    public Task<bool> RemoveDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Dislike, dislikerIri.Value, objectIri.Value, ct);

    public Task<bool> HasDislikedAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Dislike, dislikerIri.Value, objectIri.Value, ct);

    public Task<IReadOnlyList<Iri>> GetDislikersAsync(Iri objectIri, CancellationToken ct = default)
        => _edges.InSourcesAsync(EdgeKind.Dislike, objectIri.Value, ct);
}
