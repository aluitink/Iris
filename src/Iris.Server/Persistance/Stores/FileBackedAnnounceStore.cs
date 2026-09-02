using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IAnnounceStore"/> (production persistence): announce (boost) edges
/// persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds both directions of the announce edge: the <c>announcer → announcedObject</c> index
/// (the actor's boosts) and the <c>announcedObject → announcers</c> reverse index (the object's
/// <c>shares</c> reverse index, the per-object boost counter — decision 056 (d)). Both are maintained
/// atomically on record / remove. (The reverse index's on-disk section is named <c>announcedBy</c> —
/// an internal storage key, not the wire term.)
/// </remarks>
/// <remarks>
/// <strong>Document format.</strong> The file is a JSON object
/// <c>{"edges":[...],"announcedBy":[...]}</c> where each section is an array of
/// <see cref="FilePersistence.IriEdge"/> (the <c>edges</c> section is the announcer direction, the
/// <c>announcedBy</c> section is the reverse direction). Thread-safe (the underlying
/// <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedAnnounceStore : IAnnounceStore, IDisposable
{
    private const string EdgesKey = "edges";
    private const string AnnouncedByKey = "announcedBy";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed announce store over <paramref name="path"/> (creating the file on
    /// the first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedAnnounceStore(string path)
        : this(new FilePersistence(path, AnnounceDocumentToDocument, AnnounceDocumentFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedAnnounceStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            Edges(s).GetOrAdd(announcerIri, _ => new HashSet<Iri>()).Add(announcedObjectIri);
            AnnouncedBy(s).GetOrAdd(announcedObjectIri, _ => new HashSet<Iri>()).Add(announcerIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveAnnounceAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            var removed = Edges(s).TryGetValue(announcerIri, out var forward) && forward.Remove(announcedObjectIri);
            AnnouncedBy(s).TryGetValue(announcedObjectIri, out var reverse);
            if (reverse is not null)
            {
                reverse.Remove(announcerIri);
            }

            return removed;
        }, true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncedAsync(Iri announcerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => Edges(s).TryGetValue(announcerIri, out var set) ? set.ToList() : new List<Iri>(), ct);

    /// <inheritdoc/>
    public Task<bool> HasAnnouncedAsync(Iri announcerIri, Iri announcedObjectIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => Edges(s).TryGetValue(announcerIri, out var set) && set.Contains(announcedObjectIri), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetAnnouncersAsync(Iri announcedObjectIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => AnnouncedBy(s).TryGetValue(announcedObjectIri, out var set) ? set.ToList() : new List<Iri>(), ct);

    /// <summary>
    /// The forward edge index for the current state (announcer → set of announced objects), created on
    /// demand.
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
    /// The reverse edge index for the current state (announced object → set of announcers), created on
    /// demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> AnnouncedBy(ConcurrentDictionary<string, object> state)
    {
        if (state.TryGetValue(AnnouncedByKey, out var existing) && existing is ConcurrentDictionary<Iri, HashSet<Iri>> announcedBy)
        {
            return announcedBy;
        }

        var created = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        state[AnnouncedByKey] = created;
        return created;
    }

    /// <summary>
    /// Serializes both edge indexes to a JSON document (<c>{"edges":[...],"announcedBy":[...]}</c>).
    /// </summary>
    private static JsonDocument AnnounceDocumentToDocument(ConcurrentDictionary<string, object> state)
    {
        var edges = state.TryGetValue(EdgesKey, out var e) ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e! : new();
        var announcedBy = state.TryGetValue(AnnouncedByKey, out var a) ? (ConcurrentDictionary<Iri, HashSet<Iri>>)a! : new();
        var document = new
        {
            edges = EdgesToIriEdges(edges),
            announcedBy = EdgesToIriEdges(announcedBy),
        };
        return JsonSerializer.SerializeToDocument(document, FilePersistence.JsonOptions);
    }

    private static List<FilePersistence.IriEdge> EdgesToIriEdges(ConcurrentDictionary<Iri, HashSet<Iri>> index)
        => index.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();

    /// <summary>
    /// Populates both edge indexes from the file's root element (an object
    /// <c>{"edges":[...],"announcedBy":[...]}</c>).
    /// </summary>
    private static void AnnounceDocumentFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var edges = new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var announcedBy = new ConcurrentDictionary<Iri, HashSet<Iri>>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(EdgesKey, out var edgesEl))
            {
                foreach (var edge in ReadEdges(edgesEl))
                {
                    AddEdge(edges, edge.Source, edge.Target);
                }
            }

            if (root.TryGetProperty(AnnouncedByKey, out var announcedByEl))
            {
                foreach (var edge in ReadEdges(announcedByEl))
                {
                    AddEdge(announcedBy, edge.Source, edge.Target);
                }
            }
        }

        state[EdgesKey] = edges;
        state[AnnouncedByKey] = announcedBy;
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
