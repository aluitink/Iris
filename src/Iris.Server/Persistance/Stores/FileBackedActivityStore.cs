using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IActivityStore"/> (Phase 16.4, production persistence): activity documents
/// and per-actor outboxes persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds two sections: <c>activities</c> (a map of activity IRI →
/// <see cref="FilePersistence.DocumentEntry"/>) and <c>outboxes</c> (a map of actor IRI → list of
/// outbox-item JSON strings, newest first). Activities and outbox items round-trip through
/// <see cref="ActivityJson"/> so the concrete type is preserved. Thread-safe (the underlying
/// <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedActivityStore : IActivityStore, IDisposable
{
    private const string Activities = "activities";
    private const string Outboxes = "outboxes";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed activity store over <paramref name="path"/> (creating the file on
    /// the first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedActivityStore(string path)
        : this(new FilePersistence(path, ActivityToDocument, ActivityFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedActivityStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task<bool> TryGetActivityAsync(Iri activityIri, out IObject? activity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (found, a) = _file.Snapshot(s =>
        {
            if (ActivityMap(s).TryGetValue(activityIri.Value, out var entry) && entry is not null)
            {
                return (true, ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as IObject);
            }

            return (false, (IObject?)null);
        });

        activity = a;
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        var iri = new Iri(activity.Id);
        var json = ActivityJson.Serialize(activity);
        return _file.WithStateAsync(s =>
        {
            ActivityMap(s)[iri.Value] = new FilePersistence.DocumentEntry(iri, json);
            return 0;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<bool> TryAddActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        var iri = new Iri(activity.Id);
        var json = ActivityJson.Serialize(activity);
        // The state function runs under the store lock, so the check-then-store is atomic: the add wins iff
        // the activity IRI is not already present. The file is rewritten on every call (matching
        // PutActivityAsync's persist:true); when the key already existed the state is unchanged, so the
        // rewrite is a harmless no-op and a re-delivered activity durably reports false.
        return _file.WithStateAsync(s =>
        {
            var map = ActivityMap(s);
            if (map.ContainsKey(iri.Value))
            {
                return false;
            }

            map[iri.Value] = new FilePersistence.DocumentEntry(iri, json);
            return true;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<IObjectOrLink>>(s =>
        {
            var outboxes = OutboxMap(s);
            var result = new List<IObjectOrLink>();
            if (outboxes.TryGetValue(actorIri.Value, out var items))
            {
                foreach (var itemJson in items)
                {
                    var item = ActivityJson.Deserialize<IObjectOrLink>(itemJson);
                    if (item is not null)
                    {
                        result.Add(item);
                    }
                }
            }

            return result;
        }, ct);

    /// <inheritdoc/>
    public Task AddToOutboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var json = ActivityJson.Serialize(item);
        var itemIri = item is IObject obj ? obj.Id : (item as Link)?.Href?.AbsoluteUri;
        return _file.WithStateAsync(s =>
        {
            var outboxes = OutboxMap(s);
            if (!outboxes.TryGetValue(actorIri.Value, out var list))
            {
                list = new List<string>();
                outboxes[actorIri.Value] = list;
            }

            // Idempotent by IRI (F-1911-2): a re-recorded activity (at-least-once delivery, restart
            // replay) is not duplicated in the outbox.
            if (itemIri is not null)
            {
                var alreadyPresent = list.Any(existing =>
                {
                    var existingItem = ActivityJson.Deserialize<IObjectOrLink>(existing);
                    var existingIri = existingItem is IObject eobj ? eobj.Id : (existingItem as Link)?.Href?.AbsoluteUri;
                    return existingIri == itemIri;
                });
                if (alreadyPresent)
                {
                    return 0;
                }
            }

            list.Insert(0, json); // newest first (mirrors the in-memory store)
            return 0;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveFromOutboxAsync(Iri actorIri, Iri itemIri, CancellationToken ct = default)
    {
        return _file.WithStateAsync(s =>
        {
            var outboxes = OutboxMap(s);
            if (outboxes.TryGetValue(actorIri.Value, out var list))
            {
                var removed = list.RemoveAll(itemJson =>
                {
                    var item = ActivityJson.Deserialize<IObjectOrLink>(itemJson);
                    var iri = item is IObject obj ? obj.Id : (item as Link)?.Href?.AbsoluteUri;
                    return iri == itemIri.Value;
                }) > 0;
                return removed;
            }

            return false;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IObject>> GetAllActivitiesAsync(CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<IObject>>(s =>
        {
            var map = ActivityMap(s);
            var result = new List<IObject>();
            foreach (var entry in map.Values)
            {
                if (entry is null)
                {
                    continue;
                }

                var activity = ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as IObject;
                if (activity is not null)
                {
                    result.Add(activity);
                }
            }

            return result;
        }, ct);

    /// <summary>
    /// The activity document map for the current state (activity IRI value → entry), created on demand.
    /// </summary>
    private static ConcurrentDictionary<string, FilePersistence.DocumentEntry> ActivityMap(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)(state.TryGetValue(Activities, out var d) ? d! : state[Activities] = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>());

    /// <summary>
    /// The outbox map for the current state (actor IRI value → list of outbox-item JSON strings, newest
    /// first), created on demand.
    /// </summary>
    private static ConcurrentDictionary<string, List<string>> OutboxMap(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, List<string>>)(state.TryGetValue(Outboxes, out var d) ? d! : state[Outboxes] = new ConcurrentDictionary<string, List<string>>());

    /// <summary>
    /// Serializes both sections to a JSON document.
    /// </summary>
    private static JsonDocument ActivityToDocument(ConcurrentDictionary<string, object> state)
    {
        var activities = state.TryGetValue(Activities, out var a)
            ? (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)a!
            : new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        var outboxes = state.TryGetValue(Outboxes, out var o)
            ? (ConcurrentDictionary<string, List<string>>)o!
            : new ConcurrentDictionary<string, List<string>>();

        var activitiesJson = JsonSerializer.Serialize(activities.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        var outboxesJson = JsonSerializer.Serialize(outboxes.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        return JsonDocument.Parse($"{{\"{Activities}\":{activitiesJson},\"{Outboxes}\":{outboxesJson}}}");
    }

    /// <summary>
    /// Populates both sections from the file's root element.
    /// </summary>
    private static void ActivityFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var activities = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        if (root.TryGetProperty(Activities, out var aEl) && aEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in aEl.EnumerateObject())
            {
                var entry = prop.Value.Deserialize<FilePersistence.DocumentEntry>(FilePersistence.JsonOptions);
                if (entry is not null)
                {
                    activities[entry.Iri.Value] = entry;
                }
            }
        }

        var outboxes = new ConcurrentDictionary<string, List<string>>();
        if (root.TryGetProperty(Outboxes, out var oEl) && oEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in oEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (s is not null)
                        {
                            list.Add(s);
                        }
                    }

                    outboxes[prop.Name] = list;
                }
            }
        }

        state[Activities] = activities;
        state[Outboxes] = outboxes;
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}