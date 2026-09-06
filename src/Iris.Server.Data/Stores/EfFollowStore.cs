using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IFollowStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Follow"/>).
/// </summary>
public sealed class EfFollowStore : IFollowStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfFollowStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Follow, followerIri.Value, targetIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Follow, followerIri.Value, targetIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowersAsync(Iri actorIri, CancellationToken ct = default)
        => _edges.InSourcesAsync(EdgeKind.Follow, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowingAsync(Iri actorIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Follow, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> IsFollowingAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Follow, followerIri.Value, targetIri.Value, ct);
}
