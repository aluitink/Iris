using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IObjectStore"/> (Phase 16.4, production persistence): generic
/// ActivityStreams content objects (notes, articles, media, <see cref="Tombstone"/>s) persisted to a
/// single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// Each object is stored as a <see cref="FilePersistence.DocumentEntry"/> (the object's IRI + its
/// ActivityStreams JSON, round-tripped through <see cref="ActivityJson"/> on read so the concrete
/// type — <c>Note</c>, <c>Article</c>, <c>Tombstone</c>, … — is preserved). Thread-safe (the
/// underlying <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedObjectStore : IObjectStore, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed object store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedObjectStore(string path)
        : this(new FilePersistence(path, DocumentMapToDocument, DocumentMapFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedObjectStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task<bool> TryGetObjectAsync(Iri objectIri, out IObject? obj, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (found, o) = _file.Snapshot(s =>
        {
            if (DocumentMap(s).TryGetValue(objectIri.Value, out var entry) && entry is not null)
            {
                return (true, ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as IObject);
            }

            return (false, (IObject?)null);
        });

        obj = o;
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

        var iri = new Iri(obj.Id);
        var json = ActivityJson.Serialize(obj);
        return _file.WithStateAsync(s =>
        {
            DocumentMap(s)[iri.Value] = new FilePersistence.DocumentEntry(iri, json);
            return 0;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<bool> TryDeleteObjectAsync(Iri objectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => DocumentMap(s).TryRemove(objectIri.Value, out _), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObject>> ListObjectsAsync(CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<IObject>>(s =>
        {
            var result = new List<IObject>();
            foreach (var entry in DocumentMap(s).Values)
            {
                var obj = ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as IObject;
                if (obj is not null)
                {
                    result.Add(obj);
                }
            }

            return result;
        }, ct);

    /// <summary>
    /// The document map for the current state (object IRI value → entry), created on demand.
    /// </summary>
    private static ConcurrentDictionary<string, FilePersistence.DocumentEntry> DocumentMap(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)(state.TryGetValue("docs", out var d) ? d! : state["docs"] = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>());

    /// <summary>
    /// Serializes the document map to a JSON document (an object of IRI → <see cref="FilePersistence.DocumentEntry"/>).
    /// </summary>
    private static JsonDocument DocumentMapToDocument(ConcurrentDictionary<string, object> state)
    {
        var map = state.TryGetValue("docs", out var d)
            ? (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)d!
            : new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        var dict = map.ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.SerializeToDocument(dict, FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the document map from the file's root element (an object of IRI → entry).
    /// </summary>
    private static void DocumentMapFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var map = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                var entry = prop.Value.Deserialize<FilePersistence.DocumentEntry>(FilePersistence.JsonOptions);
                if (entry is not null)
                {
                    map[entry.Iri.Value] = entry;
                }
            }
        }

        state["docs"] = map;
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}