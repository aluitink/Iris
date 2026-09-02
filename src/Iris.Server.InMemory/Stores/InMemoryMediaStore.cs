using System.Collections.Concurrent;
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
/// <see cref="Guid"/>; the same-origin media IRI is <c>{baseUrl}/ap/v1/media/{id}</c>.
/// </remarks>
public sealed class InMemoryMediaStore : IMediaStore
{
    private sealed record MediaEntry(byte[] Content, string ContentType, string FileName);

    private readonly ConcurrentDictionary<string, MediaEntry> _media = new();

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
    /// Extracts the media id (the last path segment) from a media IRI.
    /// </summary>
    internal static string? MediaIdFromIri(Iri mediaIri)
    {
        var value = mediaIri.Value;
        var lastSlash = value.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == value.Length - 1)
        {
            return null;
        }

        return value[(lastSlash + 1)..];
    }
}
