using System.Security.Cryptography;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IMediaStore"/>: media metadata lives in the <c>Media</c> table
/// (plus the source-URL and content-hash dedupe edges in the <c>Edges</c> table); the raw bytes live in
/// a local blob directory (a file per media id) referenced by the row's <c>StorageKey</c>.
/// </summary>
/// <remarks>
/// A media item's identity is its unguessable <see cref="Guid"/> (in "N" form); the same-origin media
/// IRI is <c>{baseUrl}/ap/v1/media/{id}</c>. The source-URL edge stores the full same-origin media IRI
/// as its target (so <see cref="TryGetMediaIriBySourceUrlAsync"/> can return the serve IRI directly); the
/// content-hash edge stores the bare media id (the server-internal dedupe key).
/// </remarks>
public sealed class EfMediaStore : IMediaStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;
    private readonly EdgeStore _edges;
    private readonly string _blobDir;

    /// <summary>
    /// Initializes the store over a context factory, a shared edge store, and a blob directory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    /// <param name="edges">The shared <see cref="EdgeStore"/>. Must not be null.</param>
    /// <param name="blobDir">The directory that holds the raw media bytes (created if missing).</param>
    public EfMediaStore(IDbContextFactory<IrisDbContext> factory, EdgeStore edges, string blobDir)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _edges = edges ?? throw new ArgumentNullException(nameof(edges));
        _blobDir = blobDir ?? throw new ArgumentNullException(nameof(blobDir));
        Directory.CreateDirectory(_blobDir);
    }

    /// <inheritdoc/>
    public async Task<Iri> PutAsync(byte[] content, string contentType, string fileName, Iri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var mediaIri = BuildMediaIri(baseUrl, id);
        var storageKey = Path.Combine(_blobDir, id);
        var tempKey = Path.Combine(_blobDir, $".{id}.tmp");

        await File.WriteAllBytesAsync(tempKey, content, ct).ConfigureAwait(false);
        File.Move(tempKey, storageKey, overwrite: true);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Set<MediaEntity>().Add(new MediaEntity
        {
            Id = id,
            ContentType = contentType ?? string.Empty,
            FileName = fileName,
            SizeBytes = content.LongLength,
            StorageKey = storageKey,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return mediaIri;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has <c>out</c> parameters (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query + a local file read under a short-lived context
    /// (mirrors the in-memory store's contract).
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

        using var db = _factory.CreateDbContext();
        var entity = db.Set<MediaEntity>().AsNoTracking().FirstOrDefault(e => e.Id == id);
        if (entity is null || !File.Exists(entity.StorageKey))
        {
            return Task.FromResult(false);
        }

        content = File.ReadAllBytes(entity.StorageKey);
        contentType = entity.ContentType;
        fileName = entity.FileName;
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
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();
        var urlKey = sourceUrl.Value;
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Idempotent: the source URL is already stored → return its existing media IRI (no re-write).
        var urlEdge = await db.Set<EdgeEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Kind == EdgeKind.MediaSourceUrl && e.Source == urlKey, ct).ConfigureAwait(false);
        if (urlEdge is not null)
        {
            return new Iri(urlEdge.Target);
        }

        // Dedupe by content hash: the same bytes from a different URL are stored once (the first media
        // id wins; the new URL's index points at the same item).
        var hashEdge = await db.Set<EdgeEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Kind == EdgeKind.MediaContentHash && e.Source == hash, ct).ConfigureAwait(false);
        if (hashEdge is not null)
        {
            var dedupeIri = BuildMediaIri(baseUrl, hashEdge.Target);
            await SetSourceUrlEdgeAsync(db, urlKey, dedupeIri.Value, ct).ConfigureAwait(false);
            return dedupeIri;
        }

        // New bytes: write the blob, record the metadata row + content-hash edge, then the source-URL edge.
        var id = Guid.NewGuid().ToString("N");
        var newIri = BuildMediaIri(baseUrl, id);
        var storageKey = Path.Combine(_blobDir, id);
        var tempKey = Path.Combine(_blobDir, $".{id}.tmp");
        await File.WriteAllBytesAsync(tempKey, content, ct).ConfigureAwait(false);
        File.Move(tempKey, storageKey, overwrite: true);

        db.Set<MediaEntity>().Add(new MediaEntity
        {
            Id = id,
            ContentType = contentType ?? string.Empty,
            FileName = null,
            SizeBytes = content.LongLength,
            StorageKey = storageKey,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Set<EdgeEntity>().Add(new EdgeEntity { Kind = EdgeKind.MediaContentHash, Source = hash, Target = id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await SetSourceUrlEdgeAsync(db, urlKey, newIri.Value, ct).ConfigureAwait(false);
        return newIri;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query under a short-lived context (mirrors the in-memory store).
    /// </remarks>
    public Task<bool> TryGetMediaIriBySourceUrlAsync(Iri sourceUrl, out Iri? mediaIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        mediaIri = null;
        using var db = _factory.CreateDbContext();
        var edge = db.Set<EdgeEntity>().AsNoTracking().FirstOrDefault(e => e.Kind == EdgeKind.MediaSourceUrl && e.Source == sourceUrl.Value);
        if (edge is null)
        {
            return Task.FromResult(false);
        }

        mediaIri = new Iri(edge.Target);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Records (idempotently) the source URL → media-IRI edge.
    /// </summary>
    private static async Task SetSourceUrlEdgeAsync(IrisDbContext db, string sourceUrl, string mediaIri, CancellationToken ct)
    {
        var exists = await db.Set<EdgeEntity>().AnyAsync(e => e.Kind == EdgeKind.MediaSourceUrl && e.Source == sourceUrl, ct).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.Set<EdgeEntity>().Add(new EdgeEntity { Kind = EdgeKind.MediaSourceUrl, Source = sourceUrl, Target = mediaIri, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the same-origin media IRI (<c>{baseUrl}/ap/v1/media/{id}</c>) for a media id.
    /// </summary>
    private static Iri BuildMediaIri(Iri baseUrl, string id)
        => new Iri($"{baseUrl.Value.TrimEnd('/')}/ap/v1/media/{id}");

    /// <summary>
    /// Extracts the media id (the last path segment) from a media IRI (the full same-origin form or a
    /// bare id).
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
}
