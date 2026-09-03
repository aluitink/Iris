using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IActorStore"/> (Phase 16.4, production persistence): <see cref="Actor"/>
/// documents persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// Each actor is stored as a <see cref="FilePersistence.DocumentEntry"/> (the actor's IRI + its
/// ActivityStreams JSON, round-tripped through <see cref="ActivityJson"/> on read so the concrete
/// actor type — <c>Person</c>, <c>Application</c>, … — is preserved). Thread-safe (the underlying
/// <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedActorStore : IActorStore, IDisposable
{
    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed actor store over <paramref name="path"/> (creating the file on the
    /// first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedActorStore(string path)
        : this(new FilePersistence(path, DocumentMapToDocument, DocumentMapFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedActorStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (found, a) = _file.Snapshot(s =>
        {
            if (DocumentMap(s).TryGetValue(actorIri.Value, out var entry) && entry is not null)
            {
                return (true, ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as Actor);
            }

            return (false, (Actor?)null);
        });

        actor = a;
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutActorAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new ArgumentException("Actor must have a non-null Id.", nameof(actor));
        }

        var iri = new Iri(actor.Id);
        var json = ActivityJson.Serialize(actor);
        return _file.WithStateAsync(s =>
        {
            DocumentMap(s)[iri.Value] = new FilePersistence.DocumentEntry(iri, json);
            return 0;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => DocumentMap(s).TryRemove(actorIri.Value, out _), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Actor>>(s =>
        {
            var result = new List<Actor>();
            foreach (var entry in DocumentMap(s).Values)
            {
                var actor = ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as Actor;
                if (actor is not null)
                {
                    result.Add(actor);
                }
            }

            return result;
        }, ct);

    /// <summary>
    /// The document map for the current state (actor IRI value → entry), created on demand.
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
