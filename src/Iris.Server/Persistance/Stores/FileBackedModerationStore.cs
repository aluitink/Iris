using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IModerationStore"/> (F-07, Phase 16.4, production persistence): the
/// directed block (<c>blocker → blocked</c>), flag (<c>flagger → flagged</c>), and mute
/// (<c>muter → muted</c>) edges persisted to a single JSON file that survives a restart.
/// </summary>
/// <remarks>
/// The file holds three edge lists (blocks, flags, mutes), each an array of
/// <see cref="FilePersistence.IriEdge"/>. The block inverse query (who blocked X) is derived by
/// scanning the block edges. Thread-safe (the underlying <see cref="FilePersistence"/> serializes
/// reads/writes).
/// </remarks>
public sealed class FileBackedModerationStore : IModerationStore, IDisposable
{
    private const string Blocks = "blocks";
    private const string Flags = "flags";
    private const string Mutes = "mutes";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed moderation store over <paramref name="path"/> (creating the file
    /// on the first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedModerationStore(string path)
        : this(new FilePersistence(path, ModerationToDocument, ModerationFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedModerationStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task RecordBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            EdgeSet(s, Blocks).GetOrAdd(blockerIri, _ => new HashSet<Iri>()).Add(blockedIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s, Blocks).TryGetValue(blockerIri, out var set) && set.Remove(blockedIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlocksAsync(Iri blockerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => SortedSnapshot(EdgeSet(s, Blocks), blockerIri), ct);

    /// <inheritdoc/>
    public Task<bool> IsBlockedAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s, Blocks).TryGetValue(blockerIri, out var set) && set.Contains(blockedIri), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetBlockersAsync(Iri blockedIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s =>
        {
            // Inverse: who has blocked this actor (scan the forward index).
            var edges = EdgeSet(s, Blocks);
            return edges.Where(kv => kv.Value.Contains(blockedIri)).Select(kv => kv.Key)
                .OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
        }, ct);

    /// <inheritdoc/>
    public Task RecordFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            EdgeSet(s, Flags).GetOrAdd(flaggerIri, _ => new HashSet<Iri>()).Add(flaggedIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s, Flags).TryGetValue(flaggerIri, out var set) && set.Remove(flaggedIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetFlagsAsync(Iri flaggerIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => SortedSnapshot(EdgeSet(s, Flags), flaggerIri), ct);

    /// <inheritdoc/>
    public Task<bool> HasFlaggedAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s, Flags).TryGetValue(flaggerIri, out var set) && set.Contains(flaggedIri), ct);

    /// <inheritdoc/>
    public Task RecordMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s =>
        {
            EdgeSet(s, Mutes).GetOrAdd(muterIri, _ => new HashSet<Iri>()).Add(mutedIri);
            return 0;
        }, true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMuteAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => EdgeSet(s, Mutes).TryGetValue(muterIri, out var set) && set.Remove(mutedIri), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Iri>> GetMutesAsync(Iri muterIri, CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyList<Iri>>(s => SortedSnapshot(EdgeSet(s, Mutes), muterIri), ct);

    /// <inheritdoc/>
    public Task<bool> IsMutedAsync(Iri muterIri, Iri mutedIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => EdgeSet(s, Mutes).TryGetValue(muterIri, out var set) && set.Contains(mutedIri), ct);

    /// <summary>
    /// The named edge index for the current state, created on demand.
    /// </summary>
    private static ConcurrentDictionary<Iri, HashSet<Iri>> EdgeSet(ConcurrentDictionary<string, object> state, string name)
        => (ConcurrentDictionary<Iri, HashSet<Iri>>)(state.TryGetValue(name, out var e) ? e! : state[name] = new ConcurrentDictionary<Iri, HashSet<Iri>>());

    /// <summary>
    /// An IRI-sorted snapshot of the values for a key (empty when absent).
    /// </summary>
    private static IReadOnlyList<Iri> SortedSnapshot(ConcurrentDictionary<Iri, HashSet<Iri>> index, Iri key)
    {
        if (!index.TryGetValue(key, out var set) || set.Count == 0)
        {
            return [];
        }

        return set.OrderBy(iri => iri.Value, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Serializes the three edge indexes to a JSON document (an object with blocks/flags/mutes arrays).
    /// </summary>
    private static JsonDocument ModerationToDocument(ConcurrentDictionary<string, object> state)
    {
        var blocks = EdgeListToElement(state, Blocks);
        var flags = EdgeListToElement(state, Flags);
        var mutes = EdgeListToElement(state, Mutes);
        return JsonDocument.Parse($"{{\"{Blocks}\":{blocks},\"{Flags}\":{flags},\"{Mutes}\":{mutes}}}");
    }

    private static string EdgeListToElement(ConcurrentDictionary<string, object> state, string name)
    {
        var edges = state.TryGetValue(name, out var e)
            ? (ConcurrentDictionary<Iri, HashSet<Iri>>)e!
            : new ConcurrentDictionary<Iri, HashSet<Iri>>();
        var list = edges.SelectMany(kv => kv.Value.Select(t => new FilePersistence.IriEdge(kv.Key, t))).ToList();
        return JsonSerializer.Serialize(list, FilePersistence.JsonOptions);
    }

    /// <summary>
    /// Populates the three edge indexes from the file's root element (an object with blocks/flags/mutes).
    /// </summary>
    private static void ModerationFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var name in new[] { Blocks, Flags, Mutes })
        {
            var index = new ConcurrentDictionary<Iri, HashSet<Iri>>();
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var edge = item.Deserialize<FilePersistence.IriEdge>(FilePersistence.JsonOptions);
                    if (edge is not null)
                    {
                        index.GetOrAdd(edge.Source, _ => new HashSet<Iri>()).Add(edge.Target);
                    }
                }
            }

            state[name] = index;
        }
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}