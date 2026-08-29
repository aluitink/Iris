using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="IObjectStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// Ephemeral: objects vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, IObject> _objects = new();

    /// <inheritdoc/>
    public Task<bool> TryGetObjectAsync(Iri objectIri, out IObject? obj, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _objects.TryGetValue(objectIri, out obj);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutObjectAsync(IObject obj, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.IsNullOrWhiteSpace(obj.Id))
        {
            throw new ArgumentException("Object must have a non-null Id.", nameof(obj));
        }

        ct.ThrowIfCancellationRequested();
        _objects[new Iri(obj.Id)] = obj;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> TryDeleteObjectAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_objects.TryRemove(objectIri, out _));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IObject>>(_objects.Values.ToList());
    }
}
