using Iris.Core;

namespace Iris.Client;

/// <summary>
/// The client for **uploading a note's media attachment** (Phase 20.4 (a)): a local, non-federated,
/// Basic-authenticated multipart POST of a file to the acting actor's own home instance.
/// </summary>
/// <remarks>
/// A media upload is not an ActivityStreams activity (the file is not an activity, and the server stores
/// it and returns a same-origin media IRI), so it is not a signed inbox delivery. It is a
/// **Basic-authenticated** multipart POST to <c>POST {LocalRoutePrefix}/u/{handle}/media</c>, where the
/// instance identifies the actor from the credentials (<c>IActorCredentialValidator</c>), stores the
/// bytes, and returns (201) the same-origin media IRI (<c>{base}/ap/v1/media/{id}</c>) + content-type +
/// file name. The caller sets that IRI as the <c>url</c> of the <c>Image</c>/<c>Document</c> attachment
/// on the note it authors. This is the dedicated surface for that write; the corresponding *serve* read
/// (<c>GET /ap/v1/media/{id}</c>) is a public resource load (the browser's <c>&lt;img&gt;</c>), not a
/// client call.
/// </remarks>
public interface IMediaClient
{
    /// <summary>
    /// Uploads a media file (a note's attachment) to the acting actor's instance.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor uploading the media (e.g.
    /// <c>https://host/ap/v1/u/bob</c>).</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="contentType">The file's <c>Content-Type</c> (e.g. <c>image/png</c>).</param>
    /// <param name="fileName">The file's original name (for the attachment's <c>name</c> / the
    /// <c>&lt;img&gt;</c> <c>alt</c>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The upload result: the same-origin media IRI (to set as the attachment's <c>url</c>), the
    /// content-type, and the file name.</returns>
    /// <exception cref="InvalidOperationException">When the response is not a success (the HTTP status is
    /// carried in the exception message), or when the client has no local credentials configured and none
    /// are passed (a programming error).</exception>
    Task<MediaUploadResult> UploadAsync(
        Iri actorId,
        byte[] content,
        string contentType,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Uploads a media file with explicit Basic-auth credentials.
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor uploading the media.</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="contentType">The file's <c>Content-Type</c>.</param>
    /// <param name="fileName">The file's original name.</param>
    /// <param name="credentials">The acting actor's Basic-auth credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The upload result (the same-origin media IRI, content-type, and file name).</returns>
    /// <exception cref="InvalidOperationException">When the response is not a success.</exception>
    Task<MediaUploadResult> UploadAsync(
        Iri actorId,
        byte[] content,
        string contentType,
        string fileName,
        ProxyCredentials credentials,
        CancellationToken ct = default);
}
