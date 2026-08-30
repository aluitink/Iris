using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="ILikeStore"/> (Phase 16.4, production persistence): like edges
/// <c>liker → likedObject</c> persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds a single edge list (the <see cref="FilePersistence.IriEdge"/>s, source = liker,
/// target = liked object). Thread-safe (the underlying <see cref="FilePersistence"/> serializes
/// reads/writes).
/// </remarks>
public sealed class FileBackedLikeStore : ILikeStore, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed like store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedLikeStore(string path)
        : this(new FilePersistence(path, EdgeListToDocument, EdgeListFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedLikeStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var edges = EdgeSet(s);
            edges.GetOrAdd(likerIri, _ => new HashSet<Iri>()).Add(likedObjectIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s).TryGetValue(likerIri, out var set) && set.Remove(likedObjectIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => EdgeSet(s).TryGetValue(likerIri, out var set) ? set.ToList() : new List<Iri>(), ct);

    /// <inheritdoc/>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s).TryGetValue(likerIri, out var set) && set.Contains(likedObjectIri), ct);

    /// <summary>
    /// The edge index for the current state (liker → set of liked objects), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> EdgeSet(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, HashSet<Iri>>)(state.TryGetValue("edges", out var e) ? e! : state["edges"] = new ConcurrentDictionary<Iri, HashSet<Iri>>());

    /// <summary>
    /// Serializes the edge index to a JSON document (an array of <see cref="FilePersistence.IriEdge"/>).
    /// </summary>
    private static JsonDocument EdgeListToDocument(ConcurrentDictionary<string, object> state)
    {
        var edges = state.TryGetValue("edges", out var e)
            ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e!
            : new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var list = edges.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();
        return JsonSerializer.SerializeToDocument(list, FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the edge index from the file's root element (an array of edges).
    /// </summary>
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

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}