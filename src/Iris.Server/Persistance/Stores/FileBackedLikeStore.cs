using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="ILikeStore"/> (Phase 16.4, production persistence): like edges persisted
/// to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds both directions of the like edge: the <c>liker → likedObject</c> index (the actor's
/// <c>liked</c> collection) and the <c>likedObject → likers</c> reverse index (the object's
/// <c>likedBy</c> reverse index, the per-object like counter — decision 056 (d)). Both are maintained
/// atomically on record / remove.
/// </remarks>
/// <remarks>
/// <strong>Document format (backward compatible).</strong> The current format is a JSON object
/// <c>{"edges":[...],"likedBy":[...]}</c> where each section is an array of
/// <see cref="FilePersistence.IriEdge"/> (the <c>edges</c> section is the liker direction, the
/// <c>likedBy</c> section is the reverse direction). A file written by an earlier build holds a bare
/// array of edges (the liker direction only); on load such a file is read as <c>edges</c> and the
/// <c>likedBy</c> reverse index is rebuilt by reversing those edges, so no data is lost. The next write
/// upgrades the file to the object form. Thread-safe (the underlying <see cref="FilePersistence"/>
/// serializes reads/writes).
/// </remarks>
public sealed class FileBackedLikeStore : ILikeStore, IDisposable
{
    private const string EdgesKey = "edges";
    private const string LikedByKey = "likedBy";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed like store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedLikeStore(string path)
        : this(new FilePersistence(path, LikeDocumentToDocument, LikeDocumentFromDocument))
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
            Edges(s).GetOrAdd(likerIri, _ => new HashSet<Iri>()).Add(likedObjectIri);
            LikedBy(s).GetOrAdd(likedObjectIri, _ => new HashSet<Iri>()).Add(likerIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveLikeAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var removed = Edges(s).TryGetValue(likerIri, out var forward) && forward.Remove(likedObjectIri);
            LikedBy(s).TryGetValue(likedObjectIri, out var reverse);
            if (reverse is not null)
            {
                reverse.Remove(likerIri);
            }

            return removed;
        }, true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikedAsync(Iri likerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => Edges(s).TryGetValue(likerIri, out var set) ? set.ToList() : new List<Iri>(), ct);

    /// <inheritdoc/>
    public Task<bool> HasLikedAsync(Iri likerIri, Iri likedObjectIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => Edges(s).TryGetValue(likerIri, out var set) && set.Contains(likedObjectIri), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetLikersAsync(Iri likedObjectIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => LikedBy(s).TryGetValue(likedObjectIri, out var set) ? set.ToList() : new List<Iri>(), ct);

    /// <summary>
    /// The forward edge index for the current state (liker → set of liked objects), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> Edges(ConcurrentDictionary<string, object> state)
    {
        if (state.TryGetValue(EdgesKey, out var existing) && existing is ConcurrentDictionary<Iri, HashSet<Iri>> edges)
        {
            return edges;
        }

        var created = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        state[EdgesKey] = created;
        return created;
    }

    /// <summary>
    /// The reverse edge index for the current state (liked object → set of likers), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> LikedBy(ConcurrentDictionary<string, object> state)
    {
        if (state.TryGetValue(LikedByKey, out var existing) && existing is ConcurrentDictionary<Iri, HashSet<Iri>> likedBy)
        {
            return likedBy;
        }

        var created = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        state[LikedByKey] = created;
        return created;
    }

    /// <summary>
    /// Serializes both edge indexes to a JSON document (<c>{"edges":[...],"likedBy":[...]}</c>).
    /// </summary>
    private static JsonDocument LikeDocumentToDocument(ConcurrentDictionary<string, object> state)
    {
        var edges = state.TryGetValue(EdgesKey, out var e) ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e! : new();
        var likedBy = state.TryGetValue(LikedByKey, out var l) ? (ConcurrentDictionary<Iri, HashSet<Iri>>)l! : new();
        var document = new
        {
            edges = EdgesToIriEdges(edges),
            likedBy = EdgesToIriEdges(likedBy),
        };
        return JsonSerializer.SerializeToDocument(document, FilePersistence.JsonOptions);
    }

    private static List<FilePersistence.IriEdge> EdgesToIriEdges(ConcurrentDictionary<Iri, HashSet<Iri>> index)
        => index.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();

    /// <summary>
    /// Populates both edge indexes from the file's root element. Accepts the current object form
    /// (<c>{"edges":[...],"likedBy":[...]}</c>) and, for backward compatibility, a bare array (the
    /// earlier liker-direction-only form) — a bare array is read as <c>edges</c> and the <c>likedBy</c>
    /// reverse index is rebuilt by reversing those edges.
    /// </summary>
    private static void LikeDocumentFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var edges = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var likedBy = new ConcurrentDictionary<Iri, HashSet<Iri>>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            // Old format: a bare array of edges (liker direction only). Rebuild the reverse index by
            // reversing the edges so GetLikersAsync is correct without a data migration.
            foreach (var edge in ReadEdges(root))
            {
                AddEdge(edges, edge.Source, edge.Target);
                AddEdge(likedBy, edge.Target, edge.Source);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(EdgesKey, out var edgesEl))
            {
                foreach (var edge in ReadEdges(edgesEl))
                {
                    AddEdge(edges, edge.Source, edge.Target);
                }
            }

            if (root.TryGetProperty(LikedByKey, out var likedByEl))
            {
                foreach (var edge in ReadEdges(likedByEl))
                {
                    AddEdge(likedBy, edge.Source, edge.Target);
                }
            }
        }

        state[EdgesKey] = edges;
        state[LikedByKey] = likedBy;
    }

    private static IEnumerable<FilePersistence.IriEdge> ReadEdges(JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
            if (edge is not null)
            {
                yield return edge;
            }
        }
    }

    private static void AddEdge(ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri source, Iri target)
        => index.GetOrAdd(source, _ => new HashSet<Iri>()).Add(target);

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
