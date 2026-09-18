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

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> GetAnnouncersBatchAsync(
        IReadOnlyCollection<Iri> announcedObjectIris, CancellationToken ct = default)
    {
        var targets = announcedObjectIris.Select(i => i.Value).ToList();
        return await _edges.InSourcesBatchAsync(EdgeKind.Announce, targets, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Iri>> HasAnnouncedBatchAsync(
        Iri announcerIri, IReadOnlyCollection<Iri> objectIris, CancellationToken ct = default)
    {
        if (objectIris.Count == 0)
        {
            return new HashSet<Iri>();
        }

        var pairs = objectIris.Select(o => (announcerIri.Value, o.Value)).ToList();
        var found = await _edges.ContainsBatchAsync(EdgeKind.Announce, pairs, ct).ConfigureAwait(false);
        return found
            .Select(s => s.Split('\0')[1])
            .Select(v => new Iri(v))
            .ToHashSet();
    }
}
