namespace Iris.Client;

/// <summary>
/// Route constants for the **media** surface (Phase 20.4 (a)): uploading a note's attachment and
/// serving it back from the same origin.
/// </summary>
/// <remarks>
/// Two routes, on two trees, mirroring the mute/relay split:
/// <list type="bullet">
///   <item>
///     <description><strong>Upload (write):</strong> <c>POST {LocalModerationConstants.LocalRoutePrefix}
///     /u/{handle}/{UploadSegment}</c> — an owner-only, Basic-authenticated multipart POST of the file's
///     bytes. Not an ActivityStreams activity (it is a local, non-federated write), so it is on the
///     non-AP <c>/local/v1</c> tree, not <c>/ap/v1</c>.</description>
///   </item>
///   <item>
///     <description><strong>Serve (read):</strong> <c>GET {ServeRoutePrefix}/{ServeSegment}/{id}</c> — a
///     public, same-origin, long-cacheable GET of the stored bytes. It is an ordinary resource read (the
///     browser's <c>&lt;img&gt;</c>/<c>&lt;a&gt;</c> loads it), so it is on the AP tree
///     (<c>/ap/v1</c>, Resolved Decision #10: every endpoint under the versioned prefix).</description>
///   </item>
/// </list>
/// The server mints the same-origin media IRI (<c>{base}{ServeRoutePrefix}/{ServeSegment}/{id}</c>) on
/// upload and returns it to the uploader, who sets it as the <c>url</c> of the note's
/// <c>Image</c>/<c>Document</c> attachment.
/// </remarks>
public static class MediaConstants
{
    /// <summary>
    /// The route segment for the media upload write under an actor's local path
    /// (<c>{LocalModerationConstants.LocalRoutePrefix}/u/{handle}/media</c>).
    /// </summary>
    public const string UploadSegment = "media";

    /// <summary>
    /// The route prefix for the media serve tree: <c>/ap/v1</c> (the same versioned prefix as the AP
    /// endpoints — Resolved Decision #10). The serve route is <c>{ServeRoutePrefix}/{ServeSegment}/{id}</c>.
    /// </summary>
    public const string ServeRoutePrefix = "/ap/v1";

    /// <summary>
    /// The route segment for the media serve read (<c>{ServeRoutePrefix}/media/{id}</c>).
    /// </summary>
    public const string ServeSegment = "media";
}
