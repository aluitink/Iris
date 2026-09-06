using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IReplyStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Reply"/>). A reply edge is stored <c>parent → child</c>: <c>parent</c> is the
/// source (so "replies to this note" is the forward direction) and <c>child</c> is the target.
/// </summary>
public sealed class EfReplyStore : IReplyStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfReplyStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Reply, parentIri.Value, childIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Reply, parentIri.Value, childIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetRepliesAsync(Iri parentIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Reply, parentIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> HasReplyAsync(Iri parentIri, Iri childIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Reply, parentIri.Value, childIri.Value, ct);
}
