using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IFollowStore"/> (Phase 16.4, production persistence): follow edges
/// <c>follower → target</c> persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds a single edge list (the <see cref="FilePersistence.IriEdge"/>s). Both query
/// directions (followers of X, following of X) are derived by scanning the edge list, mirroring the
/// in-memory store's two-index semantics. Thread-safe (the underlying <see cref="FilePersistence"/>
/// serializes reads/writes).
/// </remarks>
public sealed class FileBackedFollowStore : IFollowStore, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed follow store over <paramref name="path"/> (creating the file on
    /// the first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedFollowStore(string path)
        : this(new FilePersistence(path, EdgeListToDocument, EdgeListFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedFollowStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var edges = EdgeSet(s);
            // The in-memory store tracks both directions: target → followers and follower → following.
            // Here the single index is follower → set of targets; the inverse (followers of X) is
            // derived by scanning, so only one index needs to be written.
            edges.GetOrAdd(followerIri, _ => new HashSet<Iri>()).Add(targetIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s).TryGetValue(followerIri, out var set) && set.Remove(targetIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowersAsync(Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            var edges = EdgeSet(s);
            return edges.Where(kv => kv.Value.Contains(actorIri)).Select(kv => kv.Key).ToList();
        }, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowingAsync(Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            var edges = EdgeSet(s);
            return edges.TryGetValue(actorIri, out var set) ? set.ToList() : new List<Iri>();
        }, ct);

    /// <inheritdoc/>
    public Task<bool> IsFollowingAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s).TryGetValue(followerIri, out var set) && set.Contains(targetIri), ct);

    /// <summary>
    /// The edge index for the current state (follower → set of targets), created on demand.
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