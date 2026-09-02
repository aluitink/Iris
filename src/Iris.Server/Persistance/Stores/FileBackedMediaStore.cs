using System.Collections.Concurrent;
using System.Security.Cryptography;
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
                Metadata(s)[id] = new MediaMeta(contentType, fileName, ContentHash: null);
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

    /// <inheritdoc/>
    public async Task<Iri> PutBySourceUrlAsync(
        Iri sourceUrl,
        byte[] content,
        string contentType,
        Iri baseUrl,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);

        var urlKey = sourceUrl.Value;
        var hash = ContentHash(content);

        // Idempotent: the source URL is already stored → return its existing media IRI (no re-write).
        // The URL index stores the full same-origin media IRI (not a bare id) so
        // TryGetMediaIriBySourceUrlAsync can return the serve IRI directly.
        var existingIri = await _file
            .SnapshotAsync(s => Lookup(UrlIndex(s), urlKey), ct)
            .ConfigureAwait(false);
        if (existingIri is not null)
        {
            return new Iri(existingIri);
        }

        // Dedupe by content hash: the same bytes from a different URL are stored once (the first media
        // id wins; the second URL's index points at the same item).
        var dedupeId = await _file
            .SnapshotAsync(s => Lookup(HashIndex(s), hash), ct)
            .ConfigureAwait(false);
        if (dedupeId is not null)
        {
            var dedupeIri = BuildMediaIri(baseUrl, dedupeId);
            await _file.WithStateAsync(s => UrlIndex(s)[urlKey] = dedupeIri.Value, persist: true, ct).ConfigureAwait(false);
            return dedupeIri;
        }

        // New item: write the bytes as a sibling file, then record the metadata + both indices.
        var id = Guid.NewGuid().ToString("N");
        WriteBytesFile(id, content, ct);
        var newIri = BuildMediaIri(baseUrl, id);
        await _file.WithStateAsync(
            s =>
            {
                Metadata(s)[id] = new MediaMeta(contentType, string.Empty, hash);
                UrlIndex(s)[urlKey] = newIri.Value;
                HashIndex(s)[hash] = id;
                return 0;
            },
            persist: true,
            ct).ConfigureAwait(false);
        return newIri;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the URL-index
    /// read is the synchronous <see cref="FilePersistence.Snapshot{T}"/> (the in-memory index lookup under
    /// the store lock), appropriate for a per-request media lookup.
    /// </remarks>
    public Task<bool> TryGetMediaIriBySourceUrlAsync(Iri sourceUrl, out Iri? mediaIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var storedIri = _file.Snapshot(s => Lookup(UrlIndex(s), sourceUrl.Value));
        if (storedIri is null)
        {
            mediaIri = null;
            return Task.FromResult(false);
        }

        mediaIri = new Iri(storedIri);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Looks up a value in a string → string index (a read helper for the state actions).
    /// </summary>
    private static string? Lookup(ConcurrentDictionary<string, string> index, string key)
        => index.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Builds the same-origin media IRI (<c>{baseUrl}/ap/v1/media/{id}</c>) for a media id.
    /// </summary>
    private static Iri BuildMediaIri(Iri baseUrl, string id)
        => new($"{baseUrl.Value.TrimEnd('/')}/ap/v1/media/{id}");

    /// <summary>
    /// Computes the SHA-256 hex digest of a byte array (the content dedupe key).
    /// </summary>
    private static string ContentHash(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

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
    /// Extracts the media id (the last path segment) from a media IRI. A media IRI is either the full
    /// same-origin form (<c>{base}/ap/v1/media/{id}</c>) or a bare id (the form
    /// <see cref="Stores.IMediaStore.TryGetMediaIriBySourceUrlAsync"/> may return); in both cases the id
    /// is the last path segment (a bare id's last segment is itself).
    /// </summary>
    private static string? MediaIdFromIri(Iri mediaIri)
    {
        var value = mediaIri.Value;
        if (value.Length == 0)
        {
            return null;
        }

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash == value.Length - 1)
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

    /// <summary>
    /// The source-URL → media-id index (the client-facing key for the media proxy, Phase 20.4 (d)).
    /// </summary>
    private static ConcurrentDictionary<string, string> UrlIndex(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, string>)(
            state.TryGetValue(UrlKey, out var u) ? u! : state[UrlKey] = new ConcurrentDictionary<string, string>());

    /// <summary>
    /// The content-hash (SHA-256 hex) → media-id index (the server-internal dedupe index, Phase 20.4
    /// (d)).
    /// </summary>
    private static ConcurrentDictionary<string, string> HashIndex(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, string>)(
            state.TryGetValue(HashKey, out var h) ? h! : state[HashKey] = new ConcurrentDictionary<string, string>());

    private const string MetaKey = "media";
    private const string UrlKey = "urlIndex";
    private const string HashKey = "hashIndex";

    /// <summary>
    /// The metadata for one media item (its content-type, original file name, and — for a proxy-fetched
    /// item — its content hash). The hash is null for an upload (the dedupe index is keyed by the
    /// uploaded bytes but is not persisted for uploads, so a hash is only recorded for proxy items).
    /// </summary>
    private sealed record MediaMeta(string ContentType, string FileName, string? ContentHash);

    /// <summary>
    /// Serializes the metadata index + the two Phase 20.4 (d) indices to a JSON document: an object with
    /// a <c>media</c> member (media id → {contentType, fileName, contentHash}) and, when present,
    /// <c>urlIndex</c> (source URL → media id) and <c>hashIndex</c> (content hash → media id).
    /// </summary>
    private static JsonDocument MediaToDocument(ConcurrentDictionary<string, object> state)
    {
        var media = state.TryGetValue(MetaKey, out var m)
            ? (ConcurrentDictionary<string, MediaMeta>)m!
            : new ConcurrentDictionary<string, MediaMeta>();
        var doc = new JsonObject();

        var mediaObj = new JsonObject();
        foreach (var pair in media)
        {
            var item = new JsonObject
            {
                ["contentType"] = pair.Value.ContentType,
                ["fileName"] = pair.Value.FileName,
            };
            if (pair.Value.ContentHash is { Length: > 0 } hash)
            {
                item["contentHash"] = hash;
            }

            mediaObj[pair.Key] = item;
        }

        doc[MetaKey] = mediaObj;

        if (state.TryGetValue(UrlKey, out var u) && ((ConcurrentDictionary<string, string>)u!).Count > 0)
        {
            var urlObj = new JsonObject();
            foreach (var pair in (ConcurrentDictionary<string, string>)u!)
            {
                urlObj[pair.Key] = pair.Value;
            }

            doc[UrlKey] = urlObj;
        }

        if (state.TryGetValue(HashKey, out var h) && ((ConcurrentDictionary<string, string>)h!).Count > 0)
        {
            var hashObj = new JsonObject();
            foreach (var pair in (ConcurrentDictionary<string, string>)h!)
            {
                hashObj[pair.Key] = pair.Value;
            }

            doc[HashKey] = hashObj;
        }

        return JsonDocument.Parse(doc.ToString());
    }

    /// <summary>
    /// Populates the metadata index + the two Phase 20.4 (d) indices from the file's root element. The
    /// root is an object with a <c>media</c> member (media id → {contentType, fileName, contentHash}) and,
    /// when present, <c>urlIndex</c> / <c>hashIndex</c> members.
    /// </summary>
    private static void MediaFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var index = new ConcurrentDictionary<string, MediaMeta>();
        var urlIndex = new ConcurrentDictionary<string, string>();
        var hashIndex = new ConcurrentDictionary<string, string>();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(MetaKey, out var mediaElement)
            && mediaElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in mediaElement.EnumerateObject())
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
                var contentHash = property.Value.TryGetProperty("contentHash", out var ch)
                    ? ch.GetString()
                    : null;
                index[property.Name] = new MediaMeta(
                    contentType ?? "application/octet-stream",
                    fileName ?? string.Empty,
                    contentHash);
            }
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(UrlKey, out var urlElement)
            && urlElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in urlElement.EnumerateObject())
            {
                urlIndex[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(HashKey, out var hashElement)
            && hashElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in hashElement.EnumerateObject())
            {
                hashIndex[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        state[MetaKey] = index;
        state[UrlKey] = urlIndex;
        state[HashKey] = hashIndex;
    }

    /// <summary>
    /// Releases the metadata file lock. The metadata file and the per-media byte files on disk are left
    /// in place (the data is durable); this only frees the <see cref="FilePersistence"/> lock.
    /// </summary>
    public void Dispose() => _file.Dispose();
}
