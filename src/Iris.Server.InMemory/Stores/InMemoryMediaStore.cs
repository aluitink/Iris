using System.Collections.Concurrent;
using System.Security.Cryptography;
using Iris.Core.Identity;
using Iris.Server.Stores;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="Iris.Server.Stores.IMediaStore"/> (Phase 20.4 (a)) backed by a concurrent
/// dictionary of
/// media id → (bytes, content-type, file-name).
/// </summary>
/// <remarks>
/// Ephemeral: uploaded media vanishes on restart. Thread-safe. The id is an unguessable
/// <see cref="Guid"/>; the same-origin media IRI is <c>{baseUrl}/ap/v1/media/{id}</c>. Phase 20.4 (d)
/// adds a content-hash dedupe index (same bytes fetched from any source URL are stored once) and a
/// source-URL → media-id index (the client-facing key for the media proxy).
/// </remarks>
public sealed class InMemoryMediaStore : IMediaStore
{
    private sealed record MediaEntry(byte[] Content, string ContentType, string FileName);

    private readonly ConcurrentDictionary<string, MediaEntry> _media = new();

    // Phase 20.4 (d): source URL (absolute, canonical string) → the same-origin media IRI (the full
    // {baseUrl}/ap/v1/media/{id} string), for the media proxy's fetch-once store + the eager-warm
    // hook's idempotency check. The full IRI (not a bare id) is stored so
    // <see cref="TryGetMediaIriBySourceUrlAsync"/> can return the serve IRI directly.
    private readonly ConcurrentDictionary<string, string> _urlToMediaId = new();

    // Phase 20.4 (d): SHA-256 hex of the content → media id, the server-internal dedupe index (the
    // same bytes from any source URL are stored once). Never exposed to the client.
    private readonly ConcurrentDictionary<string, string> _hashToMediaId = new();

    /// <inheritdoc/>
    public Task<Iri> PutAsync(byte[] content, string contentType, string fileName, Iri baseUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);

        var id = Guid.NewGuid().ToString("N");
        _media[id] = new MediaEntry(content, contentType, fileName);
        return Task.FromResult(new Iri($"{baseUrl.Value.TrimEnd('/')}/ap/v1/media/{id}"));
    }

    /// <inheritdoc/>
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
        if (id is not null && _media.TryGetValue(id, out var entry))
        {
            content = entry.Content;
            contentType = entry.ContentType;
            fileName = entry.FileName;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Extracts the media id (the last path segment) from a media IRI. A media IRI is either the full
    /// same-origin form (<c>{base}/ap/v1/media/{id}</c>) or a bare id; in both cases the id is the last
    /// path segment (a bare id's last segment is itself).
    /// </summary>
    internal static string? MediaIdFromIri(Iri mediaIri)
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

    /// <inheritdoc/>
    public Task<Iri> PutBySourceUrlAsync(
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
        if (_urlToMediaId.TryGetValue(urlKey, out var existingIri))
        {
            return Task.FromResult(new Iri(existingIri));
        }

        // Dedupe by content hash: the same bytes from a different URL are stored once (the first
        // media id wins; the second URL's index points at the same item).
        if (_hashToMediaId.TryGetValue(hash, out var dedupeId))
        {
            var dedupeIri = BuildMediaIri(baseUrl, dedupeId);
            _urlToMediaId[urlKey] = dedupeIri.Value;
            return Task.FromResult(dedupeIri);
        }

        var id = Guid.NewGuid().ToString("N");
        _media[id] = new MediaEntry(content, contentType, string.Empty);
        _hashToMediaId[hash] = id;
        var newIri = BuildMediaIri(baseUrl, id);
        _urlToMediaId[urlKey] = newIri.Value;
        return Task.FromResult(newIri);
    }

    /// <inheritdoc/>
    public Task<bool> TryGetMediaIriBySourceUrlAsync(Iri sourceUrl, out Iri? mediaIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // The URL is the client-facing key: resolve it straight to the stored same-origin media IRI
        // (the full {baseUrl}/ap/v1/media/{id} string), which the caller serves directly.
        mediaIri = null;
        if (_urlToMediaId.TryGetValue(sourceUrl.Value, out var storedIri))
        {
            mediaIri = new Iri(storedIri);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Builds the same-origin media IRI (<c>{baseUrl}/ap/v1/media/{id}</c>) for a media id.
    /// </summary>
    private static Iri BuildMediaIri(Iri baseUrl, string id)
        => new($"{baseUrl.Value.TrimEnd('/')}/ap/v1/media/{id}");

    /// <summary>
    /// Computes the SHA-256 hex digest of a byte array (the content dedupe key).
    /// </summary>
    internal static string ContentHash(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
