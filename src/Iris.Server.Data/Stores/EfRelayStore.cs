using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IRelayStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Relay"/>). A relay subscription edge is <c>subscribing actor → relay</c>.
/// </summary>
public sealed class EfRelayStore : IRelayStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfRelayStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Relay, actorIri.Value, relayIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Relay, actorIri.Value, relayIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetRelaysAsync(Iri actorIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Relay, actorIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> IsRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Relay, actorIri.Value, relayIri.Value, ct);
}
