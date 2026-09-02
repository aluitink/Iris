using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Core.Identity;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="Stores.IMediaStore"/> (Phase 20.4 (a), production persistence): uploaded
/// media survives a host restart.
/// </summary>
/// <remarks>
/// Two-part persistence, matching the rest of the file-backed stores. The **metadata** (media id →
/// content-type + file-name) is a single JSON file (a <see cref="FilePersistence"/>), so it is durable
/// and read/written under the same lock as the other stores. The **bytes** are one raw file per media
/// id, stored as a sibling of the metadata file (<c>{id}</c> under the store's directory) and written
/// atomically (temp-file + an overwriting move, the <see cref="FilePersistence"/> recipe). The media
/// id is an unguessable <see cref="Guid"/>; the same-origin media IRI is <c>{baseUrl}/ap/v1/media/{id}</c>.
/// </remarks>
public sealed class FileBackedMediaStore : Stores.IMediaStore, IDisposable
{
    private readonly FilePersistence _file;
    private readonly string _mediaDir;
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new file-backed media store over <paramref name="path"/> (the metadata JSON file).
    /// The media bytes are stored as sibling files under the file's directory. The directory must already
    /// exist.
    /// </summary>
    /// <param name="path">The path of the metadata file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedMediaStore(string path)
        : this(new FilePersistence(path, MediaToDocument, MediaFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing metadata file. Must not be null.</param>
    public FileBackedMediaStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        // The bytes live next to the metadata file (one file per media id, named by the id).
        _mediaDir = Path.GetDirectoryName(Path.GetFullPath(file.Path))
            ?? throw new InvalidOperationException("Cannot resolve the media store's directory.");
        Directory.CreateDirectory(_mediaDir);
    }

    /// <inheritdoc/>
    public async Task<Iri> PutAsync(byte[] content, string contentType, string fileName, Iri baseUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);

        var id = Guid.NewGuid().ToString("N");

        // 1. Persist the bytes as a sibling file (atomic temp-file + move).
        WriteBytesFile(id, content, ct);

        // 2. Record the metadata (id → content-type + file-name) and persist it.
        await _file.WithStateAsync(
            s =>
            {
                Metadata(s)[id] = new MediaMeta(contentType, fileName);
                return 0;
            },
            persist: true,
            ct).ConfigureAwait(false);

        return new Iri($"{baseUrl.Value.TrimEnd('/')}/ap/v1/media/{id}");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has <c>out</c> parameters (an async method cannot); the metadata read
    /// uses the synchronous <see cref="FilePersistence.Snapshot{T}"/> overload (the in-memory index lookup
    /// under the store lock) and the byte read is a blocking file read — both are appropriate for a
    /// per-request media fetch.
    /// </remarks>
    public Task<bool> TryGetAsync(
        Iri mediaIri,
        out byte[]? content,
        out string? contentType,
        out string? fileName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        content = null;
        contentType = null;
        fileName = null;

        var id = MediaIdFromIri(mediaIri);
        if (id is null)
        {
            return Task.FromResult(false);
        }

        var bytesPath = Path.Combine(_mediaDir, id);
        if (!File.Exists(bytesPath))
        {
            return Task.FromResult(false);
        }

        var meta = _file.Snapshot(
            s =>
            {
                var index = Metadata(s);
                return index.TryGetValue(id, out var m) ? m : null;
            });

        if (meta is null)
        {
            return Task.FromResult(false);
        }

        content = File.ReadAllBytes(bytesPath);
        contentType = meta.ContentType;
        fileName = meta.FileName;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Writes a media item's bytes to its sibling file atomically (temp-file + move, the
    /// <see cref="FilePersistence"/> recipe).
    /// </summary>
    private void WriteBytesFile(string id, byte[] content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_writeLock)
        {
            var tempPath = Path.Combine(_mediaDir, $".{id}.tmp");
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, Path.Combine(_mediaDir, id), overwrite: true);
        }
    }

    /// <summary>
    /// Extracts the media id (the last path segment) from a media IRI.
    /// </summary>
    private static string? MediaIdFromIri(Iri mediaIri)
    {
        var value = mediaIri.Value;
        var lastSlash = value.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == value.Length - 1)
        {
            return null;
        }

        return value[(lastSlash + 1)..];
    }

    /// <summary>
    /// The metadata index for the current state, created on demand.
    /// </summary>
    private static ConcurrentDictionary<string, MediaMeta> Metadata(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, MediaMeta>)(
            state.TryGetValue(MetaKey, out var m) ? m! : state[MetaKey] = new ConcurrentDictionary<string, MediaMeta>());

    private const string MetaKey = "media";

    /// <summary>
    /// The metadata for one media item (its content-type and original file name).
    /// </summary>
    private sealed record MediaMeta(string ContentType, string FileName);

    /// <summary>
    /// Serializes the metadata index to a JSON document (an object mapping media id → {contentType,
    /// fileName}).
    /// </summary>
    private static JsonDocument MediaToDocument(ConcurrentDictionary<string, object> state)
    {
        var index = state.TryGetValue(MetaKey, out var m)
            ? (ConcurrentDictionary<string, MediaMeta>)m!
            : new ConcurrentDictionary<string, MediaMeta>();
        var doc = new JsonObject();
        foreach (var pair in index)
        {
            doc[pair.Key] = new JsonObject
            {
                ["contentType"] = pair.Value.ContentType,
                ["fileName"] = pair.Value.FileName,
            };
        }

        return JsonDocument.Parse(doc.ToString());
    }

    /// <summary>
    /// Populates the metadata index from the file's root element (an object mapping media id →
    /// {contentType, fileName}).
    /// </summary>
    private static void MediaFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var index = new ConcurrentDictionary<string, MediaMeta>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var contentType = property.Value.TryGetProperty("contentType", out var ct)
                    ? ct.GetString()
                    : "application/octet-stream";
                var fileName = property.Value.TryGetProperty("fileName", out var fn)
                    ? fn.GetString()
                    : string.Empty;
                index[property.Name] = new MediaMeta(contentType ?? "application/octet-stream", fileName ?? string.Empty);
            }
        }

        state[MetaKey] = index;
    }

    /// <summary>
    /// Releases the metadata file lock. The metadata file and the per-media byte files on disk are left
    /// in place (the data is durable); this only frees the <see cref="FilePersistence"/> lock.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
