using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IAnnounceStore"/> over the shared <c>Edges</c> table (kind
/// <see cref="EdgeKind.Announce"/>).
/// </summary>
public sealed class EfAnnounceStore : IAnnounceStore
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the store over a shared edge store.
    /// </summary>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    public EfAnnounceStore(EdgeStore edges)
        => _edges = edges ?? throw new ArgumentNullException(nameof(edges));

    /// <inheritdoc/>
    public Task RecordAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _edges.AddAsync(EdgeKind.Announce, announcerIri.Value, announcedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _edges.RemoveAsync(EdgeKind.Announce, announcerIri.Value, announcedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncedAsync(Iri announcerIri, CancellationToken ct = default)
        => _edges.OutTargetsAsync(EdgeKind.Announce, announcerIri.Value, ct);

    /// <inheritdoc/>
    public Task<bool> HasAnnouncedAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _edges.ContainsAsync(EdgeKind.Announce, announcerIri.Value, announcedObjectIri.Value, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncersAsync(Iri announcedObjectIri, CancellationToken ct = default)
        => _edges.InSourcesAsync(EdgeKind.Announce, announcedObjectIri.Value, ct);
}
