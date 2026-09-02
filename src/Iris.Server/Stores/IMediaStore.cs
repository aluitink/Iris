using Iris.Core.Identity;

namespace Iris.Server.Stores;

/// <summary>
/// The media store (Phase 20.4 (a)): persists uploaded media (a note's attachment — an image or
/// document) so it can be served back from the same origin, and hands out the same-origin media IRI
/// that an object's attachment references.
/// </summary>
/// <remarks>
/// A media item is stored under an unguessable id (a <see cref="Guid"/>); the server builds the
/// same-origin media IRI (<c>{baseUrl}/ap/v1/media/{id}</c>) and returns it to the uploader, who sets
/// it as the <c>url</c> of the <c>Image</c>/<c>Document</c> attachment on the note they author. The
/// browser then loads the attachment from that same-origin IRI (never a cross-origin media host).
/// Implementations: <c>Iris.Server.InMemory</c> (ephemeral) and <c>Iris.Server.Persistance</c>
/// (file-backed, survives a restart).
/// </remarks>
public interface IMediaStore
{
    /// <summary>
    /// Stores a media item and returns its same-origin media IRI.
    /// </summary>
    /// <param name="content">The media bytes (the file the user uploaded).</param>
    /// <param name="contentType">The media's <c>Content-Type</c> (e.g. <c>image/png</c>).</param>
    /// <param name="fileName">The original file name (for the attachment's <c>name</c> / the
    /// <c>&lt;img&gt;</c> <c>alt</c>; not part of the identity).</param>
    /// <param name="baseUrl">The instance's base IRI (e.g. <c>https://host</c>); the media IRI is built
    /// as <c>{baseUrl}/ap/v1/media/{id}</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The same-origin media IRI the uploader should reference from the attachment.</returns>
    Task<Iri> PutAsync(byte[] content, string contentType, string fileName, Iri baseUrl, CancellationToken ct = default);

    /// <summary>
    /// Reads a stored media item (by its same-origin media IRI) back, for serving.
    /// </summary>
    /// <param name="mediaIri">The media IRI (the <c>{baseUrl}/ap/v1/media/{id}</c> form, or an IRI whose
    /// last path segment is the id).</param>
    /// <param name="content">Receives the media bytes when found.</param>
    /// <param name="contentType">Receives the media's <c>Content-Type</c> when found.</param>
    /// <param name="fileName">Receives the original file name when found.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when the media item exists; otherwise <see langword="false"/>.</returns>
    Task<bool> TryGetAsync(
        Iri mediaIri,
        out byte[]? content,
        out string? contentType,
        out string? fileName,
        CancellationToken ct = default);
}
