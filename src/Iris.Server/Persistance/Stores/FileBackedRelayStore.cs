using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IRelayStore"/> (F-06, Phase 16.4, production persistence): relay
/// subscriptions <c>actor → relay</c> persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds a single edge list (the <see cref="FilePersistence.IriEdge"/>s, source = actor,
/// target = relay). Thread-safe (the underlying <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedRelayStore : IRelayStore, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed relay store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedRelayStore(string path)
        : this(new FilePersistence(path, EdgeListToDocument, EdgeListFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedRelayStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var edges = EdgeSet(s);
            edges.GetOrAdd(actorIri, _ => new HashSet<Iri>()).Add(relayIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s).TryGetValue(actorIri, out var set) && set.Remove(relayIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetRelaysAsync(Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            var result = new List<Iri>();
            if (EdgeSet(s).TryGetValue(actorIri, out var set))
            {
                // IRI-sorted for a deterministic collection order (mirrors the in-memory store).
                result = set.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
            }

            return result;
        }, ct);

    /// <inheritdoc/>
    public Task<bool> IsRelayAsync(Iri actorIri, Iri relayIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s).TryGetValue(actorIri, out var set) && set.Contains(relayIri), ct);

    /// <summary>
    /// The edge index for the current state (actor → set of relays), created on demand.
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
