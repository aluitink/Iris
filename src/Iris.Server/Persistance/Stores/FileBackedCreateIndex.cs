using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="ICreateIndex"/> (production persistence): object IRI → Create IRI links
/// persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds the object → Create links (the <see cref="FilePersistence.IriEdge"/>s, source = the
/// created object, target = the <c>Create</c> that produced it). Thread-safe (the underlying
/// <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedCreateIndex : ICreateIndex, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed create index over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedCreateIndex(string path)
        : this(new FilePersistence(path, LinksToDocument, LinksFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new index over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedCreateIndex(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordAsync(Iri objectIri, Iri createIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            LinkSet(s)[objectIri] = createIri;
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(Iri objectIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => LinkSet(s).TryRemove(objectIri, out _), true, ct);

    /// <inheritdoc/>
    public Task<Iri?> TryGetCreateIriAsync(Iri objectIri, CancellationToken ct = default)
        => _file.SnapshotAsync<Iri?>(s => LinkSet(s).TryGetValue(objectIri, out var createIri) ? createIri : null, ct);

    /// <summary>
    /// The link index for the current state (object IRI → Create IRI), created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, Iri> LinkSet(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<Iri, Iri>)(state.TryGetValue("links", out var l) ? l! : state["links"] = new ConcurrentDictionary<Iri, Iri>());

    /// <summary>
    /// Serializes the link index to a JSON document (an array of <see cref="FilePersistence.IriEdge"/>,
    /// source = the created object, target = the <c>Create</c>).
    /// </summary>
    private static JsonDocument LinksToDocument(ConcurrentDictionary<string, object> state)
    {
        var links = state.TryGetValue("links", out var l)
            ? (ConcurrentDictionary<Iri, Iri>)l!
            : new ConcurrentDictionary<Iri, Iri>();
        var list = links.Select(kv => new FilePersistence.IriEdge(kv.Key, kv.Value)).ToList();
        return JsonSerializer.SerializeToDocument(list, FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the link index from the file's root element (an array of edges).
    /// </summary>
    private static void LinksFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var links = new ConcurrentDictionary<Iri, Iri>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
                if (edge is not null)
                {
                    links[edge.Source] = edge.Target;
                }
            }
        }

        state["links"] = links;
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
