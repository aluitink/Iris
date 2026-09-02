using Iris.Core;

namespace Iris.Client;

/// <summary>
/// The result of a media upload (an <c>IMediaClient.UploadAsync</c> call, Phase 20.4 (a)): the
/// same-origin media IRI the server minted for the uploaded file, its content-type, and its file name.
/// The caller sets <see cref="MediaIri"/> as the <c>url</c> of the <c>Image</c>/<c>Document</c>
/// attachment on the note it authors (and <see cref="ContentType"/>/<see cref="FileName"/> as the
/// attachment's <c>mediaType</c>/<c>name</c>).
/// </summary>
/// <param name="MediaIri">The same-origin media IRI (<c>{base}/ap/v1/media/{id}</c>) — set it as the
/// attachment's <c>url</c>.</param>
/// <param name="ContentType">The stored media's <c>Content-Type</c> (e.g. <c>image/png</c>).</param>
/// <param name="FileName">The uploaded file's original name.</param>
public sealed record MediaUploadResult(Iri MediaIri, string ContentType, string FileName);
