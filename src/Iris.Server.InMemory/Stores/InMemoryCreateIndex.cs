using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory.Stores;

/// <summary>
/// An in-memory <see cref="ICreateIndex"/> backed by a single concurrent dictionary
/// (object IRI → Create IRI).
/// </summary>
/// <remarks>
/// Ephemeral: links vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryCreateIndex : ICreateIndex
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, Iri> _creates = new();

    /// <inheritdoc/>
    public Task RecordAsync(Iri objectIri, Iri createIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _creates[objectIri] = createIri;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_creates.TryRemove(objectIri, out _));
    }

    /// <inheritdoc/>
    public Task<Iri?> TryGetCreateIriAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Iri?>(_creates.TryGetValue(objectIri, out var createIri) ? createIri : null);
    }
}
