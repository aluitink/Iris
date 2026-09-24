using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IBookmarkStore"/> (S111, production persistence): bookmark edges
/// <c>bookmarker → object</c> persisted to a single JSON file that survives a restart.
/// </summary>
public sealed class FileBackedBookmarkStore : IBookmarkStore, IDisposable
{
    private readonly FilePersistence _file;

    public FileBackedBookmarkStore(string path)
        : this(new FilePersistence(path, EdgeListToDocument, EdgeListFromDocument))
    {
    }

    public FileBackedBookmarkStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    public Task RecordBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var edges = EdgeSet(s);
            edges.GetOrAdd(bookmarkerIri, _ => new HashSet<Iri>()).Add(objectIri);
            return 0;
        }, true, ct);

    public Task<bool> RemoveBookmarkAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s).TryGetValue(bookmarkerIri, out var set) && set.Remove(objectIri), true, ct);

    public Task<IReadOnlyList<Iri>> GetBookmarksAsync(Iri bookmarkerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            var result = new List<Iri>();
            if (EdgeSet(s).TryGetValue(bookmarkerIri, out var set))
            {
                result = set.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
            }
            return result;
        }, ct);

    public Task<bool> IsBookmarkedAsync(Iri bookmarkerIri, Iri objectIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s).TryGetValue(bookmarkerIri, out var set) && set.Contains(objectIri), ct);

    private static ConcurrentDictionary<Iri, HashSet<Iri>> EdgeSet(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, HashSet<Iri>>)(state.TryGetValue("edges", out var e) ? e! : state["edges"] = new ConcurrentDictionary<Iri, HashSet<Iri>>());

    private static JsonDocument EdgeListToDocument(ConcurrentDictionary<string, object> state)
    {
        var edges = state.TryGetValue("edges", out var e)
            ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e!
            : new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var list = edges.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();
        return JsonSerializer.SerializeToDocument(list, FilePersistence.JsonOptions);
    }

    private static void EdgeListFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var edges = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
                if (edge is not null)
                {
                    edges.GetOrAdd(edge.Source, _ => new HashSet<Iri>()).Add(edge.Target);
                }
            }
        }
        state["edges"] = edges;
    }

    public void Dispose() => _file.Dispose();
}
