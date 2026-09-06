namespace Iris.Server.Data.Entities;

/// <summary>
/// A media asset row (Phase 20.4 (a)): the metadata for uploaded media (a note's attachment) and the
/// proxy-fetched media (Phase 20.4 (d)). The raw bytes live in a local blob store (a file on disk)
/// referenced by <see cref="StorageKey"/>; this table holds the queryable metadata.
/// </summary>
/// <remarks>
/// The dedupe and source-URL indexes are the same directed edges as everything else (kind
/// <c>MediaSourceUrl → media id</c> and <c>MediaContentHash → media id</c>), so they are persisted in
/// the edge table rather than here.
/// </remarks>
public sealed class MediaEntity
{
    /// <summary>
    /// The media id (the last path segment of the same-origin media IRI <c>{base}/ap/v1/media/{id}</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The media's <c>Content-Type</c> (e.g. <c>image/png</c>).
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// The original file name (for the attachment's <c>name</c> / <c>&lt;img&gt;</c> <c>alt</c>).
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// The size of the stored bytes, in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// The key into the blob store that holds the raw bytes.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// When the asset was stored.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
