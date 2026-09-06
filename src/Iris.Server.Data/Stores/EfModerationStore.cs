using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IModerationStore"/> over the shared <c>Edges</c> table (kinds
/// <see cref="EdgeKind.Block"/>, <see cref="EdgeKind.Flag"/>, <see cref="EdgeKind.Mute"/>).
/// </summary>
public sealed class EfModerationStore : IModerationStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfModerationStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Block, blockerIri.Value, blockedIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Block, blockerIri.Value, blockedIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlocksAsync(Iri blockerIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Block, blockerIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> IsBlockedAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Block, blockerIri.Value, blockedIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlockersAsync(Iri blockedIri, CancellationToken ct = default)
        => _edges.InSourcesAsync(EdgeKind.Block, blockedIri.Value, ct);

    /// <inheritdoc/>
    public Task RecordFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Flag, flaggerIri.Value, flaggedIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Flag, flaggerIri.Value, flaggedIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFlagsAsync(Iri flaggerIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Flag, flaggerIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> HasFlaggedAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Flag, flaggerIri.Value, flaggedIri.Value, ct);

    /// <inheritdoc/>
    public Task RecordMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Mute, muterIri.Value, mutedIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Mute, muterIri.Value, mutedIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetMutesAsync(Iri muterIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Mute, muterIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> IsMutedAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Mute, muterIri.Value, mutedIri.Value, ct);
}
