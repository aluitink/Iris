using System.Collections.Concurrent;
using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// A minimal in-memory <see cref="IDislikeStore"/> for deployments that don't need durable dislike
/// persistence (e.g. the file-backed provider, where dislikes are ephemeral).
/// </summary>
public sealed class SimpleDislikeStore : IDislikeStore
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _outEdges = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _inEdges = new();

    public Task RecordDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        _outEdges.GetOrAdd(dislikerIri.Value, _ => new HashSet<string>(StringComparer.Ordinal)).Add(objectIri.Value);
        _inEdges.GetOrAdd(objectIri.Value, _ => new HashSet<string>(StringComparer.Ordinal)).Add(dislikerIri.Value);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveDislikeAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        if (_outEdges.TryGetValue(dislikerIri.Value, out var set) && set.Remove(objectIri.Value))
        {
            if (_inEdges.TryGetValue(objectIri.Value, out var inSet))
                inSet.Remove(dislikerIri.Value);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> HasDislikedAsync(Iri dislikerIri, Iri objectIri, CancellationToken ct = default)
    {
        var result = _outEdges.TryGetValue(dislikerIri.Value, out var set) && set.Contains(objectIri.Value);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Iri>> GetDislikersAsync(Iri objectIri, CancellationToken ct = default)
    {
        var result = _inEdges.TryGetValue(objectIri.Value, out var set)
            ? set.Select(v => new Iri(v)).ToList()
            : new List<Iri>();
        return Task.FromResult<IReadOnlyList<Iri>>(result);
    }
}
