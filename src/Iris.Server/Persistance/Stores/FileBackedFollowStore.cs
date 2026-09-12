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

    /// <inheritdoc/>
    public Task RecordFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var requests = RequestSet(s);
            requests.GetOrAdd(targetIri, _ => new List<Iri>());
            lock (requests[targetIri])
            {
                if (!requests[targetIri].Contains(followerIri))
                {
                    requests[targetIri].Add(followerIri);
                }
            }

            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => RequestSet(s).TryGetValue(targetIri, out var list) && RemoveLocked(list, followerIri), true, ct);

    /// <inheritdoc/>
    public Task<bool> HasFollowRequestAsync(Iri followerIri, Iri targetIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => RequestSet(s).TryGetValue(targetIri, out var list) && ContainsLocked(list, followerIri), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFollowRequestsAsync(Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            if (!RequestSet(s).TryGetValue(actorIri, out var list))
            {
                return new List<Iri>();
            }

            // Serve newest-first: the list is oldest→newest, so reverse the snapshot.
            lock (list)
            {
                var snapshot = list.ToList();
                snapshot.Reverse();
                return snapshot;
            }
        }, ct);

    private static bool RemoveLocked(List<Iri> list, Iri iri)
    {
        lock (list)
        {
            return list.Remove(iri);
        }
    }

    private static bool ContainsLocked(List<Iri> list, Iri iri)
    {
        lock (list)
        {
            return list.Contains(iri);
        }
    }

    /// <summary>
    /// The pending follow-request index for the current state (target actor → ordered list of requester
    /// IRIs, oldest→newest), created on demand. Kept separate from <see cref="EdgeSet"/> so a pending
    /// request (follower → target) is not conflated with a recorded follow edge of the same shape.
    /// </summary>
    private static ConcurrentDictionary<Iri, List<Iri>> RequestSet(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, List<Iri>>)(state.TryGetValue("requestEdges", out var e) ? e! : state["requestEdges"] = new ConcurrentDictionary<Iri, List<Iri>>());

    /// <summary>
    /// The edge index for the current state (follower → set of targets), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> EdgeSet(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, HashSet<Iri>>)(state.TryGetValue("edges", out var e) ? e! : state["edges"] = new ConcurrentDictionary<Iri, HashSet<Iri>>());

    /// <summary>
    /// Serializes the follow + pending-request indices to a JSON document. The new format is an object
    /// <c>{ "edges": [...], "requestEdges": { target: [requester, ...] } }</c>; the legacy format (a bare
    /// array of edges, no requests) is still readable (see <see cref="EdgeListFromDocument"/>).
    /// </summary>
    private static JsonDocument EdgeListToDocument(ConcurrentDictionary<string, object> state)
    {
        var edges = state.TryGetValue("edges", out var e)
            ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e!
            : new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var requests = state.TryGetValue("requestEdges", out var r)
            ? (ConcurrentDictionary<Iri, List<Iri>>)r!
            : new ConcurrentDictionary<Iri, List<Iri>>();

        var edgeList = edges.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();
        var requestObj = new Dictionary<string, string[]>();
        foreach (var kv in requests)
        {
            lock (kv.Value)
            {
                requestObj[kv.Key.Value] = kv.Value.Select(i => i.Value).ToArray();
            }
        }

        return JsonSerializer.SerializeToDocument(
            new { edges = edgeList, requestEdges = requestObj },
            FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the follow + pending-request indices from the file's root element. Accepts the new
    /// object shape (<c>{ "edges": [...], "requestEdges": {...} }</c>) and the legacy bare-array shape
    /// (an array of edges, no requests) so pre-Phase-100 store files load unchanged.
    /// </summary>
    private static void EdgeListFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var edges = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var requests = new ConcurrentDictionary<Iri, List<Iri>>();

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                // Legacy format: a bare array of edges, no pending requests.
                foreach (var item in root.EnumerateArray())
                {
                    var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
                    if (edge is not null)
                    {
                        edges.GetOrAdd(edge.Source, _ => new HashSet<Iri>()).Add(edge.Target);
                    }
                }
                break;

            case JsonValueKind.Object:
                if (root.TryGetProperty("edges", out var edgeArray) && edgeArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in edgeArray.EnumerateArray())
                    {
                        var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
                        if (edge is not null)
                        {
                            edges.GetOrAdd(edge.Source, _ => new HashSet<Iri>()).Add(edge.Target);
                        }
                    }
                }

                if (root.TryGetProperty("requestEdges", out var requestObj) && requestObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in requestObj.EnumerateObject())
                    {
                        var targetIri = new Iri(prop.Name);
                        var list = new List<Iri>();
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var requester in prop.Value.EnumerateArray())
                            {
                                if (requester.ValueKind == JsonValueKind.String && requester.GetString() is { } value)
                                {
                                    list.Add(new Iri(value));
                                }
                            }
                        }

                        requests[targetIri] = list;
                    }
                }
                break;
        }

        state["edges"] = edges;
        state["requestEdges"] = requests;
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
